using Cassini.Demo.SiteCatalog;
using Cassini.Sheltering.Catalog;
using Cassini.Sheltering.Infrastructure;

namespace Cassini.Sheltering.Reception;

internal sealed record PeopleAndPlaces(int People, int Places, int AccessiblePlaces);
internal sealed record PlaceTotals(int Places, int AccessiblePlaces);

/// <summary>
/// Separate reception counters. Awaiting = active holds; present = confirmed admissions without departure;
/// admissionsConfirmed and departuresConfirmed count reports once each. Commitments stay the authoritative totals,
/// including seeded places that no tracked case explains (untracked).
/// </summary>
internal sealed record ReceptionCounters(
    PeopleAndPlaces AwaitingAdmission, int OverdueArrivals, PeopleAndPlaces Present, int AdmissionsConfirmed,
    int DeparturesConfirmed, ShelterCommitments Commitments, AvailablePlaces Available,
    PlaceTotals UntrackedHeld, PlaceTotals UntrackedOccupied, int TransferredAwaitingDeparture);

internal sealed record AwaitingView(string ReservationId, string CaseId, string LegId, PlaceNeed Resources,
    SiteWindow? ArrivalWindow, DateTimeOffset? ExpiresAt, bool Overdue);

internal sealed record AdmissionView(string AdmissionId, string CaseId, string LegId, string? ReservationId, bool WalkIn,
    PlaceNeed Resources, DateTimeOffset AdmittedAt, DateTimeOffset? DepartedAt, StayRef? TransferFrom,
    StayRef? OnwardAdmission);

internal sealed record ShelterReceptionView(string ShelterId, string Name, string Status, int Version,
    ReceptionCounters Counters, AwaitingView[] Awaiting, AdmissionView[] Admissions);

internal sealed record ReceptionCatalogView(int Revision, DateTimeOffset At, ShelterReceptionView[] Shelters);

internal sealed record ReceptionEventsView(int Revision, ReceptionEvent[] Events);

internal sealed class ReceptionQueries(CatalogStore<Shelter> catalog, TimeProvider clock)
{
    internal ReceptionCatalogView GetReception(string? shelterId, string? incidentId, DateTimeOffset? at,
        OperatorIdentity? caller = null)
    {
        OperatorDirectory.RequireScope(caller, incidentId, shelterId);
        incidentId ??= caller?.IncidentId;
        var checkedAt = (at ?? clock.GetUtcNow()).ToUniversalTime();
        var (revision, shelters) = catalog.Read();
        var rows = shelters.Where(shelter => (shelterId is null || shelter.ShelterId == shelterId) &&
                                             OperatorDirectory.CanSee(caller, shelter.ShelterId))
            .OrderBy(shelter => shelter.ShelterId, StringComparer.Ordinal)
            .Select(shelter => View(shelter, shelters, incidentId, checkedAt)).ToArray();
        if (shelterId is not null && rows.Length == 0)
            throw new SiteCatalogError(404, "UnknownShelter", $"Shelter {shelterId} is not in the catalog.");
        return new(revision, checkedAt, rows);
    }

    /// <summary>Outbox entries in commit order, for the publisher (SH-T06) and the dashboard (SH-T07).</summary>
    internal ReceptionEventsView GetEvents(string? shelterId, OperatorIdentity? caller = null)
    {
        OperatorDirectory.RequireScope(caller, null, shelterId);
        var (revision, shelters) = catalog.Read();
        var events = shelters.Where(shelter => (shelterId is null || shelter.ShelterId == shelterId) &&
                                               OperatorDirectory.CanSee(caller, shelter.ShelterId))
            .SelectMany(shelter => shelter.Ledger.Outbox)
            .Where(item => caller is null || item.CorrelationId == caller.IncidentId)
            .OrderBy(item => item.OccurredAt).ThenBy(item => item.SubjectId, StringComparer.Ordinal)
            .ThenBy(item => item.SubjectVersion).ToArray();
        return new(revision, events);
    }

    private static ShelterReceptionView View(Shelter shelter, Shelter[] shelters, string? incidentId, DateTimeOffset at)
    {
        // Onward stays at other shelters that name an admission here as their origin (transfer as two legs).
        var onward = shelters.Where(other => other.ShelterId != shelter.ShelterId)
            .SelectMany(other => other.Ledger.Admissions.Where(item => item.TransferFrom?.ShelterId == shelter.ShelterId)
                .Select(item => (Origin: item.TransferFrom!.AdmissionId, Stay: new StayRef(other.ShelterId, item.AdmissionId, item.LegId))))
            .GroupBy(item => item.Origin).ToDictionary(group => group.Key, group => group.First().Stay);
        var ledger = shelter.Ledger;
        var holds = ledger.Holds.Where(hold => incidentId is null || hold.IncidentId == incidentId).ToArray();
        var admissions = ledger.Admissions.Where(item => incidentId is null || item.IncidentId == incidentId).ToArray();
        var present = admissions.Where(item => item.Present).ToArray();
        var awaiting = holds.Select(hold => new AwaitingView(hold.ReservationId, hold.CaseId, hold.LegId, hold.Resources,
            hold.ArrivalWindow, hold.ExpiresAt, hold.ArrivalWindow?.ClosesAt is { } closes && at >= closes)).ToArray();

        // Untracked figures are computed over the whole ledger so an incident filter cannot invent them.
        var allHeld = Sum(ledger.Holds.Select(hold => hold.Resources));
        var allPresent = Sum(ledger.Admissions.Where(item => item.Present).Select(item => item.Resources));
        var commitments = shelter.Commitments;
        var counters = new ReceptionCounters(
            Sum(holds.Select(hold => hold.Resources)), awaiting.Count(item => item.Overdue),
            Sum(present.Select(item => item.Resources)), admissions.Length, admissions.Count(item => !item.Present),
            commitments, shelter.CalculateAvailability(),
            new(commitments.HeldPlaces - allHeld.Places, commitments.HeldAccessiblePlaces - allHeld.AccessiblePlaces),
            new(commitments.OccupiedPlaces - allPresent.Places,
                commitments.OccupiedAccessiblePlaces - allPresent.AccessiblePlaces),
            present.Count(item => onward.ContainsKey(item.AdmissionId)));
        return new(shelter.ShelterId, shelter.Name, shelter.Status, shelter.Version, counters, awaiting,
            admissions.Select(item => new AdmissionView(item.AdmissionId, item.CaseId, item.LegId, item.ReservationId,
                item.WalkIn, item.Resources, item.ObservedAt, item.Departure?.ObservedAt, item.TransferFrom,
                onward.GetValueOrDefault(item.AdmissionId))).ToArray());
    }

    private static PeopleAndPlaces Sum(IEnumerable<PlaceNeed> needs)
    {
        var list = needs.ToArray();
        return new(list.Length, list.Sum(need => need.Places), list.Sum(need => need.AccessiblePlaces));
    }
}
