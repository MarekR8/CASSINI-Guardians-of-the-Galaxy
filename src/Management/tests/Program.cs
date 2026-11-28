using System.Text.Json;
using Cassini.Management;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

var clock = new FixedClock(DateTimeOffset.Parse("2026-11-27T19:40:00Z"));
var directory = Path.Combine(Path.GetTempPath(), $"cassini-management-{Guid.NewGuid():N}");
try
{
    var store = new ManagementStore(new FileManagementPersistence(directory));
    var commands = new IncidentCommands(store, clock);
    var population = new StubPopulation(new PopulationEstimate(4872, "GHS-POP sum", "Copernicus GHSL", 2025, 0));
    var queries = new IncidentQueries(store, population);

    var fix = new RecordProgress("exit-14", "case-14", "exit-confirmed", null, null,
        clock.GetUtcNow().AddMinutes(-1), 50.441, 16.654, 8.5, new(true, "simulated", "unknown"), "measured");

    // W2: exit confirmation requires position evidence.
    Expect(commands.RecordProgress(fix with { Latitude = null, Longitude = null, Accuracy = null, Evidence = null,
        Mode = null, CommandId = "exit-14-bare" }), 400, "PositionEvidenceRequired");
    Expect(commands.RecordProgress(fix with { CommandId = "exit-14-egnos", Evidence = new(true, "simulated", "assumed") }),
        400, "PositionEvidenceRequired");
    Expect(commands.RecordProgress(fix with { CommandId = "exit-14-blurred", Accuracy = 250 }), 400, "PositionEvidenceRequired");
    Expect(commands.RecordProgress(fix with { CommandId = "exit-14-future", ObservedAt = clock.GetUtcNow().AddHours(1) }),
        400, "PositionEvidenceRequired");
    Expect(commands.RecordProgress(new("ack-14", "case-14", "acknowledgement", 1, null, null, 50, 16, 5,
        new(true, "none", "unknown"), "measured")), 400, "InvalidProgress");

    // Pickup is recorded separately and does not count as being outside the zone.
    var picked = Accept<EvacuationCase>(commands.RecordProgress(new("pickup-15", "case-15", "pickup", 1, null, null)));
    Check(picked.Progress == ProgressStates.PickedUp && picked.Version == 2, "Pickup moves the case to PickedUp.");
    var afterPickup = await queries.GetIncident(default);
    Check(afterPickup.Counters.ConfirmedOutside == 0 && afterPickup.Counters.Unconfirmed == 3,
        "Pickup leaves the confirmed-outside counter unchanged.");

    // A late field report without an expected version still confirms the exit exactly once.
    var exited = Accept<EvacuationCase>(commands.RecordProgress(fix));
    Check(exited is { Progress: ProgressStates.ExitConfirmed, Exit.Evidence.Egnos: "unknown" }, "The exit stores its evidence.");
    var retry = Accept<EvacuationCase>(commands.RecordProgress(fix));
    Check(retry.Version == exited.Version, "An exact retry returns the original receipt.");
    Expect(commands.RecordProgress(fix with { Accuracy = 9 }), 409, "CommandIdConflict");
    Expect(commands.RecordProgress(fix with { CommandId = "exit-14-again" }), 409, "ExitAlreadyConfirmed");

    var simulatedExit = fix with { CommandId = "exit-15", CaseId = "case-15", Mode = "simulated", ExpectedVersion = 2 };
    Accept<EvacuationCase>(commands.RecordProgress(simulatedExit));
    var counters = (await queries.GetIncident(default)).Counters;
    Check(counters is { ConfirmedOutside: 2, ConfirmedOutsideSimulated: 1, Unconfirmed: 1,
        InZoneStatus: "available", EstimatedWithoutConfirmedExit: 4870 }, "Counters follow confirmed exits only.");

    Expect(commands.RecordProgress(new("stale-16", "case-16", "acknowledgement", 7, null, null)), 409, "VersionConflict");
    Expect(commands.RecordProgress(new("skip-16", "case-16", "moving", 1, null, null)), 409, "InvalidTransition");

    population.Estimate = null;
    var unavailable = (await queries.GetIncident(default)).Counters;
    Check(unavailable is { InZoneEstimate: null, InZoneStatus: "unavailable", EstimatedWithoutConfirmedExit: null },
        "An unavailable estimate is reported, never guessed.");

    // State survives a restart, including the evidence.
    var restored = new ManagementStore(new FileManagementPersistence(directory));
    Check(restored.Read().Incident.Cases.Single(item => item.CaseId == "case-14").Exit?.Accuracy == 8.5,
        "Exit evidence survives a restart.");
    Accept<EvacuationCase>(new IncidentCommands(restored, clock).RecordProgress(fix));

    // EWSS mock broadcast.
    var ewss = new EwssCommands(store, new MockEwssBroadcaster(), clock);
    var zone = JsonSerializer.SerializeToElement(new
    {
        type = "Polygon",
        coordinates = new[] { new[] { new[] { 16.64, 50.43 }, new[] { 16.66, 50.43 }, new[] { 16.66, 50.44 }, new[] { 16.64, 50.43 } } },
    });
    var alert = new BroadcastEwssAlert("ewss-1", "severe", "Powódź. Opuść strefę trasą z aplikacji.", zone);
    var broadcast = Accept<EwssBroadcast>(ewss.Broadcast(alert));
    Check(broadcast is { Delivery: "simulated", Provenance.Mode: "mock", BroadcastId: "ewss-1" },
        "The mock labels the broadcast as simulated and never as delivered.");
    Accept<EwssBroadcast>(ewss.Broadcast(alert));
    Check(store.Read().Broadcasts.Length == 1, "A repeated broadcast command is stored once.");
    Expect(ewss.Broadcast(alert with { CommandId = "ewss-long", Text = new string('x', 101) }), 400, "InvalidEwssAlert");
    Expect(ewss.Broadcast(alert with { CommandId = "ewss-level", Level = "panic" }), 400, "InvalidEwssAlert");
    var open = JsonSerializer.SerializeToElement(new
    {
        type = "Polygon",
        coordinates = new[] { new[] { new[] { 16.64, 50.43 }, new[] { 16.66, 50.43 }, new[] { 16.66, 50.44 }, new[] { 16.6, 50.4 } } },
    });
    Expect(ewss.Broadcast(alert with { CommandId = "ewss-open", Zone = open }), 400, "InvalidEwssAlert");

    Console.WriteLine("Management tests passed: W2 evidence, pickup vs exit, counters, retries, EWSS mock.");
}
finally
{
    if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
}

static T Accept<T>(IResult result)
{
    if (result is not Ok<JsonElement> ok)
        throw new InvalidOperationException($"Expected 200, got {Describe(result)}.");
    return ok.Value.Deserialize<T>(ManagementStore.Json)!;
}

static void Expect(IResult result, int status, string code)
{
    var actual = (result as IStatusCodeHttpResult)?.StatusCode;
    var body = JsonSerializer.SerializeToElement((result as IValueHttpResult)?.Value, ManagementStore.Json);
    if (actual != status || !body.TryGetProperty("code", out var value) || value.GetString() != code)
        throw new InvalidOperationException($"Expected {status} {code}, got {Describe(result)}.");
}

static string Describe(IResult result) =>
    $"{(result as IStatusCodeHttpResult)?.StatusCode} {JsonSerializer.Serialize((result as IValueHttpResult)?.Value)}";

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class StubPopulation(PopulationEstimate? estimate) : ISituationPopulation
{
    internal PopulationEstimate? Estimate { get; set; } = estimate;
    public Task<PopulationEstimate?> ReadEstimate(CancellationToken cancellation) => Task.FromResult(Estimate);
}
