using Cassini.Demo.SiteCatalog;

namespace Cassini.Sheltering.Allocation;

internal static class AssignmentStatus
{
    internal const string Pending = "Pending";
    internal const string Reserved = "Reserved";
    internal const string AwaitingReplacement = "AwaitingReplacement";
    // Derived from SH-T05 admission reports in the reception ledger. Allocation commands never move admitted occupancy.
    internal const string Admitted = "Admitted";
    internal const string Closed = "Closed";
}

internal sealed record PlaceNeeds(int Places, int AccessiblePlaces)
{
    internal bool IsValid => Places is >= 1 and <= 10 && AccessiblePlaces >= 0 && AccessiblePlaces <= Places;
    internal static PlaceNeeds Zero { get; } = new(0, 0);
    internal PlaceNeeds Add(PlaceNeeds other) => new(Places + other.Places, AccessiblePlaces + other.AccessiblePlaces);
}

internal sealed record GroupPlanRef(string? IncidentId, string? GroupId, int? PlanRevision)
{
    internal bool IsValid => SiteRules.IsId(IncidentId) && SiteRules.IsId(GroupId) && PlanRevision is > 0;
}

internal sealed record AssignmentRef(string IncidentId, string CaseId, string LegId)
{
    internal string Key => $"{IncidentId}/{CaseId}/{LegId}";
}

internal sealed record ArrivalWindow(DateTimeOffset Earliest, DateTimeOffset LatestExclusive);

/// <summary>
/// The inputs of a Routing <c>destination-route</c> query. Sheltering repeats it with the evidence ID it
/// received to verify that the route is still current before committing or keeping a hold.
/// </summary>
internal sealed record RouteQuery(
    string? Mode, DateTimeOffset? At, string? Node, string? CaseIds, DateTimeOffset? ClearBy, bool? StepFree,
    string? GroupId, int? GroupRevision, string? TransportId, string? RecordedRouteId);

internal sealed record RouteEvidenceRef(string? EvidenceId, RouteQuery? Query);

/// <summary>Assignment-side record of a hold; the hold itself lives in the shelter's reception ledger.</summary>
internal sealed record AssignmentHold(
    string ReservationId, string ShelterId, int ShelterVersion, PlaceNeeds Resources, ArrivalWindow ArrivalWindow,
    DateTimeOffset HoldsExpireAt, RouteEvidenceRef Route, long SituationRevision, string ApprovalRef, string CommandId);

internal sealed record WithdrawnHold(
    int AssignmentVersion, string ReservationId, string ShelterId, string Reason, string? EvidenceRef);

internal sealed record ShelterAssignment(
    string IncidentId, string CaseId, string LegId, int Version, string Status, PlaceNeeds Needs,
    GroupPlanRef? GroupPlanRef, AssignmentHold? Reservation, WithdrawnHold? Withdrawn, string? Reason)
{
    internal AssignmentRef Ref => new(IncidentId, CaseId, LegId);

    internal bool HoldsAt(string shelterId) => Status == AssignmentStatus.Reserved && Reservation?.ShelterId == shelterId;
}
