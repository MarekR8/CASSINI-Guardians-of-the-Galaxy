namespace Cassini.Routing;

using Cassini.Demo;

internal sealed record Node(string Id, string Label, double Longitude, double Latitude);
internal sealed record Segment(
    string Id, string From, string To, int? WalkingSeconds, int? DrivingSeconds, bool BothWays,
    double[][]? Geometry = null, double[][]? DrivingGeometry = null,
    double[][]? ReverseDrivingGeometry = null, int? ReverseDrivingSeconds = null);
internal sealed record GraphPath(string[] Nodes, string[] Segments, int Seconds);

internal static class Network
{
    internal static Node[] Nodes { get; private set; } =
    [
        new("walk-start", "Punkt startu", 16.6500, 50.4360),
        new("pickup", "Punkt przesiadkowy", 16.6520, 50.4350),
        new("east-junction", "Dojście do mostu", 16.6560, 50.4350),
        new("exit-east", "Wyjście wschodnie", 16.6590, 50.4350),
        new("north-junction", "Skrzyżowanie północne", 16.6530, 50.4385),
        new("exit-north", "Wyjście północne", 16.6540, 50.4410),
    ];

    internal static Segment[] Segments { get; private set; } =
    [
        new("walk-to-pickup", "walk-start", "pickup", 300, null, false),
        new("east-link", "pickup", "east-junction", 360, 150, true),
        new("bridge-2", "east-junction", "exit-east", 120, 90, true),
        new("north-link", "pickup", "north-junction", 600, 260, true),
        new("north-exit-link", "north-junction", "exit-north", 180, 80, true),
        new("outside-connector", "exit-east", "exit-north", null, 300, true),
    ];

    internal static readonly string[] Exits = ["exit-east", "exit-north"];
    internal static DemoNetwork? Warsaw { get; private set; }
    internal static CityNetwork? City { get; private set; }

    internal static void Configure(DemoNetwork? network, CityNetwork? city = null)
    {
        if (network is null) return;
        Warsaw = network;
        City = city;
        Nodes = network.Nodes.Select(node => new Node(node.Id, node.Label, node.Longitude, node.Latitude)).ToArray();
        Segments = network.Segments.Select(link => new Segment(link.Id, link.From, link.To, link.WalkingSeconds,
            link.DrivingSeconds, link.BothWays, link.Geometry, link.DrivingGeometry, link.ReverseDrivingGeometry,
            link.ReverseDrivingSeconds)).ToArray();
    }

    internal static bool Contains(string? node) => Nodes.Any(candidate => candidate.Id == node);
    internal static bool ContainsSegment(string id) => Segments.Any(segment => segment.Id == id)
        || City?.Contains(id) == true;

    internal static GraphPath? FindPath(string start, string destination, bool driving, IReadOnlySet<string> closed)
    {
        var queue = new PriorityQueue<string, (int Duration, int Sequence)>();
        var best = new Dictionary<string, int>(StringComparer.Ordinal) { [start] = 0 };
        var previous = new Dictionary<string, (string Node, string Segment)>(StringComparer.Ordinal);
        var sequence = 0;
        queue.Enqueue(start, (0, sequence));

        while (queue.TryDequeue(out var node, out var cost))
        {
            if (cost.Duration != best[node])
                continue;
            if (node == destination)
                return Reconstruct(start, destination, cost.Duration, previous);

            foreach (var segment in Segments)
            {
                if (closed.Contains(segment.Id))
                    continue;
                var next = segment.From == node ? segment.To
                    : segment.BothWays && segment.To == node ? segment.From
                    : null;
                if (next is null)
                    continue;
                var seconds = driving
                    ? Warsaw is not null && segment.To == node ? segment.ReverseDrivingSeconds : segment.DrivingSeconds
                    : segment.WalkingSeconds;
                if (seconds is null)
                    continue;

                var duration = cost.Duration + seconds.Value;
                if (best.TryGetValue(next, out var current) && current <= duration)
                    continue;
                best[next] = duration;
                previous[next] = (node, segment.Id);
                queue.Enqueue(next, (duration, ++sequence));
            }
        }

        return null;
    }

    private static GraphPath Reconstruct(
        string start, string destination, int seconds, Dictionary<string, (string Node, string Segment)> previous)
    {
        var segments = new List<string>();
        var nodes = new List<string> { destination };
        for (var node = destination; node != start;)
        {
            var step = previous[node];
            segments.Add(step.Segment);
            nodes.Add(step.Node);
            node = step.Node;
        }
        segments.Reverse();
        nodes.Reverse();
        return new(nodes.ToArray(), segments.ToArray(), seconds);
    }
}
