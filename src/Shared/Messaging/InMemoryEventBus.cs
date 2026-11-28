using System.Threading.Channels;

namespace Cassini.Messaging;

/// <summary>
/// Default offline bus inside one process. Queues behave like durable RabbitMQ queues while the process lives:
/// events published while no consumer is attached wait for the next subscription. A handler failure redelivers
/// the event up to <see cref="MaxDeliveries"/> times, then moves it to <see cref="DeadLetters"/>.
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    public const int MaxDeliveries = 5;

    private readonly object gate = new();
    private readonly Dictionary<string, Queue> queues = [];
    private readonly List<IntegrationEvent> deadLetters = [];

    public IReadOnlyList<IntegrationEvent> DeadLetters
    {
        get
        {
            lock (gate)
                return [.. deadLetters];
        }
    }

    public Task PublishAsync(IntegrationEvent message, CancellationToken cancellation)
    {
        var key = message.RoutingKey;
        lock (gate)
            foreach (var queue in queues.Values.Where(queue => queue.Bindings.Any(pattern => TopicPattern.Matches(pattern, key))))
                queue.Messages.Writer.TryWrite(new(message, 0));
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> SubscribeAsync(string queue, IReadOnlyCollection<string> bindings, EventHandler handler,
        CancellationToken cancellation)
    {
        Queue target;
        lock (gate)
        {
            if (!queues.TryGetValue(queue, out target!))
                queues[queue] = target = new Queue(Channel.CreateUnbounded<Delivery>(), []);
            target.Bindings.UnionWith(bindings);
        }
        return Task.FromResult<IAsyncDisposable>(new Consumer(this, target, handler));
    }

    private sealed record Delivery(IntegrationEvent Message, int Attempts);

    private sealed record Queue(Channel<Delivery> Messages, HashSet<string> Bindings);

    private sealed class Consumer : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;

        internal Consumer(InMemoryEventBus bus, Queue queue, EventHandler handler) =>
            loop = Task.Run(() => Run(bus, queue, handler, stop.Token));

        private static async Task Run(InMemoryEventBus bus, Queue queue, EventHandler handler, CancellationToken token)
        {
            var reader = queue.Messages.Reader;
            while (await WaitAsync(reader, token))
            {
                if (!reader.TryPeek(out var delivery))
                    continue;
                try
                {
                    await handler(delivery.Message, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return; // Unacknowledged: stays at the head of the queue for the next consumer.
                }
                catch (Exception)
                {
                    reader.TryRead(out _);
                    if (delivery.Attempts + 1 >= MaxDeliveries)
                        lock (bus.gate)
                            bus.deadLetters.Add(delivery.Message);
                    else
                        queue.Messages.Writer.TryWrite(delivery with { Attempts = delivery.Attempts + 1 });
                    continue;
                }
                reader.TryRead(out _);
            }
        }

        private static async Task<bool> WaitAsync(ChannelReader<Delivery> reader, CancellationToken token)
        {
            try
            {
                return await reader.WaitToReadAsync(token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await loop;
            stop.Dispose();
        }
    }
}
