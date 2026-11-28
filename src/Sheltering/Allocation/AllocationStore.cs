using System.Security.Cryptography;
using System.Text.Json;
using Cassini.Demo.SiteCatalog;
using Cassini.Messaging;
using Cassini.Sheltering.Catalog;
using Cassini.Sheltering.Reception;
using ReceptionHold = Cassini.Sheltering.Reception.ShelterHold;

namespace Cassini.Sheltering.Allocation;

internal sealed record OutboxEntry(long Sequence, IntegrationEvent Event);

internal sealed record AllocationReceipt(string Fingerprint, JsonElement Result);

internal sealed record AllocationState(
    int Revision, Dictionary<string, ShelterAssignment> Assignments, Dictionary<string, AllocationReceipt> Receipts,
    OutboxEntry[] Outbox)
{
    internal static AllocationState Empty() => new(0, new(StringComparer.Ordinal), new(StringComparer.Ordinal), []);

    internal ShelterAssignment? Find(AssignmentRef reference) => Assignments.GetValueOrDefault(reference.Key);
}

/// <summary>A consistent view: shelters (with their reception ledgers) and assignments reconciled against them.</summary>
internal sealed record AllocationView(int CatalogRevision, IReadOnlyDictionary<string, Shelter> Shelters, AllocationState State)
{
    internal ShelterAssignment? Find(AssignmentRef reference) => State.Find(reference);
    internal Shelter? Shelter(string shelterId) => Shelters.GetValueOrDefault(shelterId);
}

/// <summary>The assignments and shelters to write and the events to append to the outbox in the same commit.</summary>
internal sealed record AllocationChange(
    IReadOnlyList<ShelterAssignment> Assignments, IReadOnlyList<Shelter> Shelters, IReadOnlyList<IntegrationEvent> Events);

/// <summary>
/// Assignments, command receipts and the allocation outbox live in the shelter catalog file next to the shelters'
/// reception ledgers (SH-T05). Holds are written into <c>reception.holds</c>, so one commit changes an assignment,
/// the hold, the shelter commitments and the outbox together, and admission converts exactly the hold issued here.
/// </summary>
internal sealed class AllocationStore(CatalogStore<Shelter> catalog)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal AllocationView Read()
    {
        var (revision, shelters, state) = catalog.ReadWith<AllocationState>();
        return View(revision, shelters, state);
    }

    /// <summary>Returns the original result of a completed command without consulting other services.</summary>
    internal bool TryReplay<T>(string commandId, object command, out T result)
    {
        var receipt = Read().State.Receipts.GetValueOrDefault(commandId);
        result = receipt is null ? default! : Restore<T>(receipt, Fingerprint(command));
        return receipt is not null;
    }

    internal T Execute<T>(string commandId, object command, Func<AllocationView, (AllocationChange Change, Func<int, T> Result)> decide)
    {
        var fingerprint = Fingerprint(command);
        return catalog.Mutate<AllocationState, T>((shelters, stored) =>
        {
            var view = View(0, shelters, stored);
            var state = view.State;
            if (state.Receipts.TryGetValue(commandId, out var receipt))
                return ([], state, Restore<T>(receipt, fingerprint));

            var (change, build) = decide(view);
            // A command that returns an existing record stores its receipt without a new revision.
            var revision = change.Assignments.Count == 0 && change.Events.Count == 0 && change.Shelters.Count == 0
                ? state.Revision : checked(state.Revision + 1);
            var assignments = new Dictionary<string, ShelterAssignment>(state.Assignments, StringComparer.Ordinal);
            foreach (var assignment in change.Assignments)
                assignments[assignment.Ref.Key] = assignment;

            var result = build(revision);
            var receipts = new Dictionary<string, AllocationReceipt>(state.Receipts, StringComparer.Ordinal)
            {
                [commandId] = new(fingerprint, JsonSerializer.SerializeToElement(result, Json)),
            };
            var sequence = state.Outbox.Length == 0 ? 0 : state.Outbox[^1].Sequence;
            var outbox = state.Outbox.Concat(change.Events.Select(message => new OutboxEntry(++sequence, message))).ToArray();
            return (change.Shelters.ToArray(), new AllocationState(revision, assignments, receipts, outbox), result);
        });
    }

    private static AllocationView View(int revision, IReadOnlyDictionary<string, Shelter> shelters, AllocationState? stored)
    {
        var state = stored ?? AllocationState.Empty();
        var assignments = new Dictionary<string, ShelterAssignment>(StringComparer.Ordinal);
        foreach (var (key, assignment) in state.Assignments)
            assignments[key] = Reconcile(assignment, shelters);
        return new(revision, shelters, state with { Assignments = assignments, Receipts = new(state.Receipts, StringComparer.Ordinal) });
    }

    /// <summary>
    /// Applies what the reception ledger reports since the assignment was written: an admission makes it
    /// <c>Admitted</c>, a confirmed departure <c>Closed</c>, and a hold released by a walk-in elsewhere leaves it
    /// awaiting replacement. These derived states are never stored, so they always follow the ledger.
    /// </summary>
    private static ShelterAssignment Reconcile(ShelterAssignment assignment, IReadOnlyDictionary<string, Shelter> shelters)
    {
        if (assignment.Status is AssignmentStatus.Closed)
            return assignment;
        if (assignment is { Status: AssignmentStatus.Reserved, Reservation: { } hold } &&
            shelters.GetValueOrDefault(hold.ShelterId)?.Ledger.Holds.Any(item => item.ReservationId == hold.ReservationId) == true)
            return assignment;

        var admission = shelters.Values.SelectMany(shelter => shelter.Ledger.Admissions.Select(item => (shelter.ShelterId, item)))
            .Where(entry => entry.item.IncidentId == assignment.IncidentId && entry.item.CaseId == assignment.CaseId &&
                entry.item.LegId == assignment.LegId)
            .OrderBy(entry => entry.item.ObservedAt).LastOrDefault();
        if (admission.item is { } admitted)
            return assignment with
            {
                Status = admitted.Present ? AssignmentStatus.Admitted : AssignmentStatus.Closed,
                Reason = admitted.Present ? $"AdmittedAt:{admission.ShelterId}" : "DepartureConfirmed",
            };
        return assignment.Status == AssignmentStatus.Reserved
            ? assignment with { Status = AssignmentStatus.AwaitingReplacement, Reason = "HoldReleasedByReception" }
            : assignment;
    }

    private static T Restore<T>(AllocationReceipt receipt, string fingerprint)
    {
        if (receipt.Fingerprint != fingerprint)
            throw new SiteCatalogError(409, "CommandConflict", "This commandId was used with different inputs.");
        return receipt.Result.Deserialize<T>(Json)!;
    }

    private static string Fingerprint(object command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, command.GetType(), Json)));
}

