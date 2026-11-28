using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cassini.Demo.SiteCatalog;
using Cassini.Messaging;
using Cassini.Routing.Destinations;

namespace Cassini.Routing.Publication;

internal sealed record RouteSubject(string? Kind, string? Id)
{
    internal const string ShelterAssignment = "shelter-assignment";
    internal string Key => $"{Kind}/{Id}";
}

/// <summary>The Sheltering assignment and hold that the instruction guides a person to (SH-W4).</summary>
internal sealed record ShelterAssignmentRef(
    string? IncidentId, string? CaseId, string? LegId, int? AssignmentVersion, string? ReservationId, string? ShelterId);

/// <summary>The inputs of the <c>destination-route</c> proposal that the publisher saw, repeated at publication.</summary>
internal sealed record ProposalQuery(
    string? Mode, DateTimeOffset? At, string? Node, string? CaseIds, DateTimeOffset? ClearBy, bool? StepFree,
    string? GroupId, int? GroupRevision, string? TransportId, string? RecordedRouteId);

internal sealed record RouteProposal(string? EvidenceId, ProposalQuery? Query);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PublishRouteCommand(
    string? CommandId, string? CorrelationId, RouteSubject? Subject, int? ExpectedVersion, ShelterAssignmentRef? Assignment,
    RouteProposal? Route);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WithdrawRouteCommand(
    string? CommandId, string? CorrelationId, RouteSubject? Subject, int? ExpectedVersion, int? RouteVersion,
    string? ReasonCode, string? EvidenceRef);

internal sealed record CurrentRoute(
    int RouteVersion, string EvidenceId, DestinationRef Destination, ShelterAssignmentRef Assignment, long SituationRevision,
    ArrivalWindow ArrivalWindow, DateTimeOffset ValidUntil, DateTimeOffset PublishedAt, string CommandId);

internal sealed record RouteWithdrawal(
    int RouteVersion, string ReasonCode, string? EvidenceRef, string? ReservationId, DateTimeOffset WithdrawnAt, string CommandId);

/// <summary>
/// The instruction aggregate of one route subject. <c>AggregateVersion</c> is the concurrency token; the displayed
/// <c>RouteVersion</c> grows with every publication and is never reused, so a stale withdrawal cannot retire a newer route.
/// </summary>
internal sealed record RouteInstruction(string Kind, string Id, int AggregateVersion, CurrentRoute? Current, RouteWithdrawal? LastWithdrawal)
{
    internal int LastRouteVersion => Math.Max(Current?.RouteVersion ?? 0, LastWithdrawal?.RouteVersion ?? 0);
}

internal sealed record InstructionReceipt(string Fingerprint, RouteInstruction Result);
internal sealed record InstructionOutboxEntry(long Sequence, IntegrationEvent Event, bool Published);

/// <summary>Receipts and the outbox, committed in the same write as the instruction they belong to.</summary>
internal sealed record InstructionJournal(Dictionary<string, InstructionReceipt> Receipts, InstructionOutboxEntry[] Outbox)
{
    internal static InstructionJournal Empty() => new(new(StringComparer.Ordinal), []);
}

internal sealed record RoutePublishedPayload(
    RouteSubject Subject, int RouteVersion, DestinationRef Destination, ShelterAssignmentRef Assignment, string EvidenceId,
    long SituationRevision, ArrivalWindow ArrivalWindow, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil, string RoutePath);

internal sealed record RouteWithdrawnPayload(
    RouteSubject Subject, int RouteVersion, string ReasonCode, string? EvidenceRef, string? ReservationId);

/// <summary>
/// Publication and withdrawal of shelter guidance (SH-T06). Publishing repeats the <c>destination-route</c> check with
/// the proposal's evidence ID, so an instruction is issued only while that exact evidence is still current. A failure
/// commits nothing; the booking in Sheltering stays visibly pending.
/// </summary>
internal sealed class RouteInstructionStore(CatalogStore<RouteInstruction> store, TimeProvider clock) : IOutboxStore
{
    private const string Producer = "routing";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string Key(RouteInstruction instruction) => $"{instruction.Kind}/{instruction.Id}";

    /// <summary>A subject without an instruction reads as version 0, which the first publication expects.</summary>
    internal RouteInstruction Get(RouteSubject? subject)
    {
        var valid = RequireSubject(subject);
        return Read().Items.GetValueOrDefault(valid.Key) ?? new(valid.Kind!, valid.Id!, 0, null, null);
    }

    internal InstructionOutboxEntry[] Outbox(long after) =>
        Read().Journal.Outbox.Where(entry => entry.Sequence > after).ToArray();

