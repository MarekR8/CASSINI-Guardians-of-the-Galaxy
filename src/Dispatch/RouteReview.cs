namespace Cassini.Dispatch;

internal sealed record RouteStatus(string State, long? Revision, string? Code = null, string[]? Segments = null,
    DateTimeOffset? ObservedAt = null)
{
    internal bool Valid => State == "Valid";
}

internal sealed class RouteReview(RoutingClient routing)
{
    internal Task<RestrictionLookup> ReadAsync(long since, CancellationToken cancellation) =>
        routing.GetRestrictions(since, cancellation);

    internal static RouteStatus Assess(Assignment assignment, RestrictionLookup lookup)
    {
        if (!assignment.Active)
            return new("Inactive", null);

        if (lookup.Snapshot is not { } snapshot)
            return new("Unverified", null, lookup.ErrorCode);

        var route = assignment.Route;
        if (snapshot.Revision < route.Revision)
            return new("Unverified", null, "SituationRevisionUnavailable");
        if (snapshot.Revision == route.Revision)
            return new("Valid", snapshot.Revision);

        var approaching = assignment.State is "Assigned" or "Acknowledged";
        var segments = approaching ? route.ApproachSegments.Concat(route.OutboundSegments) : route.OutboundSegments;
        var usedSegments = segments.ToHashSet(StringComparer.Ordinal);
        var geometry = route.OutboundGeometry;
        var approach = approaching ? route.ApproachGeometry : null;
        var hasGeometry = geometry is { Length: >= 2 } || approach is { Length: >= 2 };
        var changes = snapshot.Roads.Where(road => road.Closed || road.Revision > route.Revision).ToArray();
        var affected = changes.Where(road => usedSegments.Contains(road.Id) ||
            road.Bounds is { } bounds && ((geometry is not null && bounds.Intersects(geometry)) ||
                (approach is not null && bounds.Intersects(approach)))).ToArray();
        var blocked = affected.Where(road => road.Closed).ToArray();
        if (blocked.Length > 0)
            return new("Blocked", snapshot.Revision, "ClosedSegment", blocked.Select(road => road.Id).ToArray(),
                blocked.Min(road => road.ClosedAt));
        if (affected.Length > 0)
            return new("ReviewRequired", snapshot.Revision, "RoadChanged", affected.Select(road => road.Id).ToArray());

        if (!snapshot.HasHistory || hasGeometry && changes.Any(road => road.Bounds is null) ||
            usedSegments.Count == 0 && !hasGeometry)
            return new("Unverified", snapshot.Revision, "RouteEvidenceUnavailable");

        return new("Valid", snapshot.Revision);
    }
}
