using System.Text;
using System.Text.Json;
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
Check(OfflineScenario.RestoreRoute(0, "exit-east")?.Route.Exit == "exit-east" &&
      OfflineScenario.RestoreRoute(1, "exit-east") is null &&
      OfflineScenario.RestoreRoute(2, "exit-north") is null &&
      OfflineScenario.RestoreRoute(0, null) is null,
    "Only a saved route allowed by the current exercise stage can be reopened.");
IEwssReceiver receiver = new MockEwssReceiver();
var alert = receiver.Receive();
Check(alert.Simulated && !string.IsNullOrWhiteSpace(alert.Level) && !string.IsNullOrWhiteSpace(alert.Text),
    "The mock alert must carry a level and text and identify itself as simulated.");
Check(ResidentFlow.NextAction(false, false, baseline.Count > 0, false) == ResidentAction.FindRoute &&
      ResidentFlow.NextAction(false, true, baseline.Count > 0, false) == ResidentAction.ConfirmExit &&
      ResidentFlow.NextAction(false, false, detour.Count > 0, false) == ResidentAction.FindRoute &&
      ResidentFlow.NextAction(false, true, noExit.Count > 0, false) == ResidentAction.RouteUnavailable &&
      ResidentFlow.NextAction(false, true, baseline.Count > 0, true) == ResidentAction.ExitSaved &&
      ResidentFlow.NextAction(true, true, baseline.Count > 0, false) == ResidentAction.ReturnToRoute,
    "The resident action must follow route availability, closure, saved exit and archive view.");

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

var exitDirectory = Directory.CreateTempSubdirectory();
try
{
    var queue = new LocalExitReports(exitDirectory.FullName);
    var measuredAt = new DateTimeOffset(2026, 11, 28, 14, 20, 0, TimeSpan.FromHours(1));
    var position = new GalileoPosition(50.431, 16.653, 8, measuredAt);
    var measured = ExitReport.Create(position, simulated: false);
    queue.Save(measured);
    queue.Save(ExitReport.Create(position, simulated: true));
    File.WriteAllText(Path.Combine(exitDirectory.FullName, "incomplete.json.tmp"), "");

    var pending = new LocalExitReports(exitDirectory.FullName).PendingFiles();
    Check(pending.Length == 2, "Complete reports survive reopening and incomplete writes stay out of the queue.");
    using var saved = JsonDocument.Parse(File.ReadAllText(
        pending.Single(path => Path.GetFileNameWithoutExtension(path) == measured.CommandId.ToString("D"))));
    var report = saved.RootElement;
    var evidence = report.GetProperty("evidence");
    Check(report.GetProperty("commandId").GetGuid() == measured.CommandId &&
          report.GetProperty("observedAt").GetDateTimeOffset() == measuredAt &&
          report.GetProperty("observedAt").GetDateTimeOffset().Offset == TimeSpan.Zero &&
          report.GetProperty("accuracy").GetDouble() == 8 &&
          report.GetProperty("mode").GetString() == "measured" &&
          evidence.GetProperty("galileoUsed").GetBoolean() &&
          evidence.GetProperty("osnma").GetString() == "simulated" &&
          evidence.GetProperty("egnos").GetString() == "unknown",
        "The saved report must preserve identity, UTC time, accuracy and explicitly label position evidence.");
    using var simulated = JsonDocument.Parse(File.ReadAllText(
        pending.Single(path => Path.GetFileNameWithoutExtension(path) != measured.CommandId.ToString("D"))));
    Check(simulated.RootElement.GetProperty("mode").GetString() == "simulated",
        "Sample positions must be distinguishable from receiver measurements.");
}
finally
{
    exitDirectory.Delete(recursive: true);
}

Console.WriteLine("Offline Android schedule, closure, GeoJSON, exit report and EWSS tests passed.");

static void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}
