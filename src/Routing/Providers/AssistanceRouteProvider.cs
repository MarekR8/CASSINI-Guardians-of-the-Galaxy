using System.Globalization;
using Cassini.Demo;

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
            if (exclusions.Any(area => area.RoadBounds.Intersects(candidate.Geometry)))
                return null;
            return new(candidate.Geometry, candidate.Seconds, candidate.Provenance);
        }
    }

    private static double[] Coordinates(string id)
    {
        // The sample's pedestrian marker is away from the vehicular exit on the ORS graph.
        if (id == "exit-north")
            return [16.65554, 50.440838];

        var node = Network.Nodes.Single(node => node.Id == id);
        return [Math.Round(node.Longitude, 6), Math.Round(node.Latitude, 6)];
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
    internal RoadBounds RoadBounds => new(West, South, East, North);

    internal static ClosedRoadArea[] FromSnapshot(SituationSnapshot snapshot)
    {
        var areas = new List<ClosedRoadArea>();
        foreach (var id in snapshot.ClosedSegments.Order(StringComparer.Ordinal))
        {
            var bounds = ForSegment(id) ?? throw new RoutingException(503, "ClosureGeometryUnavailable",
                "The current closed road has no known geometry.");
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

    internal static ClosedRoadArea? ForSegment(string id)
    {
        var segment = Network.Segments.FirstOrDefault(segment => segment.Id == id);
        if (segment is null)
            return Network.City?.Road(id) is { } road ? Bounds(Core(road.Geometry).ToArray(), 10) : null;

        var lines = new[] { segment.DrivingGeometry, segment.ReverseDrivingGeometry, segment.Geometry }
            .OfType<double[][]>().ToArray();
        if (lines.Length == 0)
            lines = [Network.Nodes.Where(node => node.Id == segment.From || node.Id == segment.To)
                .Select(node => new[] { node.Longitude, node.Latitude }).ToArray()];
        return Bounds(lines.SelectMany(Core).ToArray(), 10);
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

}
