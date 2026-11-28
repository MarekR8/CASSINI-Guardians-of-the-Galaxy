using System.Globalization;
using Cassini.Demo;

namespace Cassini.Routing;

internal sealed record RouteOption(
    string Id, string Exit, int Seconds, double Weight, string[] Segments, string[] Nodes,
    DateTimeOffset DepartsAt, DateTimeOffset ArrivesAt);
internal sealed record Distribution(string Node, long Revision, DateTimeOffset At, RouteOption[] Options);
internal sealed record AssistanceRoute(
    long Revision, string Exit, string[] ApproachSegments, string[] OutboundSegments,
    int ApproachSeconds, int OutboundSeconds, double[][]? ApproachGeometry = null,
    double[][]? OutboundGeometry = null, RouteProvenance? Provenance = null);
internal sealed record DrawStatus(string State, RouteOption? Route, long Revision);
internal sealed record RoadRestriction(string Id, bool Closed, long? Revision, RoadBounds? Bounds, DateTimeOffset? ClosedAt);

internal sealed class RoutingQueries(SituationGateway situation, DrawJournal journal,
    AssistanceRouteProvider? routeProvider = null)
{
    internal static readonly DateTimeOffset StartTime = new(2026, 11, 27, 19, 20, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ScheduleCloses = StartTime.AddMinutes(40);

    internal async Task<Distribution> DistributionAsync(
        string node, DateTimeOffset? at, string? mode, string? destination, bool accessible,
        CancellationToken cancellationToken)
    {
        ValidateDistribution(node, mode, destination);
        var snapshot = await situation.ReadAsync(cancellationToken);
        return BuildDistribution(snapshot, node, at, mode, destination, accessible);
    }

    internal async Task<AssistanceRoute> AssistanceAsync(
        string from, string pickup, CancellationToken cancellationToken)
    {
        if (!Network.Contains(from) || !Network.Contains(pickup))
            throw new RoutingException(400, "InvalidNode", "Nieznany węzeł początku lub odbioru.");

        var snapshot = await situation.ReadAsync(cancellationToken);
        var closed = snapshot.ClosedSegments.ToHashSet(StringComparer.Ordinal);
        var approach = Network.FindPath(from, pickup, driving: true, closed);
        var outbound = Network.Exits
            .Select(exit => Network.FindPath(pickup, exit, driving: true, closed))
            .OfType<GraphPath>().OrderBy(path => path.Seconds).ThenBy(path => path.Nodes[^1], StringComparer.Ordinal)
            .ToArray();
        if (approach is null || outbound.Length == 0)
            throw new RoutingException(422, "NoEligibleRoute", "Brak zweryfikowanego dojazdu lub wyjazdu dla pojazdu.");

        if (routeProvider is not null)
            return await routeProvider.FindAsync(from, pickup, outbound.Select(path => path.Nodes[^1]).ToArray(),
                snapshot, cancellationToken);

        var preferred = outbound[0];
        return new(snapshot.Revision, preferred.Nodes[^1], approach.Segments, preferred.Segments,
            approach.Seconds, preferred.Seconds);
    }

    internal async Task<object> AssistanceRestrictionsAsync(long since, CancellationToken cancellationToken)
    {
        if (since < 0)
            throw new RoutingException(400, "InvalidRevision", "Provide a non-negative revision.");
        var snapshot = await situation.ReadAsync(cancellationToken);
        var revisions = snapshot.SegmentRevisions;
        var closed = snapshot.ClosedSegments.ToHashSet(StringComparer.Ordinal);
        var updated = revisions?.Where(pair => pair.Value > since).Select(pair => pair.Key) ?? [];
        var roads = since >= snapshot.Revision ? [] : closed.Union(updated).Order(StringComparer.Ordinal)
            .Select(id => new RoadRestriction(id, closed.Contains(id), revisions?.GetValueOrDefault(id),
                ClosedRoadArea.ForSegment(id)?.RoadBounds, snapshot.ClosureEvidence?.GetValueOrDefault(id)?.DecidedAt)).ToArray();
        return new { snapshot.Revision, hasHistory = revisions is not null, roads };
    }

    internal async Task<DrawStatus> DrawStatusAsync(string commandId, CancellationToken cancellationToken)
    {
        var saved = journal.Get(commandId)
            ?? throw new RoutingException(404, "UnknownDraw", "Nie znaleziono wyniku losowania.");
        var snapshot = await situation.ReadAsync(cancellationToken);
        var closed = snapshot.ClosedSegments.ToHashSet(StringComparer.Ordinal);
        var revised = snapshot.SegmentRevisions is null
            ? snapshot.Revision != saved.Revision
            : saved.Route.Segments.Any(segment => snapshot.SegmentRevisions.GetValueOrDefault(segment) > saved.Revision);
        if (snapshot.Revision < saved.Revision || revised || saved.Route.Segments.Any(closed.Contains))
            return new("Withdrawn", null, snapshot.Revision);
        if (ScenarioTime(snapshot.Stage) > saved.Route.ArrivesAt)
            return new("Expired", null, snapshot.Revision);
        return new("Active", saved.Route, snapshot.Revision);
    }

    internal async Task<object> ValidateRouteAsync(string segments, DateTimeOffset? at, CancellationToken cancellationToken)
    {
        var ids = segments.Split(',', StringSplitOptions.TrimEntries);
        if (ids.Length > 5000 || ids.Any(id => !Network.ContainsSegment(id)))
            throw new RoutingException(400, "InvalidSegment", "Podaj znane odcinki oddzielone przecinkami.");

        var snapshot = await situation.ReadAsync(cancellationToken);
        var checkedAt = at ?? ScenarioTime(snapshot.Stage);
        if (checkedAt < ScenarioTime(snapshot.Stage))
            throw new RoutingException(400, "InvalidTime", "Aktualna migawka nie potwierdza stanu w przeszłości.");
        var blocked = ids.Where(snapshot.ClosedSegments.Contains).Distinct().ToArray();
        return new { ok = blocked.Length == 0, reason = blocked.Length == 0 ? null : "ClosedSegment",
            closedSegments = blocked, snapshot.Revision, at = checkedAt };
    }

    internal async Task<object> CityRouteAsync(string from, string to, string? mode, CancellationToken cancellationToken)
    {
        var city = Network.City
            ?? throw new RoutingException(503, "CityMapUnavailable", "Przygotuj lokalny wycinek OSM.");
        if (mode is not (null or "walking" or "driving"))
            throw new RoutingException(400, "InvalidRequest", "Tryb musi mieć wartość walking lub driving.");
        var start = ReadPoint(from);
        var destination = ReadPoint(to);
        var snapshot = await situation.ReadAsync(cancellationToken);
        var route = city.Route(start, destination, mode == "driving", snapshot.ClosedSegments.ToHashSet(StringComparer.Ordinal));
        return new { snapshot.Revision, mode = mode ?? "walking", route };

        double[] ReadPoint(string value)
        {
            var parts = value.Split(',');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                !city.InBounds(longitude, latitude))
                throw new RoutingException(400, "InvalidPoint", "Podaj długość i szerokość w granicach obszaru mapy.");
            return [longitude, latitude];
        }
    }

    internal static void ValidateDistribution(string? node, string? mode, string? destination)
    {
        if (!Network.Contains(node) || (destination is not null && !Network.Exits.Contains(destination)))
            throw new RoutingException(400, "InvalidNode", "Nieznany węzeł początkowy lub niedozwolone wyjście.");
        if (mode is not (null or "walking" or "driving"))
            throw new RoutingException(400, "InvalidRequest", "Tryb musi mieć wartość walking lub driving.");
    }

    internal static Distribution BuildDistribution(
        SituationSnapshot snapshot, string node, DateTimeOffset? requestedAt, string? mode, string? destination,
        bool accessible)
    {
        var scenarioTime = ScenarioTime(snapshot.Stage);
        var at = requestedAt?.ToUniversalTime() ?? scenarioTime;
        if (at < scenarioTime)
            throw new RoutingException(400, "InvalidTime", "Czas zapytania jest wcześniejszy od zegara symulacji.");

        if (accessible || at >= ScheduleCloses)
            return new(node, snapshot.Revision, at, []);

        var closed = snapshot.ClosedSegments.ToHashSet(StringComparer.Ordinal);
        var options = new List<RouteOption>();
        var destinations = destination is null ? Network.Exits : [destination];

        foreach (var exit in destinations)
        {
            var windowOpens = exit == "exit-north" ? StartTime.AddMinutes(2) : StartTime;
            var departsAt = at >= windowOpens ? at : windowOpens;
            var path = Network.FindPath(node, exit, mode == "driving", closed);
            if (path is null)
                continue;

            var totalSeconds = path.Seconds + (departsAt - at).TotalSeconds;
            options.Add(new($"{node}:{exit}", exit, path.Seconds, 1 / Math.Max(1, totalSeconds), path.Segments,
                path.Nodes, departsAt, departsAt.AddSeconds(path.Seconds)));
        }

        var total = options.Sum(option => option.Weight);
        return new(node, snapshot.Revision, at,
            options.Select(option => option with { Weight = option.Weight / total }).ToArray());
    }

    private static DateTimeOffset ScenarioTime(int stage) => StartTime.AddMinutes(stage switch
    {
        0 => 0,
        1 => 10,
        2 => 13,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    });
}
