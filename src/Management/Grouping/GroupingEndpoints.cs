namespace Cassini.Management.Grouping;

internal interface IGroupingCommand
{
    string? IncidentId { get; }
}

internal sealed record RegisterEvacuationGroup(
    string? CommandId, string? IncidentId, string? GroupId, string[]? CaseIds,
    GroupRelationship[]? Relationships, string? EvidenceRef) : IGroupingCommand;
internal sealed record UpdateGroupComposition(
    string? CommandId, string? IncidentId, string? GroupId, int? ExpectedGroupRevision,
    string[]? CaseIds, GroupRelationship[]? Relationships, string? EvidenceRef) : IGroupingCommand;
internal sealed record RecordCareHandover(
    string? CommandId, string? IncidentId, string? GroupId, int? ExpectedGroupRevision, HandoverEvidence? Handover) : IGroupingCommand;
internal sealed record ApproveTravelPlan(
    string? CommandId, string? IncidentId, string? GroupId, int? ExpectedGroupRevision, int? ExpectedPlanRevision,
    string? LegId, string? EvidenceId) : IGroupingCommand;
internal sealed record FlagTravelPlanForReview(
    string? CommandId, string? IncidentId, string? GroupId, int? PlanRevision, string? Reason, string? EvidenceRef) : IGroupingCommand;

internal static class GroupingEndpoints
{
    public static void MapGrouping(this WebApplication app, GroupingEvidence evidence)
    {
        var store = app.Services.GetRequiredService<ManagementStore>();
        var queries = new GroupingQueries(store, evidence);
        var commands = new GroupingCommands(store, evidence, app.Services.GetRequiredService<TimeProvider>());
        var scope = new GroupingAccess(app.Configuration["Grouping:OperatorsFile"]);
        var routes = app.MapGroup("").AddEndpointFilter(scope.CheckAsync);
        routes.MapGet("/queries/evacuation-groups/{groupId}", queries.GetGroup);
        routes.MapGet("/queries/travel-party-proposals/{groupId}", queries.GetProposal);
        routes.MapGet("/queries/approved-travel-plans/{groupId}", queries.GetPlan);
        routes.MapPost("/commands/register-evacuation-group", commands.Register);
        routes.MapPost("/commands/update-group-composition", commands.UpdateComposition);
        routes.MapPost("/commands/record-care-handover", commands.RecordHandover);
        routes.MapPost("/commands/approve-travel-plan", commands.Approve);
        routes.MapPost("/commands/flag-travel-plan-for-review", commands.Flag);
    }
}

internal sealed record GroupingError(string Code, string Detail, int Status)
{
    public object Payload => new { code = Code, detail = Detail };
    public IResult ToResult() => Results.Json(Payload, statusCode: Status);
}

internal sealed class GroupingQueries(ManagementStore store, GroupingEvidence evidence)
{
    public IResult GetGroup(string groupId, string incidentId)
    {
        var snapshot = store.ReadSnapshot();
        if (FindGroup(snapshot, incidentId, groupId) is not { } group)
            return MissingGroup.ToResult();

        var cases = snapshot.Incident.Cases.Where(item => group.CaseIds.Contains(item.CaseId))
            .Select(item => new { item.CaseId, item.Node, item.Mobility, item.Version, item.PositionObservedAt });
        return Results.Ok(new
        {
            group.IncidentId, group.GroupId, group.GroupRevision, cases, group.Relationships,
            group.CompositionEvidenceRef,
            handovers = group.Handovers.Select(item => new { item.CaseId, item.FromCaregiverCaseId,
                item.ToCaregiverCaseId, item.EffectiveAt, item.EvidenceRef })
        });
    }

    public IResult GetProposal(string groupId, string incidentId, string legId, string evidenceId)
    {
        var snapshot = store.ReadSnapshot();
        if (FindGroup(snapshot, incidentId, groupId) is not { } group)
            return MissingGroup.ToResult();
        if (FindEvidence(evidence, group, legId, evidenceId) is not { } routeEvidence)
            return MissingEvidence.ToResult();

        return Results.Ok(TravelPartyEvaluator.Evaluate(group, routeEvidence, snapshot.Incident.Cases, evidence.ExerciseAt));
    }

    public IResult GetPlan(string groupId, string incidentId)
    {
        var snapshot = store.ReadSnapshot();
        if (FindGroup(snapshot, incidentId, groupId) is not { } group)
            return MissingGroup.ToResult();
        if (group.CurrentPlan is not { } plan)
            return new GroupingError("PlanNotFound", "The group has no approved plan revision.", 404).ToResult();

        if (plan.Status == "Approved" && plan.Proposal.Cases.Any(item =>
            !snapshot.Incident.Cases.Any(person => person.CaseId == item.CaseId && person.Version == item.CaseVersion)))
            return Results.Ok(plan with
            {
                Status = "ReviewRequired", ReviewReason = "CaseVersionChanged", ReviewEvidenceRef = plan.Proposal.EvidenceId
            });

        return Results.Ok(plan);
    }

    internal static EvacuationGroup? FindGroup(ManagementState snapshot, string? incidentId, string? groupId) =>
        snapshot.Incident.IncidentId == incidentId && groupId is not null &&
        snapshot.Groups?.GetValueOrDefault(groupId) is { } group && group.IncidentId == incidentId ? group : null;

    internal static RouteEvidenceSet? FindEvidence(
        GroupingEvidence evidence, EvacuationGroup group, string? legId, string? evidenceId) =>
        evidence.Evidence.FirstOrDefault(item => item.IncidentId == group.IncidentId &&
            item.LegId == legId && item.EvidenceId == evidenceId);

    internal static GroupingError MissingGroup => new("GroupNotFound", "Unknown group for this incident.", 404);
    internal static GroupingError MissingEvidence => new("EvidenceNotFound", "Unknown evidence for this incident and leg.", 404);
}
