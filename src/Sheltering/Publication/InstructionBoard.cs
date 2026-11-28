using Cassini.Demo.SiteCatalog;
using Cassini.Messaging;
using Cassini.Sheltering.Allocation;
using Cassini.Sheltering.Infrastructure;

namespace Cassini.Sheltering.Publication;

internal sealed record AssignmentFacts(int Version, string Status, string? ReservationId);

/// <summary>Authoritative reads used when events are missing, for example after a restart or a lost message.</summary>
internal interface IInstructionAuthority
{
    AssignmentFacts? Assignment(string key);

    Task<RouteInstructionView> Route(string key, CancellationToken cancellation);
}

internal sealed class OwnerInstructionAuthority(AllocationStore store, Func<RoutingClient> routing) : IInstructionAuthority
{
    public AssignmentFacts? Assignment(string key) => store.Read().State.Assignments.GetValueOrDefault(key) is { } item
        ? new(item.Version, item.Status, item.Reservation?.ReservationId) : null;

    public Task<RouteInstructionView> Route(string key, CancellationToken cancellation) =>
        routing().GetInstruction(key, cancellation);
}

/// <summary>One leg as the committed events report it: the assignment version and the instruction aggregate version.</summary>
internal sealed record BoardEntry(
    string Key, int AssignmentVersion, string? Status, string? ReservationId, int InstructionVersion, int? RouteVersion,
    string? RoutedReservationId, int WithdrawnRouteVersion)
{
    internal string State => InstructionStates.Classify(Status, ReservationId, RoutedReservationId, WithdrawnRouteVersion > 0);
}

internal sealed record RouteEventSubject(string Kind, string Id);
internal sealed record RouteEventAssignment(string? ReservationId);
internal sealed record RouteEventPayload(RouteEventSubject Subject, int RouteVersion, RouteEventAssignment? Assignment);

/// <summary>
/// The coordinator's SH-W5 instruction board, fed from <c>evacuation.events</c> (SH-T06 Stage B). It counts only
/// committed facts: events from the owners' outboxes or authoritative owner reads, never a command response.
/// A duplicate event ID changes nothing; an older subject version is ignored; distinct events at the same version
/// are applied by route version, so a late withdrawal of version 1 cannot retire version 2. A gap in versions
/// is filled from the owner instead of guessing.
/// </summary>
internal sealed class InstructionBoard(IInstructionAuthority authority) : IEventConsumer
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, BoardEntry> entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);

    public string Queue => "sheltering.events";

    public IReadOnlyCollection<string> Bindings { get; } = [EventTypes.RoutingKey(EventTypes.ShelterAssignmentChanged),
        EventTypes.RoutingKey(EventTypes.RoutePublished), EventTypes.RoutingKey(EventTypes.RouteWithdrawn)];

    public async Task HandleAsync(IntegrationEvent message, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            if (seen.Contains(message.EventId))
                return;
            if (await Apply(message, cancellation) is { } next)
                entries[next.Key] = next;
            seen.Add(message.EventId);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Lists the board for an incident; legs not yet seen in events are read once from the owners.</summary>
    internal async Task<object> Read(string? incidentId, IEnumerable<string> keys, CancellationToken cancellation)
    {
        if (!SiteRules.IsId(incidentId))
            throw new SiteCatalogError(400, "InvalidIncident", "Specify a valid incidentId.");
        await gate.WaitAsync(cancellation);
        try
        {
            var unverified = 0;
            foreach (var key in keys.Where(key => !entries.ContainsKey(key)))
            {
                try
                {
                    entries[key] = WithRoute(WithAssignment(Empty(key)), await authority.Route(key, cancellation));
                }
                catch (SiteCatalogError error) when (error.Status == 503)
                {
                    unverified++;
                }
            }
            var rows = entries.Values.Where(entry => entry.Key.StartsWith(incidentId + "/", StringComparison.Ordinal))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new
                {
                    entry.Key, entry.AssignmentVersion, entry.Status, entry.ReservationId, entry.InstructionVersion,
                    entry.RouteVersion, entry.RoutedReservationId, entry.State,
                }).ToArray();
            var counts = rows.GroupBy(row => row.State).ToDictionary(group => group.Key, group => group.Count());
            return new { incidentId, entries = rows, counts, unverified };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<BoardEntry?> Apply(IntegrationEvent message, CancellationToken cancellation)
    {
        switch (message.Type)
        {
            case EventTypes.ShelterAssignmentChanged:
            {
                var payload = message.ReadPayload<ShelterAssignmentChangedPayload>();
                var entry = Entry($"{payload.IncidentId}/{payload.CaseId}/{payload.LegId}");
                if (payload.AssignmentVersion <= entry.AssignmentVersion)
                    return null;
                return payload.AssignmentVersion == entry.AssignmentVersion + 1
                    ? entry with { AssignmentVersion = payload.AssignmentVersion, Status = payload.Status, ReservationId = payload.ReservationId }
                    : WithAssignment(entry);
            }
            case EventTypes.RoutePublished or EventTypes.RouteWithdrawn:
            {
                var payload = message.ReadPayload<RouteEventPayload>();
                if (payload.Subject.Kind != "shelter-assignment")
                    return null;
                var entry = Entry(payload.Subject.Id);
                var version = checked((int)message.SubjectVersion);
                if (version < entry.InstructionVersion)
                    return null;
                if (version > entry.InstructionVersion + 1)
                    return WithRoute(entry, await authority.Route(entry.Key, cancellation));
                entry = entry with { InstructionVersion = version };
                if (message.Type == EventTypes.RoutePublished)
                    return payload.RouteVersion > (entry.RouteVersion ?? 0) && payload.RouteVersion > entry.WithdrawnRouteVersion
                        ? entry with { RouteVersion = payload.RouteVersion, RoutedReservationId = payload.Assignment?.ReservationId }
                        : entry;
                var withdrawn = entry with { WithdrawnRouteVersion = Math.Max(entry.WithdrawnRouteVersion, payload.RouteVersion) };
                return entry.RouteVersion == payload.RouteVersion
                    ? withdrawn with { RouteVersion = null, RoutedReservationId = null } : withdrawn;
            }
            default:
                return null; // Site, admission and group facts do not change an instruction.
        }
    }

    private BoardEntry Entry(string key) => entries.GetValueOrDefault(key) ?? Empty(key);

    private static BoardEntry Empty(string key) => new(key, 0, null, null, 0, null, null, 0);

    private BoardEntry WithAssignment(BoardEntry entry) => authority.Assignment(entry.Key) is { } facts
        ? entry with { AssignmentVersion = facts.Version, Status = facts.Status, ReservationId = facts.ReservationId }
        : entry;

    private static BoardEntry WithRoute(BoardEntry entry, RouteInstructionView route) => entry with
    {
        InstructionVersion = route.AggregateVersion, RouteVersion = route.Current?.RouteVersion,
        RoutedReservationId = route.Current?.Assignment.ReservationId, WithdrawnRouteVersion = route.LastWithdrawal?.RouteVersion ?? 0,
    };
}
