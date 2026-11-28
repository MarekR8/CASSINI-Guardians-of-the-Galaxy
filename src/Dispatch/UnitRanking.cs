namespace Cassini.Dispatch;

internal sealed record UnitCandidate(string UnitId, string Node, string Status, int? ApproachSeconds = null);

internal sealed class UnitRanking(DispatchStore store, RoutingClient routing, SituationClient situation,
    UnitPositionPolicy positions)
{
    public async Task<IResult> GetCandidates(string? requestId, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return DispatchStore.Error("RequestIdRequired", "Provide an assistance request ID.", 400);

        var view = store.Read();
        var request = view.Requests.FirstOrDefault(item => item.RequestId == requestId);
        if (request is null)
            return DispatchStore.Error("RequestNotFound", "Unknown assistance request.", 404);
        if (request.State != "Open")
            return DispatchStore.Error("RequestUnavailable", "This assistance request is unavailable for booking.", 409);

        var revision = await situation.GetRevision(cancellation);
        if (revision is null)
            return DispatchStore.Error("SituationUnavailable", "Current restrictions are unavailable.", 503);

        var available = view.Units.Where(unit => unit.State == "Available").ToArray();
        var verifiedUnits = available.Where(unit => positions.Confirm(unit.UnitId, unit.Node, null) is not null)
            .Select(unit => unit.UnitId).ToHashSet(StringComparer.Ordinal);
        var routes = new Dictionary<string, RouteLookup>(StringComparer.Ordinal);
        foreach (var node in available.Where(unit => verifiedUnits.Contains(unit.UnitId))
                     .Select(unit => unit.Node).Distinct(StringComparer.Ordinal))
            routes.Add(node, await routing.GetTransportRoute(node, request.PickupNode, cancellation));

        var currentRevision = routes.Count > 0 ? await situation.GetRevision(cancellation) : revision;
        if (currentRevision is null)
            return DispatchStore.Error("SituationUnavailable", "Current restrictions are unavailable.", 503);
        if (currentRevision != revision || routes.Values.Any(lookup => lookup.Route is { } route && route.Revision != revision))
            return DispatchStore.Error("SituationChanged", "Restrictions changed during route evaluation. Retry.", 409);

        var candidates = available.Select(unit =>
        {
            if (!verifiedUnits.Contains(unit.UnitId))
                return new UnitCandidate(unit.UnitId, unit.Node, "PositionUnverified");

            var lookup = routes[unit.Node];
            return lookup switch
            {
                { Route: { } route } => new(unit.UnitId, unit.Node, "Eligible", route.ApproachSeconds),
                { ErrorCode: "FixtureMissing" } => new(unit.UnitId, unit.Node, "FixtureMissing"),
                { ErrorCode: "SituationUnavailable" } => new(unit.UnitId, unit.Node, "SituationUnavailable"),
                { ServiceAvailable: false } => new(unit.UnitId, unit.Node, "RoutingUnavailable"),
                _ => new(unit.UnitId, unit.Node, "NoEligibleRoute"),
            };
        }).OrderBy(candidate => candidate.ApproachSeconds is null)
            .ThenBy(candidate => candidate.ApproachSeconds)
            .ThenBy(candidate => candidate.UnitId, StringComparer.Ordinal)
            .ToArray();
        return Results.Ok(new { requestId, requestVersion = request.Version, situationRevision = revision, candidates });
    }
}
