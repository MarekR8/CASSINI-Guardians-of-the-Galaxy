// SH-T06 replay without a broker: the SH-W5 instruction board against contracts/events/replay.
using System.Text.Json;
using Cassini.Messaging;
using Cassini.Sheltering.Infrastructure;
using Cassini.Sheltering.Publication;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "replay", "sh-w5-instructions.replay.json")));
var root = fixture.RootElement;
var owners = root.GetProperty("authority");
var authority = new FixtureAuthority(owners.GetProperty("assignment").Deserialize<AssignmentFacts>(json)!,
    owners.GetProperty("route").Deserialize<RouteInstructionView>(json)!);
var steps = root.GetProperty("steps").EnumerateArray().Select(step => (
    Note: step.GetProperty("note").GetString()!,
    Event: step.GetProperty("event").Deserialize<IntegrationEvent>(json)!,
    Expected: (step.GetProperty("expect").GetProperty("state").GetString(), Route(step.GetProperty("expect"))))).ToArray();
var failures = 0;

var board = new InstructionBoard(authority);
foreach (var step in steps)
{
    await board.HandleAsync(step.Event, CancellationToken.None);
    Check(await Snapshot(board) == step.Expected, step.Note);
}
var final = steps[^1].Expected;
Check(authority.RouteReads == 1 && authority.AssignmentReads == 1, "Only the two gaps were read from the owners.");

foreach (var step in steps)
    await board.HandleAsync(step.Event, CancellationToken.None);
Check(await Snapshot(board) == final, "Redelivering the whole stream changes nothing.");

var reordered = new InstructionBoard(authority);
foreach (var step in steps.Reverse())
    await reordered.HandleAsync(step.Event, CancellationToken.None);
Check(await Snapshot(reordered) == final, "The reversed stream ends in the same state.");

var bus = new InMemoryEventBus();
var subscribed = new InstructionBoard(authority);
await using (await bus.SubscribeAsync(subscribed.Queue, subscribed.Bindings, subscribed.HandleAsync, CancellationToken.None))
{
    foreach (var step in steps)
        await bus.PublishAsync(step.Event, CancellationToken.None);
    for (var attempt = 0; attempt < 100 && await Snapshot(subscribed) != final; attempt++)
        await Task.Delay(20);
}
Check(await Snapshot(subscribed) == final, "The board bindings receive the stream through the in-memory bus.");

Console.WriteLine(failures == 0 ? $"Sheltering SH-W5 replay passed: {steps.Length} events." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

async Task<(string?, int?)> Snapshot(InstructionBoard target)
{
    var view = JsonSerializer.SerializeToElement(await target.Read("incident-42", [], CancellationToken.None), json);
    if (view.GetProperty("entries").EnumerateArray().SingleOrDefault() is not { ValueKind: JsonValueKind.Object } entry)
        return (null, null);
    Check(view.GetProperty("counts").GetProperty(entry.GetProperty("state").GetString()!).GetInt32() == 1, "Counted once.");
    return (entry.GetProperty("state").GetString(), Route(entry));
}

static int? Route(JsonElement element) =>
    element.GetProperty("routeVersion") is { ValueKind: JsonValueKind.Number } version ? version.GetInt32() : null;

void Check(bool condition, string message)
{
    if (condition)
        return;
    failures++;
    Console.Error.WriteLine("FAIL: " + message);
}

internal sealed class FixtureAuthority(AssignmentFacts assignment, RouteInstructionView route) : IInstructionAuthority
{
    internal int AssignmentReads { get; private set; }
    internal int RouteReads { get; private set; }

    public AssignmentFacts? Assignment(string key)
    {
        AssignmentReads++;
        return assignment;
    }

    public Task<RouteInstructionView> Route(string key, CancellationToken cancellation)
    {
        RouteReads++;
        return Task.FromResult(route);
    }
}
