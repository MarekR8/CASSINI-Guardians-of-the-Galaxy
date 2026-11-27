using System.Text;
using Cassini.Offline;

var baseline = OfflineScenario.Distribution(OfflineScenario.InitialNode, OfflineScenario.Closures(0), 0);
Check(baseline.Count == 2 && Math.Abs(baseline.Sum(route => route.Weight) - 1) < 1e-12,
    "Baseline weights must sum to one.");
Check(baseline[0].DepartsAt.Minute == 20 && baseline[1].DepartsAt.Minute == 22,
    "The north exit must account for its opening time.");
Check(OfflineScenario.Draw(baseline, new Random(1))?.Route.Exit == "exit-east" &&
      OfflineScenario.Draw(baseline, new Random(42))?.Route.Exit == "exit-north",
    "The baseline distribution must allow both exits.");

var detour = OfflineScenario.Distribution(OfflineScenario.LaterNode, OfflineScenario.Closures(1), 1);
Check(detour.Count == 1 && detour[0].Route.Exit == "exit-north" && detour[0].Weight == 1,
    "Closing the bridge must leave only the northern option.");
Check(!detour[0].Route.Segments.Contains("bridge-2"), "The detour must exclude the closed bridge.");
var noExit = OfflineScenario.Distribution(OfflineScenario.LaterNode, OfflineScenario.Closures(2), 2);
Check(OfflineScenario.Draw(noExit, new Random(1)) is null, "Never draw a route when all exits are blocked.");

const string layer = """
    {"type":"FeatureCollection","productId":"sample-for-test","observedAt":"2026-11-27T19:20:00Z",
      "features":[{"type":"Feature","properties":{},"geometry":{"type":"Polygon",
        "coordinates":[[[16.5,50.4],[16.6,50.4],[16.6,50.5],[16.5,50.4]]]}}]}
    """;
var parsed = CopernicusLayer.Read(Encoding.UTF8.GetBytes(layer));
Check(parsed.Product == "sample-for-test" && parsed.ObservedAt?.Hour == 19 && parsed.Polygons.Count == 1,
    "Import preserves polygon and evidence metadata.");

try
{
    CopernicusLayer.Read(Encoding.UTF8.GetBytes(layer.Replace("[16.5,50.4]]]", "[16.5,90.5]]]")));
    throw new Exception("Invalid WGS 84 coordinates were accepted.");
}
catch (InvalidDataException)
{
}

Console.WriteLine("Offline Android schedule, weighted draw, closure and GeoJSON tests passed.");

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}
