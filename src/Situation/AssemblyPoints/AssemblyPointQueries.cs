using Cassini.Demo.SiteCatalog;

namespace Situation.AssemblyPoints;

internal sealed record AssemblyPointView(string IncidentId, string PointId, string Name, SitePlace Place,
    bool StepFree, string Status, SiteWindow OperatingWindow, SiteEvidence Evidence, int Version,
    string? IneligibleReason)
{
    public bool Eligible => IneligibleReason is null;
}

internal sealed record AssemblyPointsView(int Revision, int SituationRevision, DateTimeOffset At,
    AssemblyPointView[] AssemblyPoints);

internal sealed class AssemblyPointQueries(CatalogStore<AssemblyPoint> store, SituationStore situation,
    TimeProvider clock)
{
    internal AssemblyPointsView GetAssemblyPoints(string? incidentId, DateTimeOffset? at, bool eligibleOnly)
    {
        if (!SiteRules.IsId(incidentId))
            throw new SiteCatalogError(400, "InvalidIncident", "Specify a valid incidentId.");

        var instant = (at ?? clock.GetUtcNow()).ToUniversalTime();
        var (revision, points) = store.Read();
        var situationRevision = situation.Read().Revision;
        var result = points
            .Where(point => point.IncidentId == incidentId)
            .OrderBy(point => point.PointId, StringComparer.Ordinal)
            .Select(point => new AssemblyPointView(point.IncidentId, point.PointId, point.Name, point.Place,
                point.StepFree, point.Status, point.OperatingWindow, point.Evidence, point.Version,
                SiteRules.IneligibleReason(point.Status, point.OperatingWindow, point.Evidence, instant, situationRevision)))
            .Where(point => !eligibleOnly || point.Eligible)
            .ToArray();

        return new AssemblyPointsView(revision, situationRevision, instant, result);
    }
}
