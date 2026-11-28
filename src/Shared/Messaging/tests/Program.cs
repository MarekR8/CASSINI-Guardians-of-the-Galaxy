using Cassini.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
#if MESSAGING_RABBITMQ
using Cassini.Messaging.RabbitMq;
#endif

Check(TopicPattern.Matches("dispatch.*.v1", "dispatch.assignment-changed.v1") &&
      TopicPattern.Matches("#", "routing.route-withdrawn.v1") &&
      TopicPattern.Matches("routing.#", "routing.route-published.v1") &&
      !TopicPattern.Matches("routing.*", "routing.route-published.v1") &&
      !TopicPattern.Matches("situation.changed.v1", "situation.changed.v2"), "Topic patterns follow AMQP rules.");
Check(EventTypes.RoutingKey(EventTypes.SituationChanged) == "situation.changed.v1", "Routing keys match the binding table.");

await VerifyBus("in-memory", new InMemoryEventBus());

// Outbox: a failed publication keeps the event, the next attempt publishes it once and in order.
var outbox = new MemoryOutbox([Event("o-1"), Event("o-2")]);
var flaky = new FlakyBus();
var publisher = new OutboxPublisher(outbox, flaky, new OutboxSignal(), NullLogger<OutboxPublisher>.Instance);
await Throws(() => publisher.PublishPendingAsync(default));
Check(outbox.PendingEvents().Count == 2, "An unconfirmed event stays in the outbox.");
Check(await publisher.PublishPendingAsync(default) == 2 && outbox.PendingEvents().Count == 0,
    "The retry publishes every pending event.");
Check(flaky.Published.SequenceEqual(["o-1", "o-2"]), "Events are published in outbox order.");

// Poison messages end in dead letters after bounded redeliveries.
var poisonBus = new InMemoryEventBus();
var attempts = 0;
await using (await poisonBus.SubscribeAsync("poison", ["#"], (_, _) => { attempts++; throw new InvalidOperationException(); }, default))
{
    await poisonBus.PublishAsync(Event("bad"), default);
    await Eventually(() => poisonBus.DeadLetters.Count == 1);
}
Check(attempts == InMemoryEventBus.MaxDeliveries, "A failing event is retried a bounded number of times.");

#if MESSAGING_RABBITMQ
if (Environment.GetEnvironmentVariable("RABBITMQ_URI") is { Length: > 0 } uri)
{
    var options = new RabbitMqOptions(uri, "cassini-tests")
    {
        RequireRoutingFor = new HashSet<string> { EventTypes.AssignmentChanged },
    };
    await using var rabbit = new RabbitMqEventBus(options);
    await VerifyBus("RabbitMQ", rabbit, $"tests.{Guid.NewGuid():N}");
}
else
    Console.WriteLine("RABBITMQ_URI not set: RabbitMQ checks skipped.");
#endif

Console.WriteLine("Messaging tests passed: topics, durable queue, consumer dedup, outbox retry, dead letters.");

// Stop the consumer, publish, start it again: the event arrives once; a duplicate eventId changes nothing.
static async Task VerifyBus(string name, IEventBus bus, string queue = "management.events")
{
    var inbox = new HashSet<string>();
    var effects = new List<string>();
    Task Handle(IntegrationEvent message, CancellationToken _)
    {
        lock (inbox)
            if (inbox.Add(message.EventId))
                effects.Add(message.EventId);
        return Task.CompletedTask;
    }

    var binding = EventTypes.RoutingKey(EventTypes.AssignmentChanged);
    var subscription = await bus.SubscribeAsync(queue, [binding], Handle, default);
    await subscription.DisposeAsync();

    var first = Event("e-" + Guid.NewGuid().ToString("N"));
    await bus.PublishAsync(first, default);
    await bus.PublishAsync(Event("ignored", EventTypes.SituationChanged), default);
    Check(effects.Count == 0, $"{name}: nothing is consumed while the consumer is stopped.");

    await using (await bus.SubscribeAsync(queue, [binding], Handle, default))
    {
        await Eventually(() => effects.Count == 1);
        await bus.PublishAsync(first, default);
        await Task.Delay(300);
    }
    Check(effects.SequenceEqual([first.EventId]), $"{name}: the event is applied exactly once after restart.");
    Console.WriteLine($"{name}: stop/publish/start delivered once; duplicate eventId ignored.");
}

static IntegrationEvent Event(string id, string type = EventTypes.AssignmentChanged) =>
    IntegrationEvent.Create(type, "tests", "assignment-1", 1, DateTimeOffset.UtcNow, new { state = "PickupConfirmed" })
        with { EventId = id };

static async Task Eventually(Func<bool> condition)
{
    for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        await Task.Delay(50);
    Check(condition(), "Condition not reached in time.");
}

static async Task Throws(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (InvalidOperationException)
    {
        return;
    }
    throw new InvalidOperationException("Expected a publication failure.");
}

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
}

internal sealed class MemoryOutbox(IEnumerable<IntegrationEvent> events) : IOutboxStore
{
    private readonly List<IntegrationEvent> pending = [.. events];

    public IReadOnlyList<IntegrationEvent> PendingEvents() => [.. pending];

    public void MarkPublished(IReadOnlyCollection<string> eventIds) =>
        pending.RemoveAll(message => eventIds.Contains(message.EventId));
}

internal sealed class FlakyBus : IEventBus
{
    private bool failed;
    internal List<string> Published { get; } = [];

    public Task PublishAsync(IntegrationEvent message, CancellationToken cancellation)
    {
        if (!failed)
        {
            failed = true;
            throw new InvalidOperationException("Broker unavailable.");
        }
        Published.Add(message.EventId);
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> SubscribeAsync(string queue, IReadOnlyCollection<string> bindings,
        Cassini.Messaging.EventHandler handler, CancellationToken cancellation) => throw new NotSupportedException();
}
