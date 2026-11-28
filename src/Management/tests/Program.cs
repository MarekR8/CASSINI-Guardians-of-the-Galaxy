using System.Text.Json;
using Cassini.Management;
using Cassini.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

var clock = new FixedClock(DateTimeOffset.Parse("2026-11-27T19:40:00Z"));
var directory = Path.Combine(Path.GetTempPath(), $"cassini-management-{Guid.NewGuid():N}");
try
{
    await GroupingDurabilityTests.RunAsync(clock, Path.Combine(directory, "grouping"));
    var persistence = new FileManagementPersistence(directory);
    var store = new ManagementStore(persistence);
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

    // Outbox: milestones and alerts commit their events together with the change; positions do not publish.
    var outboxTypes = store.Read().Outbox.Select(message => message.Type).ToArray();
    Check(outboxTypes.Count(type => type == EventTypes.CaseProgressChanged) == 3 &&
          !outboxTypes.Contains(EventTypes.HazardAlertRecorded), "Each accepted milestone adds one event.");
    Check(store.Read().Outbox.Last(message => message.SubjectId == "case-14")
        .ReadPayload<CaseProgressChangedPayload>() is { Progress: ProgressStates.ExitConfirmed, EvidenceMode: "measured" },
        "The exit event carries the milestone and evidence mode.");
    Accept<HazardAlert>(commands.RecordAlert(new("alert-1", null)));
    Accept<EvacuationCase>(commands.RecordProgress(new("pos-16", "case-16", "position", 1, "pickup", null)));
    Check(store.Read().Outbox.Count(message => message.Type == EventTypes.HazardAlertRecorded) == 1 &&
          store.Read().Outbox.All(message => message.SubjectId != "case-16"), "A position update publishes nothing.");

    var bus = new InMemoryEventBus();
    var received = new List<IntegrationEvent>();
    await using (await bus.SubscribeAsync("test.management", ["management.#"], (message, _) =>
                 {
                     lock (received) received.Add(message);
                     return Task.CompletedTask;
                 }, default))
    {
        var publisher = new OutboxPublisher(new ManagementOutbox(persistence), bus, new OutboxSignal(),
            NullLogger<OutboxPublisher>.Instance);
        var pending = store.Read().Outbox.Length;
        Check(await publisher.PublishPendingAsync(default) == pending && store.Read().Outbox.Length == 0,
            "Published events leave the outbox.");
        for (var attempt = 0; attempt < 50 && received.Count < pending; attempt++)
            await Task.Delay(20);
        Check(received.Count == pending, "Every outbox event reaches the bus.");
    }

    // AssignmentChanged.v1: deduplicated by eventId, ordered by subjectVersion, pickup never confirms the exit.
    var consumer = new AssignmentChangedConsumer(store);
    IntegrationEvent Assignment(string id, long version, string unitState) => IntegrationEvent.Create(
        EventTypes.AssignmentChanged, "dispatch", "assignment-1", version, clock.GetUtcNow(),
        new AssignmentChangedPayload("request-case-16-1", "assignment-1", "case-16", "unit-3", unitState, clock.GetUtcNow()))
        with { EventId = id };
    await consumer.HandleAsync(Assignment("evt-2", 2, "PickupConfirmed"), default);
    await consumer.HandleAsync(Assignment("evt-1", 1, "Assigned"), default);
    await consumer.HandleAsync(Assignment("evt-2", 2, "PickupConfirmed"), default);
    var assisted = store.Read().Incident.Cases.Single(item => item.CaseId == "case-16");
    Check(assisted is { Assistance.State: "PickupConfirmed", Assistance.SubjectVersion: 2, Progress: ProgressStates.Open },
        "A late older event cannot overwrite a newer one and pickup leaves the exit unconfirmed.");
    Check(store.Read().Inbox.Count(entry => entry.EndsWith(":evt-2")) == 1, "The processed event is recorded once.");
    Check((await queries.GetIncident(default)).Counters.ConfirmedOutside == 2, "Assistance does not change counters.");

    Console.WriteLine("Management tests passed: W2 evidence, pickup vs exit, counters, retries, EWSS mock, outbox, inbox.");
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