/// <summary>Writes SH-T04 holds into a shelter's reception ledger and its held commitments.</summary>
internal static class ShelterLedger
{
    internal static Shelter AddHold(Shelter shelter, ReceptionHold hold) => shelter with
    {
        Commitments = shelter.Commitments with
        {
            HeldPlaces = shelter.Commitments.HeldPlaces + hold.Resources.Places,
            HeldAccessiblePlaces = shelter.Commitments.HeldAccessiblePlaces + hold.Resources.AccessiblePlaces,
        },
        Reception = shelter.Ledger with { Holds = [.. shelter.Ledger.Holds, hold] },
    };

    /// <summary>Releases an unused hold; occupied places are never touched here.</summary>
    internal static Shelter RemoveHold(Shelter shelter, string reservationId)
    {
        var hold = shelter.Ledger.Holds.FirstOrDefault(item => item.ReservationId == reservationId);
        if (hold is null)
            return shelter;
        return shelter with
        {
            Commitments = shelter.Commitments with
            {
                HeldPlaces = shelter.Commitments.HeldPlaces - hold.Resources.Places,
                HeldAccessiblePlaces = shelter.Commitments.HeldAccessiblePlaces - hold.Resources.AccessiblePlaces,
            },
            Reception = shelter.Ledger with { Holds = shelter.Ledger.Holds.Where(item => item != hold).ToArray() },
        };
    }

    /// <summary>Arrivals expected (holds) or already received (admissions) in the clock hour of <paramref name="at"/>.</summary>
    internal static int ArrivalsInHour(Shelter shelter, DateTimeOffset at)
    {
        var hour = Hour(at);
        return shelter.Ledger.Holds.Where(item => item.ArrivalWindow?.OpensAt is { } opens && Hour(opens) == hour)
                .Sum(item => item.Resources.Places) +
            shelter.Ledger.Admissions.Where(item => Hour(item.ObservedAt) == hour).Sum(item => item.Resources.Places);
    }

    internal static DateTimeOffset Hour(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}
