using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cassini.Messaging;

/// <summary>
/// The producer's outbox. Events are written in the same commit as the state change that caused them and
/// removed only after the bus confirmed publication, so a crash in between republishes (at-least-once).
/// </summary>
public interface IOutboxStore
{
    IReadOnlyList<IntegrationEvent> PendingEvents();

    void MarkPublished(IReadOnlyCollection<string> eventIds);
}

/// <summary>Wakes the publisher after a commit that added outbox events.</summary>
public sealed class OutboxSignal
{
    private readonly SemaphoreSlim signal = new(0);

    public void Notify() => signal.Release();

    internal Task WaitAsync(TimeSpan timeout, CancellationToken cancellation) => signal.WaitAsync(timeout, cancellation);
}

public sealed class OutboxPublisher(IOutboxStore store, IEventBus bus, OutboxSignal signal, ILogger<OutboxPublisher> logger)
    : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);

    /// <summary>Publishes pending events in order. Returns the number published before the first failure.</summary>
    public async Task<int> PublishPendingAsync(CancellationToken cancellation)
    {
        var published = 0;
        foreach (var message in store.PendingEvents())
        {
            await bus.PublishAsync(message, cancellation);
            store.MarkPublished([message.EventId]);
            published++;
        }
        return published;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromMilliseconds(500);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishPendingAsync(stoppingToken);
                backoff = TimeSpan.FromMilliseconds(500);
                await signal.WaitAsync(Idle, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Outbox publication failed; retrying in {Backoff}.", backoff);
                await Task.Delay(backoff, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }
}
