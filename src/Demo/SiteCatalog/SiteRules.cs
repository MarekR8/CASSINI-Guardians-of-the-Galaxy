namespace Cassini.Demo.SiteCatalog;

internal sealed record SitePlace(string? GraphNodeId, double? Longitude, double? Latitude, long? NodeId = null);
internal sealed record SiteWindow(DateTimeOffset? OpensAt, DateTimeOffset? ClosesAt);
internal sealed record SiteEvidence(string? Source, string? Mode, string? Verification,
    DateTimeOffset? ObservedAt, DateTimeOffset? ValidUntil, int? SituationRevision);

internal sealed class SiteCatalogError(int status, string code, string message) : Exception(message)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}

internal static class SiteRules
{
    internal static bool IsId(string? value) => value is { Length: > 0 and <= 120 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    internal static void Validate(SitePlace? place, SiteWindow? window, SiteEvidence? evidence, string? status)
    {
        if (status is not ("draft" or "open" or "suspended" or "closed"))
            throw Invalid("Specify draft, open, suspended or closed.");

        if (place?.Longitude is not { } longitude || !double.IsFinite(longitude) || longitude is < -180 or > 180 ||
            place.Latitude is not { } latitude || !double.IsFinite(latitude) || latitude is < -90 or > 90 ||
            (place.GraphNodeId is not null && !IsId(place.GraphNodeId)) || place.NodeId is <= 0)
            throw Invalid("Specify valid WGS 84 coordinates and valid optional graph node references.");

        if (window?.OpensAt is not { } opensAt || window.ClosesAt <= opensAt)
            throw Invalid("Specify opensAt and, when present, a later closesAt.");

        if (evidence?.Source?.Trim() is not { Length: > 0 and <= 160 } ||
            evidence.Mode is not ("synthetic" or "recorded" or "client") ||
            evidence.Verification is not ("verified" or "unverified") ||
            evidence.ObservedAt is not { } observedAt || evidence.ValidUntil <= observedAt ||
            evidence.SituationRevision is < 0 ||
            (evidence.Verification == "verified" &&
                (evidence.SituationRevision is null || evidence.ValidUntil is null)))
            throw Invalid("Specify evidence provenance and time. Verified evidence requires a situation revision and an expiry.");
    }

    internal static void RequireOpenEvidence(string status, SiteEvidence evidence, int situationRevision)
    {
        if (status != "open")
            return;
        if (evidence.Verification != "verified")
            throw new SiteCatalogError(422, "UnverifiedEvidence", "Opening the site requires verified evidence.");
        if (evidence.SituationRevision != situationRevision)
            throw new SiteCatalogError(409, "StaleSituationEvidence", "Verify the site against the latest situation revision.");
    }

    internal static string? IneligibleReason(string status, SiteWindow window, SiteEvidence evidence,
        DateTimeOffset at, int? situationRevision)
    {
        if (status != "open")
            return status switch { "closed" => "SiteClosed", "suspended" => "SiteSuspended", _ => "SiteDraft" };
        if (window.OpensAt is not { } starts || at < starts || at >= window.ClosesAt)
            return "OutsideOperatingWindow";
        if (evidence.Verification != "verified")
            return "UnverifiedEvidence";
        if (situationRevision is null)
            return "SituationUnavailable";
        if (evidence.SituationRevision != situationRevision)
            return "StaleSituationEvidence";
        if (evidence.ObservedAt is not { } observed || evidence.ValidUntil is not { } expires ||
            at < observed || at >= expires)
            return "EvidenceOutsideValidity";
        return null;
    }

    private static SiteCatalogError Invalid(string message) => new(400, "InvalidSite", message);
}
