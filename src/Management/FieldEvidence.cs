using System.Text.Json.Serialization;

namespace Cassini.Management;

/// <summary>What the field device reports about the position fix. The server never upgrades these values.</summary>
internal sealed record PositionEvidence(bool? GalileoUsed, string? Osnma, string? Egnos);

/// <summary>A position fix with its evidence, attached to an exit confirmation or a pickup.</summary>
internal sealed record PositionFix(
    DateTimeOffset ObservedAt, double Latitude, double Longitude, double Accuracy,
    PositionEvidence Evidence, string Mode, string CommandId);

/// <summary>Validates the W2 position fields (<c>contracts/management/progress-command.schema.json</c>).</summary>
internal static class FieldEvidence
{
    internal const double MaxAccuracyMeters = 100;
    internal static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(2);
    internal static readonly string[] OsnmaValues = ["simulated", "verified", "none"];
    internal static readonly string[] EgnosValues = ["applied", "unknown"];
    internal static readonly string[] ModeValues = ["measured", "simulated", "replay"];

    /// <summary>True when the command carries any position field, so the caller intends to attach a fix.</summary>
    internal static bool HasFix(RecordProgress command) =>
        command.Latitude is not null || command.Longitude is not null || command.Accuracy is not null ||
        command.Evidence is not null || command.Mode is not null;

    internal static (PositionFix? Fix, string? Error) Read(RecordProgress command, DateTimeOffset now)
    {
        if (command.Latitude is not { } latitude || command.Longitude is not { } longitude ||
            !double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180)
            return (null, "Provide latitude and longitude in WGS84 degrees.");
        if (command.Accuracy is not { } accuracy || !double.IsFinite(accuracy) ||
            accuracy is <= 0 or > MaxAccuracyMeters)
            return (null, $"Provide the reported horizontal accuracy in meters, at most {MaxAccuracyMeters}.");
        if (command.ObservedAt is not { } observedAt)
            return (null, "Provide the time of the position fix.");
        if (observedAt > now + AllowedClockSkew)
            return (null, "The position fix is timestamped in the future.");
        if (command.Evidence is not { GalileoUsed: { } galileoUsed } evidence ||
            !OsnmaValues.Contains(evidence.Osnma) || !EgnosValues.Contains(evidence.Egnos))
            return (null, "Provide evidence with galileoUsed, osnma (simulated|verified|none) and egnos (applied|unknown).");
        if (!ModeValues.Contains(command.Mode))
            return (null, "Provide mode: measured, simulated or replay.");

        return (new PositionFix(observedAt.ToUniversalTime(), latitude, longitude, accuracy,
            new PositionEvidence(galileoUsed, evidence.Osnma, evidence.Egnos), command.Mode!, command.CommandId!), null);
    }
}

/// <summary>
/// <c>POST /commands/progress</c>. The position fields are omitted from the command identity when absent,
/// so receipts recorded before W2 still match an exact retry.
/// </summary>
internal sealed record RecordProgress(
    string? CommandId, string? CaseId, string? Step, int? ExpectedVersion,
    string? Node, DateTimeOffset? ObservedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Latitude = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Longitude = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Accuracy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PositionEvidence? Evidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mode = null);
