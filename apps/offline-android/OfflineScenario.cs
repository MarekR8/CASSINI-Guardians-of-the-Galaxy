namespace Cassini.Offline;

internal readonly record struct MapPoint(double Longitude, double Latitude);
internal sealed record Segment(string Id, string From, string To, int Seconds, bool BothWays);
internal sealed record WalkingRoute(string Exit, int Seconds, IReadOnlyList<string> Segments);
internal sealed record ScheduledWalkingRoute(WalkingRoute Route, DateTimeOffset DepartsAt, DateTimeOffset ArrivesAt, double Weight);

internal static class OfflineScenario
{
    internal const string InitialNode = "walk-start";
    internal const string LaterNode = "pickup";
    internal static readonly string[] Exits = ["exit-east", "exit-north"];
    internal static readonly MapPoint[] Flood =
    [
        new(16.6470, 50.4320), new(16.6570, 50.4320),
        new(16.6570, 50.4380), new(16.6470, 50.4380),
    ];

    internal static readonly IReadOnlyDictionary<string, MapPoint> Nodes = new Dictionary<string, MapPoint>
    {
        [InitialNode] = new(16.6500, 50.4360),
        [LaterNode] = new(16.6520, 50.4350),
        ["east-junction"] = new(16.6560, 50.4350),
        ["exit-east"] = new(16.6590, 50.4350),
        ["north-junction"] = new(16.6530, 50.4385),
        ["exit-north"] = new(16.6540, 50.4410),
    };

    internal static readonly Segment[] Segments =
    [
        new("walk-to-pickup", InitialNode, LaterNode, 300, false),
        new("east-link", LaterNode, "east-junction", 360, true),
        new("bridge-2", "east-junction", "exit-east", 120, true),
        new("north-link", LaterNode, "north-junction", 600, true),
        new("north-exit-link", "north-junction", "exit-north", 180, true),
    ];

    internal static IReadOnlySet<string> Closures(int stage) => stage switch
    {
        0 => new HashSet<string>(),
        1 => new HashSet<string> { "bridge-2" },
        2 => new HashSet<string> { "bridge-2", "north-link" },
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    internal static IReadOnlyList<ScheduledWalkingRoute> Distribution(string start, IReadOnlySet<string> closed, int stage)
    {
        var baseline = new DateTimeOffset(2026, 11, 27, 19, 20, 0, TimeSpan.Zero);
        var at = baseline.AddMinutes(stage switch
        {
            0 => 0,
            1 => 10,
            2 => 13,
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        });
        var options = FindWalkingRoutes(start, closed).Select(route =>
        {
            var opensAt = route.Exit == "exit-east" ? baseline : baseline.AddMinutes(2);
            var departsAt = at > opensAt ? at : opensAt;
            return new ScheduledWalkingRoute(route, departsAt, departsAt.AddSeconds(route.Seconds),
                1 / Math.Max(1, route.Seconds + (departsAt - at).TotalSeconds));
        }).ToArray();
        var total = options.Sum(option => option.Weight);
        return options.Select(option => option with { Weight = option.Weight / total }).ToArray();
    }

    internal static ScheduledWalkingRoute? Draw(IReadOnlyList<ScheduledWalkingRoute> distribution, Random random)
    {
        if (distribution.Count == 0)
            return null;

        var sample = random.NextDouble();
        foreach (var option in distribution)
        {
            sample -= option.Weight;
            if (sample < 0)
                return option;
        }
        return distribution[^1];
    }

    private static IEnumerable<WalkingRoute> FindWalkingRoutes(string start, IReadOnlySet<string> closed)
    {
        if (!Nodes.ContainsKey(start))
            yield break;

        var queue = new PriorityQueue<string, int>();
        var best = new Dictionary<string, int> { [start] = 0 };
        var previous = new Dictionary<string, (string Node, Segment Segment)>();
        queue.Enqueue(start, 0);

        while (queue.TryDequeue(out var node, out var duration))
        {
            if (duration != best[node])
                continue;

            foreach (var segment in Segments)
            {
                if (closed.Contains(segment.Id))
                    continue;

                var next = segment.From == node ? segment.To
                    : segment.BothWays && segment.To == node ? segment.From
                    : null;
                if (next is null)
                    continue;

                var candidate = duration + segment.Seconds;
                if (best.TryGetValue(next, out var current) && current <= candidate)
                    continue;

                best[next] = candidate;
                previous[next] = (node, segment);
                queue.Enqueue(next, candidate);
            }
        }

        foreach (var exit in Exits.Where(best.ContainsKey))
        {
            var path = new List<string>();
            for (var node = exit; node != start;)
            {
                var step = previous[node];
                path.Add(step.Segment.Id);
                node = step.Node;
            }
            path.Reverse();
            yield return new WalkingRoute(exit, best[exit], path);
        }
    }
}
