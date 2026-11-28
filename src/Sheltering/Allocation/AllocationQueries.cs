using Cassini.Demo.SiteCatalog;
using Cassini.Sheltering.Catalog;
using Cassini.Sheltering.Infrastructure;

namespace Cassini.Sheltering.Allocation;

internal sealed record CandidateRequest(
    string? IncidentId, string? LegId, string? CaseIds, string? Mode, DateTimeOffset? At, string? Node,
    DateTimeOffset? ClearBy, bool? StepFree, string? GroupId, int? GroupRevision, string? TransportId);

internal sealed record ShelterCandidate(
    int Rank, string ShelterId, int ShelterVersion, int SharedWithGroup, DateTimeOffset? DepartsAt,
    ArrivalWindow ArrivalWindow, AvailablePlaces Available, RouteEvidenceRef Route);

internal sealed record RejectedShelter(string ShelterId, string Reason);

internal sealed record CandidateView(
    string IncidentId, string LegId, string[] CaseIds, int AllocationRevision, PlaceNeeds Demand,
    ShelterCandidate[] Candidates, RejectedShelter[] Rejected);

internal sealed class AllocationQueries(AllocationStore store, CatalogStore<Shelter> catalog, RoutingClient routing)
{
    internal object GetAssignment(string? incidentId, string? caseId, string? legId)
    {
        if (!SiteRules.IsId(incidentId) || !SiteRules.IsId(caseId) || !SiteRules.IsId(legId))
            throw new SiteCatalogError(400, "InvalidAssignmentRef", "Specify incidentId, caseId and legId.");
        var state = store.Read();
        var assignment = state.Find(new(incidentId!, caseId!, legId!))
            ?? throw new SiteCatalogError(404, "AssignmentNotFound", "No shelter assignment exists for this case and leg.");
        return new { revision = state.Revision, assignment };
    }

    internal object GetAssignments(string? incidentId)
    {
        if (!SiteRules.IsId(incidentId))
            throw new SiteCatalogError(400, "InvalidIncident", "Specify a valid incidentId.");
        var state = store.Read();
        var assignments = state.Assignments.Values.Where(item => item.IncidentId == incidentId)
            .OrderBy(item => item.CaseId, StringComparer.Ordinal).ThenBy(item => item.LegId, StringComparer.Ordinal)
            .ToArray();
        return new { revision = state.Revision, assignments };
    }

    internal object GetOutbox(long? after)
    {
        var state = store.Read();
        return new { revision = state.Revision, entries = state.Outbox.Where(entry => entry.Sequence > (after ?? 0)).ToArray() };
    }

