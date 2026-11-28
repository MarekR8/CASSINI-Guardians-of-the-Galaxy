namespace Cassini.Management.Grouping;

internal sealed record GroupRelationship(string Kind, string CaseId, string RelatedCaseId, string EvidenceRef);
internal sealed record HandoverEvidence(
    string CaseId, string FromCaregiverCaseId, string ToCaregiverCaseId, DateTimeOffset EffectiveAt,
    string EvidenceRef, string AuthorizedBy, string Role, bool Confirmed);

internal sealed record EvacuationGroup(
    string IncidentId, string GroupId, int GroupRevision, string[] CaseIds,
    GroupRelationship[] Relationships, string CompositionEvidenceRef,
    HandoverEvidence[] Handovers, ApprovedTravelPlan[] Plans)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ApprovedTravelPlan? CurrentPlan => Plans.LastOrDefault();

    public EvacuationGroup RequireReview(string reason, string evidenceRef, bool invalidateRouteEvidence = false)
    {
        if (CurrentPlan is not { } plan ||
            (plan.Status == "ReviewRequired" && (!invalidateRouteEvidence || plan.RouteEvidenceInvalidated)))
            return this;

        var flagged = plan with
        {
            Status = "ReviewRequired", ReviewReason = reason, ReviewEvidenceRef = evidenceRef,
            RouteEvidenceInvalidated = invalidateRouteEvidence
        };
        return this with
        {
            Plans = [.. Plans[..^1], flagged]
        };
    }
}

internal sealed record ApprovedTravelPlan(
    int PlanRevision, string Status, DateTimeOffset ApprovedAt, TravelPartyProposal Proposal,
    string? ReviewReason = null, string? ReviewEvidenceRef = null, bool RouteEvidenceInvalidated = false);

internal sealed record TravelPartyProposal(
    string IncidentId, string GroupId, int GroupRevision, string LegId,
    string EvidenceId, int SituationRevision, string EvidenceSource, DateTimeOffset ObservedAt,
    DateTimeOffset ValidUntil, string Status, string Reason, CaseRouteEvidence[] Cases,
    GroupRelationship[] RequiredCare, GroupRelationship[] SharedDestinationPreferences, TravelCandidate[] Parties);
