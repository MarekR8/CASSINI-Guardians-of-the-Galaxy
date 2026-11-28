using System.Text.Json;

namespace Cassini.Demo;

internal sealed record DemoNode(string Id, string Label, double Longitude, double Latitude);
internal sealed record DemoSegment(
    string Id, string From, string To, int? WalkingSeconds, int? DrivingSeconds, bool BothWays,
    double[][]? Geometry, double[][]? DrivingGeometry, double[][]? ReverseDrivingGeometry, int? ReverseDrivingSeconds);

internal sealed record DemoNetwork(
    DemoNode[] Nodes, DemoSegment[] Segments, string[] Exits, double[] Bounds,
    double[][][] ContextRoads, double[][][] Waterways, string Attribution, string OsmTimestamp)
{
    internal static DemoNetwork? Load(string? scenario)
    {
        if (scenario is null or "" or "sample")
            return null;
        if (scenario != "warsaw")
            throw new InvalidDataException($"Unknown demo scenario '{scenario}'.");

        var path = Path.Combine(AppContext.BaseDirectory, "warsaw-graph.json");
        return JsonSerializer.Deserialize<DemoNetwork>(File.ReadAllBytes(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Warsaw graph is empty.");
    }
}
