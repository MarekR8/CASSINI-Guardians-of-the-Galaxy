using Cassini.Demo.SiteCatalog;

namespace Situation.AssemblyPoints;

internal sealed record AssemblyPoint(string IncidentId, string PointId, string Name, SitePlace Place,
    bool StepFree, string Status, SiteWindow OperatingWindow, SiteEvidence Evidence, int Version)
{
    internal static string Key(string? incidentId, string? pointId) => $"{incidentId}/{pointId}";

    internal static string ValidateIdentity(string? incidentId, string? pointId, string? name)
    {
        if (!SiteRules.IsId(incidentId) || !SiteRules.IsId(pointId) || name?.Trim() is not { Length: > 0 and <= 120 } validName)
            throw new SiteCatalogError(400, "InvalidAssemblyPoint", "Specify an incidentId, pointId and name.");

        return validName;
    }
}
