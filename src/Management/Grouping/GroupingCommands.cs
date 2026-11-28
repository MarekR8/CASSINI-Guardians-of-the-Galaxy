using static Cassini.Management.Grouping.GroupingQueries;

namespace Cassini.Management.Grouping;

internal sealed class GroupingCommands(ManagementStore store, GroupingEvidence evidence)
{
    public IResult Register(RegisterEvacuationGroup command) => store.ExecuteState(command.CommandId, command, snapshot =>
    {
        if (snapshot.Incident.IncidentId != command.IncidentId)
            return Reject("IncidentNotFound", "Unknown demo incident.", 404);
        if (string.IsNullOrWhiteSpace(command.GroupId) || string.IsNullOrWhiteSpace(command.EvidenceRef))
            return Reject("InvalidGroup", "Provide a group ID and composition evidence.", 400);
        if (FindGroup(snapshot, command.IncidentId, command.GroupId) is not null)
            return Reject("GroupAlreadyExists", "The group ID is already registered in this incident.", 409);
        if (ValidateComposition(snapshot, command.GroupId, command.CaseIds, command.Relationships) is { } error)
            return Reject(error);

        var group = new EvacuationGroup(snapshot.Incident.IncidentId, command.GroupId, 1, command.CaseIds!,
            command.Relationships!, command.EvidenceRef, [], []);
        return Save(snapshot, group, GroupReceipt(group));
    });

    public IResult UpdateComposition(UpdateGroupComposition command) =>
        store.ExecuteState(command.CommandId, command, snapshot =>
        {
            if (FindGroup(snapshot, command.IncidentId, command.GroupId) is not { } group)
                return Reject(MissingGroup);
            if (group.GroupRevision != command.ExpectedGroupRevision)
                return RejectGroupRevision();
            if (string.IsNullOrWhiteSpace(command.EvidenceRef))
                return Reject("EvidenceRequired", "Provide composition evidence.", 400);
            if (ValidateComposition(snapshot, group.GroupId, command.CaseIds, command.Relationships) is { } error)
                return Reject(error);
            if (group.Relationships.Any(link => link.Kind == "required-care" &&
                !command.Relationships!.Any(next => next.Kind == link.Kind && next.CaseId == link.CaseId &&
                    next.RelatedCaseId == link.RelatedCaseId)))
                return Reject("RequiredCareMustBePreserved", "Record a confirmed handover before changing a caregiver.", 422);

            var next = (group with
            {
                GroupRevision = group.GroupRevision + 1, CaseIds = command.CaseIds!,
                Relationships = command.Relationships!, CompositionEvidenceRef = command.EvidenceRef
            }).RequireReview("CompositionChanged", command.EvidenceRef);
            return Save(snapshot, next, GroupReceipt(next));
        });

    public IResult RecordHandover(RecordCareHandover command) =>
        store.ExecuteState(command.CommandId, command, snapshot =>
        {
            if (FindGroup(snapshot, command.IncidentId, command.GroupId) is not { } group)
                return Reject(MissingGroup);
            if (group.GroupRevision != command.ExpectedGroupRevision)
                return RejectGroupRevision();
            if (command.Handover is not { } handover || !handover.Confirmed ||
                handover.Role is not ("coordinator" or "field-operator") ||
                string.IsNullOrWhiteSpace(handover.AuthorizedBy) || string.IsNullOrWhiteSpace(handover.EvidenceRef) ||
                handover.EffectiveAt == default || handover.EffectiveAt > evidence.ExerciseAt)
                return Reject("HandoverNotConfirmed", "Provide authorized confirmation and an effective exercise time.", 422);

            if (!group.CaseIds.Contains(handover.ToCaregiverCaseId) || handover.ToCaregiverCaseId == handover.CaseId ||
                handover.ToCaregiverCaseId == handover.FromCaregiverCaseId)
                return Reject("InvalidCaregiver", "Choose a different member of the group as the incoming caregiver.", 422);

            var link = group.Relationships.SingleOrDefault(item => item.Kind == "required-care" && item.CaseId == handover.CaseId);
            if (link is null || link.RelatedCaseId != handover.FromCaregiverCaseId ||
                group.Handovers.Any(item => item.CaseId == handover.CaseId && item.EffectiveAt >= handover.EffectiveAt))
                return Reject("CaregiverConflict", "The handover must match current care and follow the previous handover.", 409);

            var next = (group with
            {
                GroupRevision = group.GroupRevision + 1,
                Relationships = group.Relationships.Select(item => item == link
                    ? item with { RelatedCaseId = handover.ToCaregiverCaseId, EvidenceRef = handover.EvidenceRef } : item).ToArray(),
                Handovers = [.. group.Handovers, handover]
            }).RequireReview("CareHandoverRecorded", handover.EvidenceRef);
            return Save(snapshot, next, GroupReceipt(next));
        });