    /// <summary>
    /// Ranks only feasible shelters for one travelling party: shared destination with already held group members
    /// first, then the earlier eligible arrival. A shelter is never preferred over access, clearance or capacity.
    /// </summary>
    internal async Task<CandidateView> GetCandidates(CandidateRequest request, CancellationToken cancellation)
    {
        var ids = request.CaseIds?.Split(',') ?? [];
        if (!SiteRules.IsId(request.IncidentId) || !SiteRules.IsId(request.LegId) || ids.Length is 0 or > 32 ||
            !ids.All(SiteRules.IsId) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            request.Mode is not ("walking" or "driving") || request.At is null)
            throw new SiteCatalogError(400, "InvalidCandidateQuery",
                "Specify incidentId, legId, caseIds, mode and an explicit at instant.");

        var state = store.Read();
        var party = ids.Select(caseId => state.Find(new(request.IncidentId!, caseId, request.LegId!))
            ?? throw new SiteCatalogError(404, "AssignmentNotFound", $"Open a shelter assignment for {caseId} first.")).ToArray();
        if (party.Any(item => item.Status is AssignmentStatus.Admitted or AssignmentStatus.Closed))
            throw new SiteCatalogError(422, "AssignmentNotAllocatable", "Admitted or closed assignments are not reallocated.");
        var demand = party.Aggregate(PlaceNeeds.Zero, (sum, item) => sum.Add(item.Needs));
        var current = party.Select(item => item.Reservation?.ShelterId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        var groupId = party.Select(item => item.GroupPlanRef?.GroupId).FirstOrDefault(id => id is not null);
        var partyKeys = party.Select(item => item.Ref.Key).ToHashSet(StringComparer.Ordinal);

        var query = new RouteQuery(request.Mode, request.At, request.Node, request.Node is null ? string.Join(',', ids) : null,
            request.ClearBy, request.StepFree, request.GroupId, request.GroupRevision, request.TransportId, null);
        var feasible = new List<(ShelterCandidate Candidate, DateTimeOffset Arrival)>();
        var rejected = new List<RejectedShelter>();
        foreach (var shelter in catalog.Read().Items.OrderBy(item => item.ShelterId, StringComparer.Ordinal))
        {
            if (current.Contains(shelter.ShelterId))
            {
                rejected.Add(new(shelter.ShelterId, "CurrentShelter"));
                continue;
            }

            var check = await routing.CheckShelterRoute(party[0].Ref, shelter.ShelterId, query, null, cancellation);
            var available = Available(state, shelter);
            var reason = Infeasible(state, shelter, check, demand, available);
            if (reason is not null)
            {
                rejected.Add(new(shelter.ShelterId, reason));
                continue;
            }

            var shared = groupId is null ? 0 : state.Assignments.Values.Count(item =>
                item.GroupPlanRef?.GroupId == groupId && item.LegId == request.LegId &&
                !partyKeys.Contains(item.Ref.Key) && item.HoldsAt(shelter.ShelterId));
            feasible.Add((new ShelterCandidate(0, shelter.ShelterId, shelter.Version, shared, check.DepartsAt,
                check.ArrivalWindow!, available, new(check.Evidence!.EvidenceId, query)), check.ArrivalWindow!.Earliest));
        }

        var ranked = feasible.OrderByDescending(item => item.Candidate.SharedWithGroup).ThenBy(item => item.Arrival)
            .ThenBy(item => item.Candidate.ShelterId, StringComparer.Ordinal)
            .Select((item, index) => item.Candidate with { Rank = index + 1 }).ToArray();
        return new CandidateView(request.IncidentId!, request.LegId!, ids, state.Revision, demand, ranked, rejected.ToArray());
    }

    internal static AvailablePlaces Available(AllocationState state, Shelter shelter) =>
        WithAllocations(state, shelter).CalculateAvailability();

    /// <summary>Adds this service's active holds to the shelter's configured baseline commitments.</summary>
    internal static Shelter WithAllocations(AllocationState state, Shelter shelter)
    {
        var held = state.HeldResources(shelter.ShelterId);
        return shelter with
        {
            Commitments = shelter.Commitments with
            {
                HeldPlaces = shelter.Commitments.HeldPlaces + held.Places,
                HeldAccessiblePlaces = shelter.Commitments.HeldAccessiblePlaces + held.AccessiblePlaces,
            },
        };
    }

    private static string? Infeasible(AllocationState state, Shelter shelter, RouteCheck check, PlaceNeeds demand,
        AvailablePlaces available)
    {
        if (!check.Eligible)
            return check.Reason ?? "RouteIneligible";
        if (check.SiteVersion != shelter.Version)
            return "RouteEvidenceStale";
        var arrival = check.ArrivalWindow!;
        var reason = SiteRules.IneligibleReason(shelter.Status, shelter.OperatingWindow, shelter.Evidence, arrival.Earliest,
            checked((int)check.SituationRevision));
        if (reason is not null)
            return reason;
        if (WithAllocations(state, shelter).IsOvercommitted)
            return "Overcommitted";
        if (demand.Places > available.Places)
            return "InsufficientCapacity";
        if (demand.AccessiblePlaces > available.AccessiblePlaces)
            return "InsufficientAccessiblePlaces";
        if (shelter.Resources.IntakePerHour is { } intake)
        {
            var hour = arrival.Earliest.ToUniversalTime();
            var expected = state.HeldAt(shelter.ShelterId).Where(item =>
                item.Reservation!.ArrivalWindow.Earliest.ToUniversalTime() is var other &&
                other.Date == hour.Date && other.Hour == hour.Hour).Sum(item => item.Reservation!.Resources.Places);
            if (expected + demand.Places > intake)
                return "IntakeLimitReached";
        }
        return null;
    }
}
