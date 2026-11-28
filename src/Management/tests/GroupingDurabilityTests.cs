using System.Collections.Concurrent;
using System.Text.Json;
using Cassini.Management;
using Cassini.Management.Grouping;
using Cassini.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

internal static class GroupingDurabilityTests
{
    internal static async Task RunAsync(TimeProvider clock, string directory)
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "grouping-fixtures");
        using var household = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "household-split.json")));
        using var care = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtures, "care-handover.json")));
        var register = Read<RegisterEvacuationGroup>(household, "register");
        var approve = Read<ApproveTravelPlan>(household, "approve");
        var store = new ManagementStore(new FileManagementPersistence(directory));
        var options = GroupingEvidence.Load(Path.Combine(fixtures, "sample-grouping.json"));
        var commands = new GroupingCommands(store, options, clock);
        var review = new FlagTravelPlanForReview("review-1", register.IncidentId, register.GroupId, 1,
            "ApproachClosed", "synthetic-closure");

        Expect(commands.Register(register), 200);
        Expect(commands.Register(register), 200);
        Expect(commands.Register(register with { IncidentId = "other-incident" }), 404);
        Expect(commands.Approve(approve), 200);
        Expect(commands.Flag(review), 200);
        Expect(commands.Approve(approve with
        {
            CommandId = "approve-2", ExpectedPlanRevision = 1, EvidenceId = "household-separated"
        }), 200);
        Expect(commands.Flag(review with { CommandId = "stale-review-1" }), 409);
        Expect(commands.UpdateComposition(Read<UpdateGroupComposition>(care, "updateComposition")), 200);
        Expect(commands.RecordHandover(Read<RecordCareHandover>(care, "handover")), 200);
        Expect(commands.Approve(Read<ApproveTravelPlan>(care, "approve")), 200);

        var position = new RecordProgress("grouping-position", "case-14", "position", 1, "east-junction", options.ExerciseAt);
        Expect(new IncidentCommands(store, clock).RecordProgress(position), 200);
        Expect(new IncidentCommands(store, clock).RecordProgress(position), 200);

        var restarted = new FileManagementPersistence(directory);
        var saved = restarted.Read();
        var group = saved.Groups![register.GroupId!];
        Check(group.Plans.Select(plan => plan.ReviewReason).SequenceEqual(
            ["ApproachClosed", "CompositionChanged", "CaseVersionChanged"]), "Only the affected plan revision is flagged.");
        Check(saved.Incident.Cases.Single(item => item.CaseId == "case-14").Version == 2 &&
            saved.Receipts.ContainsKey(position.CommandId!), "Case, review and successful receipt survive together.");
        var outbox = new ManagementOutbox(restarted);
        var pending = outbox.PendingEvents();
        Check(pending.Count == 10, "Only successful changes emit events and retries emit none.");
        var planEvents = pending.Where(message => message.SubjectId.EndsWith("/plans", StringComparison.Ordinal)).ToArray();
        Check(planEvents.Select(message => message.SubjectVersion).SequenceEqual([1L, 2L, 3L, 4L, 5L, 6L]),
            "Plan events preserve approval and review ordering across plan revisions.");
        Check(planEvents[^1].Type == EventTypes.TravelPlanReviewRequired &&
            planEvents[^1].CausationId == position.CommandId && planEvents[^1].Payload.GetProperty("planRevision").GetInt32() == 3,
            "The persisted review event names the exact affected plan.");
        var handover = pending.Single(message => message.Type == EventTypes.CareHandoverRecorded).Payload;
        Check(handover.TryGetProperty("handoverRef", out _) && !handover.TryGetProperty("caseId", out _) &&
            !handover.TryGetProperty("authorizedBy", out _), "Care events contain references instead of private details.");

        var bus = new InMemoryEventBus();
        var received = new ConcurrentQueue<string>();
        await using var subscriber = await bus.SubscribeAsync("test.grouping", ["grouping.#"], (message, _) =>
        {
            received.Enqueue(message.EventId);
            return Task.CompletedTask;
        }, default);
        var publisher = new OutboxPublisher(outbox, bus, new OutboxSignal(), NullLogger<OutboxPublisher>.Instance);
        Check(await publisher.PublishPendingAsync(default) == pending.Count, "The existing outbox publishes all recovered events.");
        for (var attempt = 0; attempt < 50 && received.Count < pending.Count; attempt++)
            await Task.Delay(20);
        Check(received.ToArray().SequenceEqual(pending.Select(message => message.EventId)) &&
            new FileManagementPersistence(directory).Read().Outbox.Length == 0, "The bus receives the events and confirmations persist.");
        Console.WriteLine("Grouping durability, scoped receipts, revision-specific review and outbox replay passed.");
    }

    private static T Read<T>(JsonDocument document, string field) =>
        document.RootElement.GetProperty(field).Deserialize<T>(ManagementStore.Json)!;

    private static void Expect(IResult result, int status) =>
        Check((result as IStatusCodeHttpResult)?.StatusCode == status, $"Expected Grouping status {status}, got {result}.");

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
