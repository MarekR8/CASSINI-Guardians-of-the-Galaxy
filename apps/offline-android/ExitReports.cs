using System.Text.Json;

namespace Cassini.Offline;

internal sealed record GnssPosition(double Latitude, double Longitude, double AccuracyMeters,
    DateTimeOffset ObservedAt, bool GalileoUsed)
{
    private const long Second = 1_000_000_000;

    internal static bool IsFresh(long observed, long now) => observed > 0 && now >= observed && now - observed <= 10 * Second;

    internal static bool IsFreshUtc(DateTimeOffset observed, DateTimeOffset now) =>
        now >= observed && now - observed <= TimeSpan.FromSeconds(10);

    internal static bool MatchesGalileoStatus(long observed, long statusObserved, bool galileoUsed) =>
        galileoUsed && statusObserved > 0 && Math.Abs(observed - statusObserved) <= Second;
}

internal sealed record PositionEvidence(bool GalileoUsed, string Osnma, string Egnos);

internal sealed record ExitReport(Guid CommandId, string Step, DateTimeOffset ObservedAt, double Latitude,
    double Longitude, double Accuracy, PositionEvidence Evidence, string Mode)
{
    internal static ExitReport Create(GnssPosition position, bool simulated) =>
        new(Guid.NewGuid(), "exit-confirmed", position.ObservedAt.ToUniversalTime(), position.Latitude,
            position.Longitude, position.AccuracyMeters, new PositionEvidence(position.GalileoUsed, "simulated", "unknown"),
            simulated ? "simulated" : "measured");
}

internal sealed class LocalExitReports(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal void Save(ExitReport report)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, report.CommandId.ToString("D") + ".json");
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(report, Json));
        File.Move(temporaryPath, path, overwrite: true);
    }

    internal string[] PendingFiles() => Directory.Exists(directory)
        ? Directory.GetFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
        : [];
}
