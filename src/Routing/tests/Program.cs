using System.Net;
using System.Net.Http.Json;
using Cassini.Routing;

EvacuationFixtureTests.Run();
EvacuationInputTests.Run();

var handler = new SituationStub();
using var http = new HttpClient(handler) { BaseAddress = new Uri("http://situation.test/") };
var situation = new SituationGateway(http);
var state = Path.Combine(AppContext.BaseDirectory, $"cassini-routing-{Guid.NewGuid():N}.json");
try
{
    var journal = new DrawJournal(state);
    var queries = new RoutingQueries(situation, journal);
    var commands = new RoutingCommands(situation, journal);
    var drawEast = new DrawCommand("test-east", "walk-start", 0, Seed: 1);
    var drawNorth = new DrawCommand("test-north", "walk-start", 0, Seed: 42);
    var baseline = await DistributionAsync(queries);

    Check(baseline.Options.Length == 2, "Both schedules reach an available exit.");
    var east = baseline.Options.Single(option => option.Exit == "exit-east");
    var north = baseline.Options.Single(option => option.Exit == "exit-north");
    Check(east.Seconds == 780 && north.Seconds == 1080, "Travel times match the fixture.");
    Check(east.DepartsAt == RoutingQueries.StartTime && north.DepartsAt == RoutingQueries.StartTime.AddMinutes(2),
        "A route cannot depart before the advertised schedule.");
    Check(east.Weight > north.Weight && Math.Abs(east.Weight + north.Weight - 1) < 1e-10,
        "The shorter option is favored and weighted draws can reach either exit.");

    var eastResult = await commands.DrawAsync(drawEast, default);
    var northResult = await commands.DrawAsync(drawNorth, default);
    Check(eastResult.Route.Exit == "exit-east" && northResult.Route.Exit == "exit-north",
        "Distinct reproducible samples draw from the node's distribution.");
    var restored = new RoutingCommands(situation, new DrawJournal(state));
    var retry = await restored.DrawAsync(drawEast, default);
    Check(retry.Route.Exit == eastResult.Route.Exit && retry.Route.Segments.SequenceEqual(eastResult.Route.Segments),
        "After restart an exact retry returns the existing draw.");
    await RejectAsync("CommandConflict", () => restored.DrawAsync(drawEast with { Seed = 24 }, default));

    handler.Snapshot = new(1, 1, ["bridge-2"], new() { ["bridge-2"] = 1 });
    var detour = await DistributionAsync(queries);
    Check(detour.Options.Single().Exit == "exit-north" && detour.Options[0].Weight == 1,
        "A closed bridge removes the affected route before drawing.");
    Check((await queries.DrawStatusAsync("test-east", default)) is { State: "Withdrawn", Route: null } &&
          (await queries.DrawStatusAsync("test-north", default)) is { State: "Active" },
        "A closure withdraws the affected draw and preserves the unrelated draw.");
    await RejectAsync("RevisionConflict", () => commands.DrawAsync(drawEast, default));
    var driving = await queries.AssistanceAsync("exit-east", "pickup", default);
    Check(driving.Exit == "exit-north" && driving.ApproachSegments.Contains("outside-connector") &&
          !driving.OutboundSegments.Contains("bridge-2"), "Both transport legs exclude the closed bridge.");
    var alreadyAtPickup = await queries.AssistanceAsync("pickup", "pickup", default);
    Check(alreadyAtPickup.ApproachSeconds == 0 && alreadyAtPickup.ApproachSegments.Length == 0,
        "A unit at pickup can replan only its outbound leg.");

    handler.Snapshot = new(2, 2, ["bridge-2", "north-link"], new() { ["bridge-2"] = 1, ["north-link"] = 2 });
    Check((await DistributionAsync(queries)).Options.Length == 0, "Two closures leave no walkable exit.");
    await RejectAsync("NoEligibleRoute", () => commands.DrawAsync(new("no-route", "walk-start", 2), default));
    await RejectAsync("NoEligibleRoute", () => queries.AssistanceAsync("exit-east", "pickup", default));

    handler.Snapshot = new(3, 0, [], new() { ["bridge-2"] = 3, ["north-link"] = 3 });
    Check((await queries.DrawStatusAsync("test-east", default)) is { State: "Withdrawn", Route: null },
        "Reopening an affected edge cannot reactivate a withdrawn draw.");
    Check((await queries.DistributionAsync("pickup", RoutingQueries.StartTime.AddMinutes(40),
        null, null, false, default)).Options.Length == 0, "No departures occur after the schedule closes.");
    Check((await queries.DistributionAsync("pickup", null, null, null, true, default)).Options.Length == 0,
        "The demo cannot offer a route when accessibility must be verified.");
    handler.Available = false;
    await RejectAsync("SituationUnavailable", () => DistributionAsync(queries));

    Console.WriteLine("Routing checks passed.");
}
finally
{
    File.Delete(state);
    File.Delete(state + ".tmp");
}

static Task<Distribution> DistributionAsync(RoutingQueries queries) =>
    queries.DistributionAsync("walk-start", null, null, null, false, default);

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
}

static async Task RejectAsync(string expectedCode, Func<Task> action)
{
    try
    {
        await action();
    }
    catch (RoutingException error) when (error.Code == expectedCode)
    {
        return;
    }
    throw new InvalidOperationException($"Expected routing error {expectedCode}.");
}

internal sealed class SituationStub : HttpMessageHandler
{
    internal SituationSnapshot Snapshot { get; set; } = new(0, 0, [], new());
    internal bool Available { get; set; } = true;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri?.AbsolutePath != "/queries/snapshot")
            throw new InvalidOperationException("Routing requested an unexpected Situation API.");
        var response = Available
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Snapshot) }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        return Task.FromResult(response);
    }
}
