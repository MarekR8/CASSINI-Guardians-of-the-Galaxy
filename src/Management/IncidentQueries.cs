namespace Cassini.Management;

/// <summary>Counters for the coordinator. The zone estimate comes from Situation (task 1) and may be unavailable.</summary>
internal sealed record IncidentCounters(
    PopulationEstimate? InZoneEstimate, string InZoneStatus, int ConfirmedOutside, int ConfirmedOutsideSimulated,
    int Unconfirmed, int? EstimatedWithoutConfirmedExit);

internal sealed record IncidentReadModel(
    string IncidentId, HazardAlert[] Alerts, EvacuationCase[] Cases, IncidentCounters Counters);

internal sealed class IncidentQueries(ManagementStore store, ISituationPopulation situation)
{
    public async Task<IncidentReadModel> GetIncident(CancellationToken cancellation)
    {
        var incident = store.Read().Incident;
        var estimate = await situation.ReadEstimate(cancellation);
        return new(incident.IncidentId, incident.Alerts, incident.Cases, Count(incident.Cases, estimate));
    }

    public EvacuationCase? GetCase(string id) =>
        store.Read().Incident.Cases.FirstOrDefault(item => item.CaseId == id);

    internal static IncidentCounters Count(EvacuationCase[] cases, PopulationEstimate? estimate)
    {
        var confirmed = cases.Where(item => item.ConfirmedOutside).ToArray();
        return new(
            estimate,
            estimate is null ? "unavailable" : "available",
            confirmed.Length,
            confirmed.Count(item => item.Exit!.Mode != "measured"),
            cases.Length - confirmed.Length,
            estimate is null ? null : Math.Max(estimate.Value - confirmed.Length, 0));
    }
}