    public IResult Approve(ApproveTravelPlan command) => store.ExecuteState(command.CommandId, command, snapshot =>
    {
        if (FindGroup(snapshot, command.IncidentId, command.GroupId) is not { } group)
            return Reject(MissingGroup);
        if (group.GroupRevision != command.ExpectedGroupRevision)
            return RejectGroupRevision();
        var planRevision = group.CurrentPlan?.PlanRevision ?? 0;
        if (planRevision != command.ExpectedPlanRevision)
            return Reject("PlanRevisionConflict", "Read the current plan revision and retry with a new command ID.", 409);
        if (FindEvidence(evidence, group, command.LegId, command.EvidenceId) is not { } routeEvidence)
            return Reject(MissingEvidence);

        var proposal = TravelPartyEvaluator.Evaluate(group, routeEvidence, snapshot.Incident.Cases, evidence.ExerciseAt);
        if (proposal.Status != "Proposed")
            return Reject(proposal.Reason, "The supplied evidence does not support an approvable travel plan.", 422);

        var plan = new ApprovedTravelPlan(planRevision + 1, "Approved", evidence.ExerciseAt, proposal);
        return Save(snapshot, group with { Plans = [.. group.Plans, plan] }, plan);
    });

    public IResult Flag(FlagTravelPlanForReview command) =>
        store.ExecuteState(command.CommandId, command, snapshot =>
        {
            if (FindGroup(snapshot, command.IncidentId, command.GroupId) is not { } group)
                return Reject(MissingGroup);
            if (group.CurrentPlan is not { } plan || plan.PlanRevision != command.PlanRevision)
                return Reject("PlanRevisionConflict", "The affected revision is no longer the current plan.", 409);
            if (string.IsNullOrWhiteSpace(command.Reason) || string.IsNullOrWhiteSpace(command.EvidenceRef))
                return Reject("EvidenceRequired", "Provide a review reason and changed evidence reference.", 400);

            var next = group.RequireReview(command.Reason, command.EvidenceRef, invalidateRouteEvidence: true);
            return Save(snapshot, next, next.CurrentPlan!);
        });

    private static GroupingError? ValidateComposition(
        ManagementState snapshot, string groupId, string[]? caseIds, GroupRelationship[]? relationships)
    {
        if (caseIds is not { Length: > 0 } || caseIds.Distinct().Count() != caseIds.Length ||
            caseIds.Any(id => !snapshot.Incident.Cases.Any(item => item.CaseId == id)))
            return new("InvalidCaseReferences", "Provide distinct case IDs from the incident.", 422);
        if (snapshot.Groups?.Values.Any(group => group.GroupId != groupId && group.CaseIds.Intersect(caseIds).Any()) == true)
            return new("CaseAlreadyGrouped", "A case already belongs to another group in the incident.", 409);
        if (relationships is null || relationships.Any(link => link is null ||
            link.Kind is not ("household" or "institution" or "required-care") || link.CaseId == link.RelatedCaseId ||
            !caseIds.Contains(link.CaseId) || !caseIds.Contains(link.RelatedCaseId) || string.IsNullOrWhiteSpace(link.EvidenceRef)))
            return new("InvalidRelationships", "Provide typed, evidenced links between different group members.", 422);
        var directedCare = relationships.Where(link => link.Kind == "required-care");
        var preferences = relationships.Where(link => link.Kind != "required-care");
        if (directedCare.GroupBy(link => link.CaseId).Any(links => links.Count() > 1) ||
            preferences.GroupBy(link => string.CompareOrdinal(link.CaseId, link.RelatedCaseId) < 0
                ? (link.Kind, link.CaseId, link.RelatedCaseId) : (link.Kind, link.RelatedCaseId, link.CaseId))
                .Any(links => links.Count() > 1))
            return new("DuplicateRelationships", "Provide one caregiver per dependent and one preference per pair and type.", 422);
        return null;
    }

    private static object GroupReceipt(EvacuationGroup group) =>
        new { group.IncidentId, group.GroupId, group.GroupRevision };

    private static (ManagementState, object, int) Save(ManagementState snapshot, EvacuationGroup group, object payload)
    {
        var groups = new Dictionary<string, EvacuationGroup>(snapshot.Groups ?? []) { [group.GroupId] = group };
        return (snapshot with { Groups = groups }, payload, 200);
    }

    private static (ManagementState?, object, int) RejectGroupRevision() =>
        Reject("GroupRevisionConflict", "Read the current group revision and retry with a new command ID.", 409);

    private static (ManagementState?, object, int) Reject(GroupingError error) => (null, error.Payload, error.Status);

    private static (ManagementState?, object, int) Reject(string code, string detail, int status) =>
        Reject(new GroupingError(code, detail, status));
}
