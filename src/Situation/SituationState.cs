using System.Text.Json;
using Cassini.Demo;

namespace Situation;

internal sealed record HazardLayer(string Id, string Source, string? ProductId, DateTimeOffset ObservedAt,
    JsonElement Geometry, string? SourceUrl = null, PopulationEstimate? PopulationEstimate = null)
{
    public HazardProvenance Provenance => new(Source, ProductId is null ? "mock" : "recorded", ObservedAt, ProductId);
}

internal sealed record HazardProvenance(string Source, string Mode, DateTimeOffset ObservedAt, string? ProductId);

internal sealed record SituationSnapshot(
    int Revision, int Stage, string[] ClosedSegments, HazardLayer[] Hazards, Dictionary<string, int> SegmentRevisions,
    ReviewFeature[]? ReviewFeatures = null, Dictionary<string, ClosureEvidence>? ClosureEvidence = null)
{
    public PopulationEstimate? PopulationEstimate =>
        (Hazards.LastOrDefault(layer => layer.ProductId is not null) ?? Hazards.FirstOrDefault())?.PopulationEstimate;
}

internal sealed class SituationError(int status, string code, string message) : Exception(message)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}

internal sealed class SituationStore
{
    private sealed record Receipt(string Fingerprint, SituationSnapshot Result);
    private sealed record State(SituationSnapshot Current, Dictionary<string, Receipt> Receipts, string? Scenario = null);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string? file;
    private State state;

    internal SituationStore(string? stateFile, DemoNetwork? network, IPopulationEstimator population)
    {
        file = string.IsNullOrWhiteSpace(stateFile) ? null : Path.GetFullPath(stateFile);
        state = file is not null && File.Exists(file)
            ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(file), Json)
                ?? throw new InvalidDataException("Situation state is empty.")
            : new State(InitialSnapshot(network), new Dictionary<string, Receipt>(), network is null ? null : "warsaw");
        if ((state.Scenario == "warsaw") != (network is not null))
            throw new InvalidDataException("The situation state belongs to a different scenario. Use a separate state directory.");
        state = state with
        {
            Current = state.Current with
            {
                Hazards = state.Current.Hazards.Select(layer => layer.ProductId is null
                    ? layer with { PopulationEstimate = population.Estimate(layer.Id) }
                    : layer).ToArray()
            }
        };
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
            var next = state with { Current = snapshot, Receipts = new Dictionary<string, Receipt>(state.Receipts)
            {
                [commandId] = new Receipt(fingerprint, snapshot)
            } };
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

    private static SituationSnapshot InitialSnapshot(DemoNetwork? network)
    {
        if (network is not null)
        {
            var exerciseFlood = JsonSerializer.SerializeToElement(new
            {
                type = "Polygon",
                coordinates = new[] { new[] { new[] { 21.0314, 52.2396 }, new[] { 21.0365, 52.2405 },
                    new[] { 21.0389, 52.2421 }, new[] { 21.0383, 52.2438 }, new[] { 21.0350, 52.2442 },
                    new[] { 21.0327, 52.2422 }, new[] { 21.0314, 52.2396 } } }
            });
            return new SituationSnapshot(0, 0, [],
                [new HazardLayer("flood-warsaw-exercise", "synthetic", null,
                    new DateTimeOffset(2026, 11, 27, 19, 20, 0, TimeSpan.Zero), exerciseFlood)],
                [], [ReviewNetwork.ExerciseFeature(network)]);
        }

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