    internal async Task<RouteInstruction> Publish(PublishRouteCommand command, DestinationRouteQueries routes,
        DestinationQueries destinations, CancellationToken cancellation)
    {
        var subject = RequireCommand(command.CommandId, command.Subject, command.ExpectedVersion);
        var assignment = command.Assignment;
        if (assignment is null || !Ids(assignment.IncidentId, assignment.CaseId, assignment.LegId, assignment.ReservationId,
                assignment.ShelterId) || assignment.AssignmentVersion is not > 0 ||
            subject.Id != $"{assignment.IncidentId}/{assignment.CaseId}/{assignment.LegId}")
            throw Invalid("InvalidAssignmentRef", "Reference the reserved shelter assignment of this subject.");
        if (command.Route is not { EvidenceId: { Length: 64 } evidenceId, Query: { } query } ||
            !evidenceId.All(char.IsAsciiHexDigitLower))
            throw Invalid("InvalidProposal", "Publish the evidence ID and inputs of a destination-route proposal.");
        if (Replay(command.CommandId!, command) is { } replay)
            return replay;

        // Fails safely: an unavailable owner raises 503 and changed evidence leaves the route ineligible.
        var checkedRoute = await routes.GetRoute(new(assignment.IncidentId, assignment.LegId, query.Node, query.Mode,
                query.StepFree, query.At, query.ClearBy, query.GroupId, query.GroupRevision, query.CaseIds),
            "shelter", assignment.ShelterId, query.RecordedRouteId, evidenceId, query.TransportId, cancellation);
        if (checkedRoute is not { Status: "Eligible", ArrivalWindow: { } arrival, Evidence: { } evidence })
            throw new RoutingException(422, "RouteIneligible",
                $"The proposal is no longer eligible: {checkedRoute.Reason ?? "RouteIneligible"}.");

        var owned = await destinations.ReadAssignment(assignment.IncidentId!, assignment.CaseId!, assignment.LegId!, cancellation);
        if (owned is not { Status: "Reserved", Reservation: { } hold } || owned.Version != assignment.AssignmentVersion ||
            hold.ReservationId != assignment.ReservationId || hold.ShelterId != assignment.ShelterId)
            throw new RoutingException(409, "ReservationChanged", "The published route needs the current Sheltering reservation.");
        if (hold.Route?.Query != query || !hold.Covers(checkedRoute))
            throw new RoutingException(422, "ReservationDoesNotCoverRoute",
                "The current hold must cover the route inputs, arrival window and required resources.");

        var validUntil = evidence.ValidUntil < hold.HoldsExpireAt ? evidence.ValidUntil : hold.HoldsExpireAt;
        return Commit(command.CommandId!, command, subject, command.ExpectedVersion!.Value, (current, version, now) =>
        {
            var route = new CurrentRoute(current.LastRouteVersion + 1, evidenceId, checkedRoute.Destination, assignment,
                checkedRoute.SituationRevision, arrival, validUntil, now, command.CommandId!);
            var events = new List<IntegrationEvent>();
            var withdrawal = current.LastWithdrawal;
            if (current.Current is { } superseded)
            {
                withdrawal = new(superseded.RouteVersion, "Superseded", null, superseded.Assignment.ReservationId, now,
                    command.CommandId!);
                events.Add(Withdrawn(subject, version, withdrawal, command.CorrelationId, command.CommandId!, now));
            }
            events.Add(IntegrationEvent.Create(EventTypes.RoutePublished, Producer, subject.Key, version, now,
                new RoutePublishedPayload(subject, route.RouteVersion, route.Destination, assignment, evidenceId,
                    route.SituationRevision, arrival, now, route.ValidUntil, $"/queries/route-instruction?kind={subject.Kind}&id={subject.Id}"),
                command.CorrelationId, command.CommandId));
            return (current with { AggregateVersion = version, Current = route, LastWithdrawal = withdrawal }, events);
        });
    }

    internal RouteInstruction Withdraw(WithdrawRouteCommand command)
    {
        var subject = RequireCommand(command.CommandId, command.Subject, command.ExpectedVersion);
        if (command.RouteVersion is not > 0 || !SiteRules.IsId(command.ReasonCode) ||
            (command.EvidenceRef is not null && !SiteRules.IsId(command.EvidenceRef)))
            throw Invalid("InvalidWithdrawal", "Specify the route version, a reason code and an optional evidence reference.");
        if (Replay(command.CommandId!, command) is { } replay)
            return replay;

        return Commit(command.CommandId!, command, subject, command.ExpectedVersion!.Value, (current, version, now) =>
        {
            if (current.Current?.RouteVersion != command.RouteVersion)
                throw new RoutingException(409, "StaleRouteVersion", "Only the current route version can be withdrawn.");
            var withdrawal = new RouteWithdrawal(command.RouteVersion.Value, command.ReasonCode!, command.EvidenceRef,
                current.Current.Assignment.ReservationId, now, command.CommandId!);
            return (current with { AggregateVersion = version, Current = null, LastWithdrawal = withdrawal },
                [Withdrawn(subject, version, withdrawal, command.CorrelationId, command.CommandId!, now)]);
        });
    }

