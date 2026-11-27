using System.Text.Json;

namespace Situation;

internal sealed record HazardLayer(string Id, string Source, string? ProductId, DateTimeOffset ObservedAt, JsonElement Geometry);

internal sealed record SituationSnapshot(
    int Revision, int Stage, string[] ClosedSegments, HazardLayer[] Hazards, Dictionary<string, int> SegmentRevisions);

internal sealed class SituationError(int status, string code, string message) : Exception(message)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}

internal sealed class SituationStore
{
    private sealed record Receipt(string Fingerprint, SituationSnapshot Result);
    private sealed record State(SituationSnapshot Current, Dictionary<string, Receipt> Receipts);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string? file;
    private State state;

    internal SituationStore(string? stateFile)
    {
        file = string.IsNullOrWhiteSpace(stateFile) ? null : Path.GetFullPath(stateFile);
        state = file is not null && File.Exists(file)
            ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(file), Json)
                ?? throw new InvalidDataException("Situation state is empty.")
            : new State(InitialSnapshot(), new Dictionary<string, Receipt>());
    }

    internal SituationSnapshot Read()
    {
        lock (gate)
            return state.Current;
    }

    internal SituationSnapshot Apply(
        string commandId, int expectedRevision, string fingerprint, Func<SituationSnapshot, SituationSnapshot> apply)
    {
        lock (gate)
        {
            if (state.Receipts.TryGetValue(commandId, out var receipt))
            {
                if (receipt.Fingerprint != fingerprint)
                    throw new SituationError(409, "CommandConflict", "This command ID was already used with different inputs.");
                return receipt.Result;
            }

            if (expectedRevision != state.Current.Revision)
                throw new SituationError(409, "RevisionConflict", "Read the latest situation before applying this command.");

            var snapshot = apply(state.Current);
            var next = new State(snapshot, new Dictionary<string, Receipt>(state.Receipts)
            {
                [commandId] = new Receipt(fingerprint, snapshot)
            });
            if (file is not null)
                Save(file, next);
            state = next;
            return snapshot;
        }
    }

    private static void Save(string path, State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        using (var stream = File.Create(temporaryPath))
        {
            JsonSerializer.Serialize(stream, state, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static SituationSnapshot InitialSnapshot()
    {
        var syntheticFlood = JsonSerializer.SerializeToElement(new
        {
            type = "Polygon",
            coordinates = new[]
            {
                new[]
                {
                    new[] { 16.6470, 50.4320 }, new[] { 16.6570, 50.4320 },
                    new[] { 16.6570, 50.4380 }, new[] { 16.6470, 50.4380 },
                    new[] { 16.6470, 50.4320 }
                }
            }
        });
        return new SituationSnapshot(0, 0, [],
        [
            new HazardLayer("flood-demo", "synthetic", null, new DateTimeOffset(2026, 11, 27, 19, 20, 0, TimeSpan.Zero),
                syntheticFlood)
        ], []);
    }
}

internal sealed class SituationQueries(SituationStore store)
{
    internal SituationSnapshot Snapshot() => store.Read();
}
