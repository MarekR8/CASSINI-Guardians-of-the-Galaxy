using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cassini.Messaging;

/// <summary>
/// A consumer of integration events. Implementations commit their effect together with the processed
/// <c>(consumer, eventId)</c> receipt, so a redelivered event changes nothing.
/// </summary>
public interface IEventConsumer
{
    string Queue { get; }

    IReadOnlyCollection<string> Bindings { get; }

    Task HandleAsync(IntegrationEvent message, CancellationToken cancellation);
}

/// <summary>
/// Keeps a consumer subscribed for the lifetime of the host. An unreachable broker does not stop the service:
/// the subscription is retried with backoff, and the durable queue keeps events published in the meantime.
/// </summary>
public sealed class EventSubscription(IEventBus bus, IEventConsumer consumer, ILogger<EventSubscription> logger)
    : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromMilliseconds(500);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var subscription = await bus.SubscribeAsync(
                    consumer.Queue, consumer.Bindings, consumer.HandleAsync, stoppingToken);
                logger.LogInformation("Subscribed {Queue} to {Bindings}.", consumer.Queue, consumer.Bindings);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Subscription {Queue} failed; retrying in {Backoff}.", consumer.Queue, backoff);
                await Task.Delay(backoff, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }
}