    public IReadOnlyList<IntegrationEvent> PendingEvents() =>
        Read().Journal.Outbox.Where(entry => !entry.Published).Select(entry => entry.Event).ToArray();

    public void MarkPublished(IReadOnlyCollection<string> eventIds) =>
        store.Mutate<InstructionJournal, int>((_, journal) =>
        {
            var current = journal ?? InstructionJournal.Empty();
            return ([], current with
            {
                Outbox = current.Outbox.Select(entry => eventIds.Contains(entry.Event.EventId) ? entry with { Published = true } : entry)
                    .ToArray(),
            }, 0);
        });

    private (IReadOnlyDictionary<string, RouteInstruction> Items, InstructionJournal Journal) Read()
    {
        var (_, items, journal) = store.ReadWith<InstructionJournal>();
        return (items, journal ?? InstructionJournal.Empty());
    }

    private RouteInstruction? Replay(string commandId, object command) =>
        Read().Journal.Receipts.GetValueOrDefault(commandId) is { } receipt ? Restore(receipt, command) : null;

    /// <summary>Checks the expected aggregate version and commits the instruction, its receipt and its events together.</summary>
    private RouteInstruction Commit(string commandId, object command, RouteSubject subject, int expected,
        Func<RouteInstruction, int, DateTimeOffset, (RouteInstruction Next, IReadOnlyList<IntegrationEvent> Events)> change) =>
        store.Mutate<InstructionJournal, RouteInstruction>((items, stored) =>
        {
            var journal = stored ?? InstructionJournal.Empty();
            if (journal.Receipts.TryGetValue(commandId, out var receipt))
                return ([], journal, Restore(receipt, command));
            var current = items.GetValueOrDefault(subject.Key) ?? new(subject.Kind!, subject.Id!, 0, null, null);
            if (current.AggregateVersion != expected)
                throw new RoutingException(409, "VersionConflict", "Read the current instruction before deciding again.");

            var (next, events) = change(current, current.AggregateVersion + 1, clock.GetUtcNow());
            var sequence = journal.Outbox.Length == 0 ? 0 : journal.Outbox[^1].Sequence;
            return ([next], journal with
            {
                Receipts = new(journal.Receipts, StringComparer.Ordinal) { [commandId] = new(Fingerprint(command), next) },
                Outbox = [.. journal.Outbox, .. events.Select(message => new InstructionOutboxEntry(++sequence, message, false))],
            }, next);
        });

    private static IntegrationEvent Withdrawn(RouteSubject subject, int version, RouteWithdrawal withdrawal,
        string? correlationId, string commandId, DateTimeOffset now) =>
        IntegrationEvent.Create(EventTypes.RouteWithdrawn, Producer, subject.Key, version, now,
            new RouteWithdrawnPayload(subject, withdrawal.RouteVersion, withdrawal.ReasonCode, withdrawal.EvidenceRef,
                withdrawal.ReservationId), correlationId, commandId);

    private static RouteSubject RequireCommand(string? commandId, RouteSubject? subject, int? expectedVersion)
    {
        if (!SiteRules.IsId(commandId) || expectedVersion is not >= 0)
            throw Invalid("InvalidCommand", "Specify a commandId and the expected instruction version.");
        return RequireSubject(subject);
    }

    private static RouteSubject RequireSubject(RouteSubject? subject)
    {
        if (subject?.Kind != RouteSubject.ShelterAssignment || subject.Id?.Split('/') is not { Length: 3 } parts ||
            !parts.All(SiteRules.IsId))
            throw Invalid("InvalidSubject", "Use a shelter-assignment subject: incidentId/caseId/legId.");
        return subject;
    }

    private static bool Ids(params string?[] values) => values.All(SiteRules.IsId);

    private static RouteInstruction Restore(InstructionReceipt receipt, object command) =>
        receipt.Fingerprint == Fingerprint(command) ? receipt.Result
            : throw new RoutingException(409, "CommandConflict", "This commandId was used with different inputs.");

    private static string Fingerprint(object command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, command.GetType(), Json)));

    private static RoutingException Invalid(string code, string message) => new(400, code, message);
}
