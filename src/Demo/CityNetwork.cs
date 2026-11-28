using System.IO.Compression;
using System.Text.Json;

namespace Cassini.Demo;

internal sealed record CityRoad(string Id, long From, long To, string? Name, bool Major,
    int?[] Walk, int?[] Drive, double[][] Geometry);
internal sealed record CityRoute(int Seconds, string[] Segments, double[][] Geometry);
internal sealed record CityData(double[] Bounds, string OsmTimestamp, CityRoad[] Roads);

internal sealed class CityNetwork
{
    private readonly Dictionary<string, CityRoad> roads;
    private readonly Dictionary<long, List<Arc>> adjacency = [];
    private readonly Dictionary<long, double[]> coordinates = [];
    private readonly Dictionary<(int, int), List<CityRoad>> grid = [];

    private sealed record Arc(long To, CityRoad Road, bool Reverse);
    internal double[] Bounds { get; }
    internal string OsmTimestamp { get; }
    internal int RoadCount => roads.Count;

    private CityNetwork(CityData data)
    {
        Bounds = data.Bounds;
        OsmTimestamp = data.OsmTimestamp;
        roads = data.Roads.ToDictionary(road => road.Id, StringComparer.Ordinal);
        foreach (var road in data.Roads)
        {
            coordinates[road.From] = road.Geometry[0];
            coordinates[road.To] = road.Geometry[^1];
            var line = road.Geometry;
            if (line.All(point => InBounds(point[0], point[1])))
            {
                if (road.Walk[0] is not null || road.Drive[0] is not null) AddArc(road.From, road.To, road, false);
                if (road.Walk[1] is not null || road.Drive[1] is not null) AddArc(road.To, road.From, road, true);
            }
            foreach (var cell in Cells(line.Min(p => p[0]), line.Min(p => p[1]), line.Max(p => p[0]), line.Max(p => p[1])))
            {
                if (!grid.TryGetValue(cell, out var links)) grid[cell] = links = [];
                links.Add(road);
            }
        }
    }

    internal static CityNetwork? Load(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        using var source = File.OpenRead(Path.Combine(directory, "city-network.json.gz"));
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        var data = JsonSerializer.Deserialize<CityData>(gzip, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Warsaw OSM data is empty.");
        if (data.Bounds.Length != 4 || data.Roads.Length == 0)
            throw new InvalidDataException("Warsaw OSM data must include its bounds and roads.");
        return new CityNetwork(data);
    }

    internal bool Contains(string id) => roads.ContainsKey(id);

    internal IEnumerable<CityRoad> IntersectingBounds(double west, double south, double east, double north)
    {
        var seen = new HashSet<string>();
        foreach (var cell in Cells(Math.Max(Bounds[0], west), Math.Max(Bounds[1], south),
                     Math.Min(Bounds[2], east), Math.Min(Bounds[3], north)))
            if (grid.TryGetValue(cell, out var links))
                foreach (var road in links)
                    if (seen.Add(road.Id)) yield return road;
    }

    internal bool InBounds(double longitude, double latitude) =>
        longitude >= Bounds[0] && longitude <= Bounds[2] && latitude >= Bounds[1] && latitude <= Bounds[3];

    internal CityRoute? Route(double[] from, double[] to, bool driving, IReadOnlySet<string> closed)
    {
        var starts = Snap(from, driving, start: true, closed);
        var destinations = Snap(to, driving, start: false, closed);
        if (starts.Count == 0 || destinations.Count == 0)
            return null;
        var best = new Dictionary<long, int>(starts);
        var previous = new Dictionary<long, (long From, Arc Arc)>();
        var queue = new PriorityQueue<long, (int Seconds, long Tie)>();
        foreach (var start in starts)
            queue.Enqueue(start.Key, (start.Value, start.Key));
        long? finish = null;
        var total = int.MaxValue;
        while (queue.TryDequeue(out var node, out var priority))
        {
            if (best[node] != priority.Seconds) continue;
            if (priority.Seconds >= total) break;
            if (destinations.TryGetValue(node, out var lastMetresCost) && priority.Seconds + lastMetresCost < total)
            {
                total = priority.Seconds + lastMetresCost;
                finish = node;
            }
            if (!adjacency.TryGetValue(node, out var arcs)) continue;
            foreach (var arc in arcs)
            {
                var cost = (driving ? arc.Road.Drive : arc.Road.Walk)[arc.Reverse ? 1 : 0];
                if (cost is null || closed.Contains(arc.Road.Id)) continue;
                var nextCost = priority.Seconds + cost.Value;
                if (best.TryGetValue(arc.To, out var current) && current <= nextCost) continue;
                best[arc.To] = nextCost;
                previous[arc.To] = (node, arc);
                queue.Enqueue(arc.To, (nextCost, arc.To));
            }
        }
        if (finish is null) return null;

        var steps = new Stack<Arc>();
        var first = finish.Value;
        while (previous.TryGetValue(first, out var step))
        {
            steps.Push(step.Arc);
            first = step.From;
        }
        var geometry = new List<double[]> { coordinates[first] };
        var seconds = 0;
        foreach (var arc in steps)
        {
            geometry.AddRange((arc.Reverse ? arc.Road.Geometry.Reverse() : arc.Road.Geometry).Skip(1));
            seconds += (driving ? arc.Road.Drive : arc.Road.Walk)[arc.Reverse ? 1 : 0]!.Value;
        }
        return new CityRoute(seconds, steps.Select(arc => arc.Road.Id).ToArray(), geometry.ToArray());
    }

    private Dictionary<long, int> Snap(double[] point, bool driving, bool start, IReadOnlySet<string> closed)
    {
        const double radius = 400;
        var candidates = new Dictionary<long, int>();
        var longitudeScale = 111_320 * Math.Cos(point[1] * Math.PI / 180);
        var dx = radius / longitudeScale;
        var dy = radius / 111_320;
        foreach (var road in IntersectingBounds(point[0] - dx, point[1] - dy, point[0] + dx, point[1] + dy))
        {
            if (closed.Contains(road.Id)) continue;
            var cost = driving ? road.Drive : road.Walk;
            Check(road.From, cost[start ? 0 : 1]);
            Check(road.To, cost[start ? 1 : 0]);
        }
        return candidates.OrderBy(item => item.Value).ThenBy(item => item.Key).Take(24).ToDictionary();

        void Check(long node, int? cost)
        {
            if (cost is null || candidates.ContainsKey(node)) return;
            var coordinate = coordinates[node];
            if (!InBounds(coordinate[0], coordinate[1])) return;
            var x = (coordinate[0] - point[0]) * longitudeScale;
            var y = (coordinate[1] - point[1]) * 111_320;
            var squared = x * x + y * y;
            if (squared < radius * radius)
                candidates[node] = (int)Math.Ceiling(Math.Sqrt(squared) / .8);
        }
    }

    private void AddArc(long from, long to, CityRoad road, bool reverse)
    {
        if (!adjacency.TryGetValue(from, out var arcs)) adjacency[from] = arcs = [];
        arcs.Add(new Arc(to, road, reverse));
    }

    private static IEnumerable<(int, int)> Cells(double west, double south, double east, double north)
    {
        if (west > east || south > north) yield break;
        for (var col = (int)Math.Floor(west * 200); col <= (int)Math.Floor(east * 200); col++)
            for (var row = (int)Math.Floor(south * 200); row <= (int)Math.Floor(north * 200); row++)
                yield return (col, row);
    }
}
