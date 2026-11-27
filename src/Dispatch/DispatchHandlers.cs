namespace Cassini.Dispatch;

internal sealed record RequestAssistance(string? CommandId, string? CaseId, string? PickupNode);
internal sealed record AssignUnit(string? CommandId, string? RequestId, string? UnitId, int? ExpectedVersion);
internal sealed record ReplanAssignment(string? CommandId, string? AssignmentId, int? ExpectedVersion);
internal sealed record ReportProgress(string? CommandId, string? AssignmentId, string? Step, int? ExpectedVersion);

internal sealed class DispatchQueries(DispatchStore store, SituationClient situation)
{
    public async Task<DispatchView> GetAssignments(CancellationToken cancellation)
    {
        var view = store.Read();
        if (!view.Assignments.Any(assignment => assignment.Active))
            return view;

        var revision = await situation.GetRevision(cancellation);
        return view with
        {
            Assignments = view.Assignments.Select(assignment => assignment with
            {
                RouteCurrent = assignment.Active && revision is not null
                    ? revision == assignment.Route.Revision : null,
            }).ToArray(),
        };
    }
}

internal sealed class DispatchCommands(
    DispatchStore store, ManagementClient management, RoutingClient routing, SituationClient situation)
{
    public async Task<IResult> RequestAssistance(RequestAssistance command, CancellationToken cancellation)
    {
        if (store.CheckReceipt(command.CommandId, command) is { } prior)
            return prior;
        if (string.IsNullOrWhiteSpace(command.CaseId) || command.CaseId.Length > 128 ||
            string.IsNullOrWhiteSpace(command.PickupNode))
            return DispatchStore.Error("InvalidRequest", "Provide a demo case and a pickup node.", 400);

        var lookup = await management.GetPosition(command.CaseId, cancellation);
        if (!lookup.ServiceAvailable)
            return DispatchStore.Error("ManagementUnavailable", "Current case information is unavailable.", 503);
        if (lookup.Case is null)
            return DispatchStore.Error("CaseNotFound", "Unknown demo case.", 404);
        if (lookup.Case.Progress == "ExitConfirmed")
            return DispatchStore.Error("CaseAlreadyEvacuated", "This case already has a confirmed exit.", 409);
        if (lookup.Case.Node != command.PickupNode)
            return DispatchStore.Error("PickupUnverified", "Use the current confirmed node for the demo case.", 422);

        return store.Execute(command.CommandId, command, view =>
        {
            var existing = view.Requests.FirstOrDefault(item =>
                item.CaseId == command.CaseId && item.State is "Open" or "Assigned");
            if (existing is not null)
            {
                if (existing.PickupNode == command.PickupNode)
                    return (view, existing, 200);
                if (existing.State == "Assigned")
                    return Reject("RequestAlreadyAssigned", "An assigned request cannot change its pickup node.", 409);

                var updated = existing with { PickupNode = command.PickupNode, Version = existing.Version + 1 };
                return (view with { Requests = Replace(view.Requests, item => item.RequestId == existing.RequestId, updated) },
                    updated, 200);
            }

            var request = new AssistanceRequest($"request-{command.CaseId}-{view.Requests.Length + 1}",
                command.CaseId, command.PickupNode, "Open", 1);
            return (view with { Requests = [.. view.Requests, request] }, request, 200);
        });
    }

    public async Task<IResult> AssignUnit(AssignUnit command, CancellationToken cancellation)
    {
        if (store.CheckReceipt(command.CommandId, command) is { } prior)
            return prior;

        if (command.ExpectedVersion is null || string.IsNullOrWhiteSpace(command.RequestId) ||
            string.IsNullOrWhiteSpace(command.UnitId))
            return DispatchStore.Error("InvalidAssignment", "Provide a request, unit and expected version.", 400);

        var current = store.Read();
        var request = current.Requests.FirstOrDefault(item => item.RequestId == command.RequestId);
        var unit = current.Units.FirstOrDefault(item => item.UnitId == command.UnitId);
        var invalid = ValidateBooking(current, request, unit, command.ExpectedVersion.Value);
        if (invalid is not null)
            return DispatchStore.Error(invalid.Value.Code, invalid.Value.Detail, invalid.Value.Status);

        var lookup = await routing.GetTransportRoute(unit!.Node, request!.PickupNode, cancellation);
        if (await GetRouteError(lookup, cancellation) is { } routeError)
            return routeError;

        return store.Execute(command.CommandId, command, view =>
        {
            var latestRequest = view.Requests.FirstOrDefault(item => item.RequestId == request.RequestId);
            var latestUnit = view.Units.FirstOrDefault(item => item.UnitId == unit.UnitId);
            var latestError = ValidateBooking(view, latestRequest, latestUnit, command.ExpectedVersion.Value);
            if (latestError is { } error)
                return Reject(error.Code, error.Detail, error.Status);
            if (latestUnit!.Node != unit.Node)
                return Reject("UnitPositionChanged", "The unit moved while its route was evaluated. Retry booking.", 409);

            var assignment = new Assignment(
                $"assignment-{view.Assignments.Length + 1}", request.RequestId, request.CaseId, unit.UnitId,
                "Assigned", 1, lookup.Route!, unit.Node);
            return (view with
            {
                Requests = Replace(view.Requests, item => item.RequestId == request.RequestId,
                    latestRequest! with { State = "Assigned", Version = latestRequest!.Version + 1 }),
                Units = Replace(view.Units, item => item.UnitId == unit.UnitId, unit with { State = "Booked" }),
                Assignments = [.. view.Assignments, assignment],
            }, assignment, 200);
        });
    }

    public async Task<IResult> Replan(ReplanAssignment command, CancellationToken cancellation)
    {
        if (store.CheckReceipt(command.CommandId, command) is { } prior)
            return prior;
        if (command.ExpectedVersion is null || string.IsNullOrWhiteSpace(command.AssignmentId))
            return DispatchStore.Error("InvalidAssignment", "Provide an assignment and expected version.", 400);

        var view = store.Read();
        var assignment = view.Assignments.FirstOrDefault(item => item.AssignmentId == command.AssignmentId);
        if (assignment is null)
            return DispatchStore.Error("AssignmentNotFound", "Unknown assignment.", 404);
        if (!assignment.Active || assignment.Version != command.ExpectedVersion)
            return DispatchStore.Error("VersionConflict", "Read the current assignment and its version.", 409);

        var request = view.Requests.Single(item => item.RequestId == assignment.RequestId);
        var lookup = await routing.GetTransportRoute(assignment.LastKnownNode, request.PickupNode, cancellation);
        if (await GetRouteError(lookup, cancellation) is { } routeError)
            return routeError;

        return store.Execute(command.CommandId, command, latest =>
        {
            var current = latest.Assignments.Single(item => item.AssignmentId == assignment.AssignmentId);
            if (!current.Active || current.Version != command.ExpectedVersion)
                return Reject("VersionConflict", "Read the current assignment and its version.", 409);

            var updated = current with { Route = lookup.Route!, Version = current.Version + 1 };
            return (latest with { Assignments = Replace(latest.Assignments,
                item => item.AssignmentId == updated.AssignmentId, updated) }, updated, 200);
        });
    }

    public async Task<IResult> ReportProgress(ReportProgress command, CancellationToken cancellation)
    {
        if (store.CheckReceipt(command.CommandId, command) is { } prior)
            return prior;
        if (command.ExpectedVersion is null || string.IsNullOrWhiteSpace(command.AssignmentId) ||
            command.Step is not ("acknowledge" or "arrive" or "pickup" or "complete" or "cancel"))
            return DispatchStore.Error("InvalidProgress", "Provide an assignment, supported step and expected version.", 400);

        var current = store.Read().Assignments.FirstOrDefault(item => item.AssignmentId == command.AssignmentId);
        if (current is null)
            return DispatchStore.Error("AssignmentNotFound", "Unknown assignment.", 404);
        if (current.Version != command.ExpectedVersion)
            return DispatchStore.Error("VersionConflict", "Read the current assignment and its version.", 409);

        if (command.Step is "acknowledge" or "complete")
        {
            var revision = await situation.GetRevision(cancellation);
            if (revision is null)
                return DispatchStore.Error("SituationUnavailable", "Current restrictions are unavailable.", 503);
            if (revision != current.Route.Revision)
                return DispatchStore.Error("RouteRequiresReview", "Replan the assignment against current restrictions.", 409);
        }

        return store.Execute(command.CommandId, command, view => ApplyProgress(command, view));
    }

    private static (DispatchView?, object, int) ApplyProgress(ReportProgress command, DispatchView view)
    {
        var current = view.Assignments.FirstOrDefault(item => item.AssignmentId == command.AssignmentId);
        if (current is null)
            return Reject("AssignmentNotFound", "Unknown assignment.", 404);
        if (current.Version != command.ExpectedVersion)
            return Reject("VersionConflict", "Read the current assignment and its version.", 409);

        var state = (current.State, command.Step) switch
        {
            ("Assigned", "acknowledge") => "Acknowledged",
            ("Acknowledged", "arrive") => "AtPickup",
            ("AtPickup", "pickup") => "PickupConfirmed",
            ("PickupConfirmed", "complete") => "Completed",
            (_, "cancel") when current.Active => "Cancelled",
            _ => null,
        };

        if (state is null)
            return Reject("InvalidTransition", "The requested progress step does not follow the current state.", 409);

        var request = view.Requests.Single(item => item.RequestId == current.RequestId);
        var unit = view.Units.Single(item => item.UnitId == current.UnitId);
        var node = state switch
        {
            "AtPickup" => request.PickupNode,
            "Completed" => current.Route.Exit,
            _ => current.LastKnownNode,
        };
        var updated = current with { State = state, Version = current.Version + 1, LastKnownNode = node };
        var changed = view with { Assignments = Replace(view.Assignments,
            item => item.AssignmentId == updated.AssignmentId, updated) };
        if (!updated.Active)
        {
            changed = changed with
            {
                Requests = Replace(view.Requests, item => item.RequestId == request.RequestId,
                    request with { State = state == "Cancelled" ? "Open" : "Completed", Version = request.Version + 1 }),
                Units = Replace(view.Units, item => item.UnitId == unit.UnitId,
                    unit with { State = "Available", Node = node }),
            };
        }

        return (changed, updated, 200);
    }

    private static (string Code, string Detail, int Status)? ValidateBooking(
        DispatchView view, AssistanceRequest? request, ResponseUnit? unit, int expectedVersion)
    {
        if (request is null || unit is null)
            return ("RequestOrUnitNotFound", "Unknown request or unit.", 404);
        if (request.Version != expectedVersion)
            return ("VersionConflict", "Read the current request and its version.", 409);
        if (request.State != "Open" || view.Assignments.Any(item => item.Active && item.RequestId == request.RequestId))
            return ("RequestUnavailable", "This assistance request is unavailable for booking.", 409);
        if (unit.State != "Available" || view.Assignments.Any(item => item.Active && item.UnitId == unit.UnitId))
            return ("UnitUnavailable", "The unit is already booked.", 409);

        return null;
    }

    private async Task<IResult?> GetRouteError(RouteLookup lookup, CancellationToken cancellation)
    {
        if (!lookup.ServiceAvailable)
            return DispatchStore.Error("RoutingUnavailable", "The local Routing service could not evaluate both legs.", 503);
        if (lookup.Route is null)
            return DispatchStore.Error("NoEligibleRoute", "Both approach and outbound legs need an eligible route.", 422);

        var revision = await situation.GetRevision(cancellation);
        if (revision is null)
            return DispatchStore.Error("SituationUnavailable", "Current restrictions are unavailable.", 503);
        if (revision != lookup.Route.Revision)
            return DispatchStore.Error("SituationChanged", "Restrictions changed during route evaluation. Retry.", 409);

        return null;
    }

    private static T[] Replace<T>(T[] collection, Func<T, bool> match, T replacement) =>
        collection.Select(item => match(item) ? replacement : item).ToArray();

    private static (DispatchView?, object, int) Reject(string code, string detail, int status) =>
        (null, new { code, detail }, status);
}
