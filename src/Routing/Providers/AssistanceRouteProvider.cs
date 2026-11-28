using System.Globalization;

namespace Cassini.Routing;

internal sealed class AssistanceRouteProvider(IRouteProvider provider)
{
    internal async Task<AssistanceRoute> FindAsync(string from, string pickup, string[] exits,
        SituationSnapshot snapshot, CancellationToken cancellation)
    {
        var exclusions = ClosedRoadArea.FromSnapshot(snapshot);
        var start = Coordinates(from);
        var transfer = Coordinates(pickup);
        var approach = await FindLeg(start, transfer);
        if (approach is not null)
        {
            foreach (var exit in exits)
            {
                var outbound = await FindLeg(transfer, Coordinates(exit));
                if (outbound is not null)
                    return new(snapshot.Revision, exit, [], [], approach.Seconds, outbound.Seconds,
                        approach.Geometry, outbound.Geometry, approach.Provenance ?? outbound.Provenance);
            }
        }

        throw new RoutingException(422, "NoEligibleRoute", "No candidate avoids the current closed roads.");

        async Task<Leg?> FindLeg(double[] origin, double[] destination)
        {
            if (origin.SequenceEqual(destination))
                return new([], 0, null);

            var candidate = await provider.FindAsync(new("driving-car", [origin, destination],
                exclusions.Select(area => area.Polygon()).ToArray()), cancellation);
            if (candidate is null || !Near(candidate.Geometry[0], origin) || !Near(candidate.Geometry[^1], destination))
                return null;
            for (var i = 1; i < candidate.Geometry.Length; i++)
                if (exclusions.Any(area => area.Intersects(candidate.Geometry[i - 1], candidate.Geometry[i])))
                    return null;
            return new(candidate.Geometry, candidate.Seconds, candidate.Provenance);
        }
    }

    private static double[] Coordinates(string id)
    {
        var node = Network.Nodes.Single(node => node.Id == id);
        // Match the routed vehicle lane where the exercise provides distinct road and pavement points.
        var lane = Network.Segments.Where(segment => segment.From == id || segment.To == id)
            .Select(segment => (Segment: segment, Line: segment.DrivingGeometry))
            .FirstOrDefault(item => item.Line is { Length: >= 2 });
        var point = lane.Line is null ? [node.Longitude, node.Latitude]
            : lane.Segment.From == id ? lane.Line[0] : lane.Line[^1];
        return point.Select(value => Math.Round(value, 6)).ToArray();
    }

    private static bool Near(double[] left, double[] right)
    {
        const double maxSnapMetres = 100;
        var x = (left[0] - right[0]) * 111_320 * Math.Cos(right[1] * Math.PI / 180);
        var y = (left[1] - right[1]) * 111_320;
        return x * x + y * y <= maxSnapMetres * maxSnapMetres;
    }

    private sealed record Leg(double[][] Geometry, int Seconds, RouteProvenance? Provenance);
}

internal readonly record struct ClosedRoadArea(double West, double South, double East, double North)
{
    internal static ClosedRoadArea[] FromSnapshot(SituationSnapshot snapshot)
    {
        var areas = new List<ClosedRoadArea>();
        foreach (var id in snapshot.ClosedSegments.Order(StringComparer.Ordinal))
        {
            var segment = Network.Segments.FirstOrDefault(segment => segment.Id == id);
            double[][][] lines;
            if (segment is not null)
            {
                lines = new[] { segment.DrivingGeometry, segment.ReverseDrivingGeometry, segment.Geometry }
                    .OfType<double[][]>().ToArray();
                if (lines.Length == 0)
                    lines = [Network.Nodes.Where(node => node.Id == segment.From || node.Id == segment.To)
                        .Select(node => new[] { node.Longitude, node.Latitude }).ToArray()];
            }
            else if (Network.City?.Road(id) is { } road)
                lines = [road.Geometry];
            else
                throw new RoutingException(503, "ClosureGeometryUnavailable",
                    "The current closed road has no known geometry.");

            // Warsaw's bridge edge includes connection roads also used by the detour. The
            // longest straight run of its driving geometry is the crossing over the river.
            var bounds = id == "bridge-2" && segment?.DrivingGeometry is { Length: > 2 } bridge
                ? Bounds(BridgeCrossing(bridge), 25) : Bounds(lines.SelectMany(Core).ToArray(), 10);
            // Merge overlapping boxes so the ORS MultiPolygon has disjoint interiors.
            for (var index = 0; index < areas.Count;)
            {
                var existing = areas[index];
                if (bounds.East < existing.West || existing.East < bounds.West ||
                    bounds.North < existing.South || existing.North < bounds.South)
                {
                    index++;
                    continue;
                }
                bounds = new(Math.Min(bounds.West, existing.West), Math.Min(bounds.South, existing.South),
                    Math.Max(bounds.East, existing.East), Math.Max(bounds.North, existing.North));
                areas.RemoveAt(index);
                index = 0;
            }
            areas.Add(bounds);
        }
        return areas.ToArray();
    }

    private static ClosedRoadArea Bounds(double[][] points, double marginMetres)
    {
        var latitude = points.Average(point => point[1]);
        var margin = marginMetres / 111_320;
        var longitudeMargin = margin / Math.Cos(latitude * Math.PI / 180);
        return new(Math.Round(points.Min(point => point[0]) - longitudeMargin, 6),
            Math.Round(points.Min(point => point[1]) - margin, 6),
            Math.Round(points.Max(point => point[0]) + longitudeMargin, 6),
            Math.Round(points.Max(point => point[1]) + margin, 6));
    }

    private static double[][] BridgeCrossing(double[][] line)
    {
        var scale = Math.Cos(line[0][1] * Math.PI / 180);
        var pair = line.Zip(line.Skip(1)).MaxBy(edge =>
        {
            var x = (edge.First[0] - edge.Second[0]) * scale;
            var y = edge.First[1] - edge.Second[1];
            return x * x + y * y;
        });
        return [Interpolate(pair.First, pair.Second, .25), Interpolate(pair.First, pair.Second, .75)];
    }

    private static double[] Interpolate(double[] a, double[] b, double fraction) =>
        [a[0] * (1 - fraction) + b[0] * fraction, a[1] * (1 - fraction) + b[1] * fraction];

    private static IEnumerable<double[]> Core(double[][] line)
    {
        // Leave space at junctions shared with roads that remain open.
        var first = line.Length / 4;
        var last = line.Length - 1 - first;
        if (first == 0)
        {
            yield return Interpolate(line[0], line[^1], .25);
            yield return Interpolate(line[0], line[^1], .75);
            if (line.Length == 3)
                yield return line[1];
            yield break;
        }
        for (var index = first; index <= last; index++)
            yield return line[index];
    }

    internal double[][][] Polygon() => [[[West, South], [East, South], [East, North], [West, North], [West, South]]];

    internal bool Intersects(double[] start, double[] end)
    {
        var first = 0d;
        var last = 1d;
        return Clip(start[0], end[0], West, East) && Clip(start[1], end[1], South, North);

        bool Clip(double a, double b, double low, double high)
        {
            if (a == b)
                return a >= low && a <= high;
            var entry = (low - a) / (b - a);
            var exit = (high - a) / (b - a);
            first = Math.Max(first, Math.Min(entry, exit));
            last = Math.Min(last, Math.Max(entry, exit));
            return first <= last;
        }
    }
}
