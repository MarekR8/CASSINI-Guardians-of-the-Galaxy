using System.Security.Cryptography;
using System.Text.Json;
using Cassini.Demo.SiteCatalog;
using Cassini.Messaging;

namespace Cassini.Sheltering.Allocation;

internal sealed record OutboxEntry(long Sequence, IntegrationEvent Event);

internal sealed record AllocationReceipt(string Fingerprint, JsonElement Result);

internal sealed record AllocationState(
    int Revision, Dictionary<string, ShelterAssignment> Assignments, Dictionary<string, AllocationReceipt> Receipts,
    OutboxEntry[] Outbox)
{
    internal static AllocationState Empty() => new(0, new(StringComparer.Ordinal), new(StringComparer.Ordinal), []);

    internal ShelterAssignment? Find(AssignmentRef reference) => Assignments.GetValueOrDefault(reference.Key);

    internal IEnumerable<ShelterAssignment> HeldAt(string shelterId) =>
        Assignments.Values.Where(assignment => assignment.HoldsAt(shelterId));

    internal PlaceNeeds HeldResources(string shelterId) =>
        HeldAt(shelterId).Aggregate(PlaceNeeds.Zero, (sum, assignment) => sum.Add(assignment.Reservation!.Resources));
}

/// <summary>The assignments to write and the events to append to the outbox in the same commit.</summary>
internal sealed record AllocationChange(IReadOnlyList<ShelterAssignment> Assignments, IReadOnlyList<IntegrationEvent> Events);

/// <summary>
/// Restart-safe, single-writer store for assignments, holds, command receipts and outbox entries.
/// Every command commits one new state file, so a booking or transfer is either written in full or not at all.
/// The same gate also serializes shelter configuration, so capacity and status cannot change mid-decision.
/// </summary>
internal sealed class AllocationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string? path;
    private AllocationState state;

    internal AllocationStore(string? path)
    {
        this.path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        state = this.path is not null && File.Exists(this.path)
            ? JsonSerializer.Deserialize<AllocationState>(File.ReadAllBytes(this.path), Json)
                ?? throw new InvalidDataException("The allocation state is empty.")
            : AllocationState.Empty();
        state = state with
        {
            Assignments = new(state.Assignments, StringComparer.Ordinal),
            Receipts = new(state.Receipts, StringComparer.Ordinal),
        };
    }

    internal AllocationState Read()
    {
        lock (gate)
            return state;
    }

    internal T Exclusive<T>(Func<T> action)
    {
        lock (gate)
            return action();
    }

    /// <summary>Returns the original result of a completed command without consulting other services.</summary>
    internal bool TryReplay<T>(string commandId, object command, out T result)
    {
        var fingerprint = Fingerprint(command);
        lock (gate)
        {
            if (!state.Receipts.TryGetValue(commandId, out var receipt))
            {
                result = default!;
                return false;
            }
            result = Restore<T>(receipt, fingerprint);
            return true;
        }
    }

    internal T Execute<T>(string commandId, object command, Func<AllocationState, (AllocationChange Change, Func<int, T> Result)> decide)
    {
        var fingerprint = Fingerprint(command);
        lock (gate)
        {
            if (state.Receipts.TryGetValue(commandId, out var receipt))
                return Restore<T>(receipt, fingerprint);

            var (change, build) = decide(state);
            // A command that returns an existing record stores its receipt without a new revision.
            var revision = change.Assignments.Count == 0 && change.Events.Count == 0 ? state.Revision : checked(state.Revision + 1);
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
            var next = new AllocationState(revision, assignments, receipts, outbox);

            if (path is not null)
                Persist(path, next);
            state = next;
            return result;
        }
    }

    private static T Restore<T>(AllocationReceipt receipt, string fingerprint)
    {
        if (receipt.Fingerprint != fingerprint)
            throw new SiteCatalogError(409, "CommandConflict", "This commandId was used with different inputs.");
        return receipt.Result.Deserialize<T>(Json)!;
    }

    private static string Fingerprint(object command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, command.GetType(), Json)));

    private static void Persist(string path, AllocationState next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, next, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
