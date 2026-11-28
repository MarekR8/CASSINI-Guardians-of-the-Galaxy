using Cassini.Messaging;

namespace Cassini.Management.Grouping;

internal static class GroupingEvents
{
    internal static ManagementState Save(ManagementState state, EvacuationGroup group, string commandId,
        DateTimeOffset now, string? compositionReason = null)
    {
        var groups = new Dictionary<string, EvacuationGroup>(state.Groups ?? []);
        var previous = groups.GetValueOrDefault(group.GroupId);
        groups[group.GroupId] = group;
        var next = state with { Groups = groups };
        var subject = $"{Uri.EscapeDataString(group.IncidentId)}/{Uri.EscapeDataString(group.GroupId)}";

        if (compositionReason is not null)
            next = Publish(next, EventTypes.EvacuationGroupChanged, subject, group.GroupRevision,
                new { group.IncidentId, group.GroupId, group.GroupRevision, reason = compositionReason });
        if (group.Handovers.Length > (previous?.Handovers.Length ?? 0))
        {
            var handover = group.Handovers[^1];
            next = Publish(next, EventTypes.CareHandoverRecorded, subject + "/care", group.GroupRevision,
                new { group.IncidentId, group.GroupId, group.GroupRevision, handoverRef = handover.EvidenceRef, handover.EffectiveAt });
        }

        if (group.CurrentPlan is { } plan)
        {
            var priorPlan = previous?.CurrentPlan;
            var proposal = plan.Proposal;
            if (priorPlan?.PlanRevision != plan.PlanRevision)
                next = Publish(next, EventTypes.TravelPlanApproved, subject + "/plans", 2L * plan.PlanRevision - 1,
                    new { group.IncidentId, group.GroupId, proposal.GroupRevision, proposal.LegId, plan.PlanRevision,
                        proposal.EvidenceId, proposal.SituationRevision });
            else if (plan.Status == "ReviewRequired" && priorPlan.Status != plan.Status)
                next = Publish(next, EventTypes.TravelPlanReviewRequired, subject + "/plans", 2L * plan.PlanRevision,
                    new { group.IncidentId, group.GroupId, proposal.LegId, plan.PlanRevision,
                        reason = plan.ReviewReason, evidenceRef = plan.ReviewEvidenceRef });
        }
        return next;

        ManagementState Publish<T>(ManagementState current, string type, string id, long version, T payload) =>
            current.Publish(IntegrationEvent.Create(type, ManagementEvents.Producer, id, version, now,
                payload, correlationId: group.IncidentId, causationId: commandId));
    }

    internal static ManagementState FlagChangedCase(ManagementState state, EvacuationCase updated,
        string commandId, DateTimeOffset now)
    {
        foreach (var group in (IEnumerable<EvacuationGroup>?)state.Groups?.Values ?? [])
        {
            if (group.IncidentId != state.Incident.IncidentId || group.CurrentPlan is not { Status: "Approved" } plan ||
                !plan.Proposal.Cases.Any(item => item.CaseId == updated.CaseId && item.CaseVersion != updated.Version))
                continue;
            state = Save(state, group.RequireReview("CaseVersionChanged", plan.Proposal.EvidenceId), commandId, now);
        }
        return state;
    }
}
