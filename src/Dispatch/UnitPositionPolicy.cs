using System.Text.Json;

namespace Cassini.Dispatch;

internal sealed record PositionEvidence(string Source, bool? EgnosApplied, double? Hpl, string Mode);
internal sealed record UnitPositionFixture(string UnitId, string Node, PositionEvidence Evidence);

internal sealed class UnitPositionPolicy
{
    private static readonly PositionEvidence Unknown = new("unknown", null, null, "unknown");
    private readonly Dictionary<(string UnitId, string Node), PositionEvidence> positions;
    private readonly double maxHpl;

    internal UnitPositionPolicy(string fixture, double maxHpl)
    {
        if (!double.IsFinite(maxHpl) || maxHpl <= 0)
            throw new ArgumentException("The horizontal protection level limit must be positive.", nameof(maxHpl));

        using var file = File.OpenRead(fixture);
        var entries = JsonSerializer.Deserialize<UnitPositionFixture[]>(file, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The exercise position fixture is empty.");
        if (entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.UnitId) ||
            string.IsNullOrWhiteSpace(entry.Node) || entry.Evidence is not { Mode: "synthetic" } ||
            string.IsNullOrWhiteSpace(entry.Evidence.Source) ||
            entry.Evidence.Hpl is { } hpl && (!double.IsFinite(hpl) || hpl <= 0)))
            throw new InvalidDataException("The exercise position fixture requires labeled synthetic evidence.");
        positions = entries.ToDictionary(entry => (entry.UnitId, entry.Node), entry => entry.Evidence);
        this.maxHpl = maxHpl;
    }

    internal PositionEvidence Read(string unitId, string node) => positions.GetValueOrDefault((unitId, node), Unknown);

    internal bool IsVerified(PositionEvidence evidence) =>
        evidence.EgnosApplied == true && evidence.Hpl is { } hpl && double.IsFinite(hpl) && hpl > 0 && hpl <= maxHpl;

    internal PositionEvidence? Confirm(string unitId, string node, string? confirmedStartNode,
        PositionEvidence? previous = null)
    {
        if (confirmedStartNode is not null)
            return confirmedStartNode == node ? new("dispatcher", null, null, "manual") : null;
        var evidence = previous ?? Read(unitId, node);
        return evidence.Mode == "manual" || IsVerified(evidence) ? evidence : null;
    }
}
