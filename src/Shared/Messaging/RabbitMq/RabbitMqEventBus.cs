using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Cassini.Messaging.RabbitMq;

public sealed record RabbitMqOptions(string Uri, string ClientName)
{
    /// <summary>Types that must reach at least one queue; an unroutable publish fails and stays in the outbox.</summary>
    public IReadOnlySet<string> RequireRoutingFor { get; init; } = new HashSet<string>();

    /// <summary>Redeliveries before a failing event moves to the dead-letter queue (quorum queue delivery limit).</summary>
    public int DeliveryLimit { get; init; } = 5;
}

/// <summary>
/// RabbitMQ implementation (<c>Integrations:Bus:Mode=live</c>): durable topic exchange <c>evacuation.events</c>,
/// persistent JSON messages with <c>MessageId = eventId</c>, publisher confirms, and durable quorum queues per
/// subscriber with manual acknowledgement after the handler commits.
/// </summary>
public sealed class RabbitMqEventBus(RabbitMqOptions options) : IEventBus, IAsyncDisposable
{
    public const string DeadLetterExchange = EventTypes.Exchange + ".dlx";
    public const string DeadLetterQueue = EventTypes.Exchange + ".dead-letter";

    private readonly SemaphoreSlim gate = new(1, 1);
    private IConnection? connection;
    private IChannel? publisher;

    public async Task PublishAsync(IntegrationEvent message, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            publisher ??= await OpenChannelAsync(confirms: true, cancellation);
            var properties = new BasicProperties
            {
                Persistent = true,
                MessageId = message.EventId,
                Type = message.Type,
                ContentType = "application/json",
                CorrelationId = message.CorrelationId,
                AppId = message.Producer,
                Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
            };
            var body = JsonSerializer.SerializeToUtf8Bytes(message, IntegrationEvent.Json);
            // With confirmation tracking the call completes on the broker ack and throws on a nack or a return.
            await publisher.BasicPublishAsync(EventTypes.Exchange, message.RoutingKey,
                mandatory: options.RequireRoutingFor.Contains(message.Type), properties, body, cancellation);
        }
        catch when (publisher is { IsOpen: false })
        {
            publisher = null;
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IAsyncDisposable> SubscribeAsync(string queue, IReadOnlyCollection<string> bindings,
        EventHandler handler, CancellationToken cancellation)
    {
        var channel = await OpenChannelAsync(confirms: false, cancellation);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = options.DeliveryLimit,
                ["x-dead-letter-exchange"] = DeadLetterExchange,
            }, cancellationToken: cancellation);
        foreach (var pattern in bindings)
            await channel.QueueBindAsync(queue, EventTypes.Exchange, pattern, cancellationToken: cancellation);
        await channel.BasicQosAsync(0, 16, false, cancellation);

        var stopping = new CancellationTokenSource();
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            IntegrationEvent? message;
            try
            {
                message = JsonSerializer.Deserialize<IntegrationEvent>(delivery.Body.Span, IntegrationEvent.Json);
            }
            catch (JsonException)
            {
                message = null;
            }
            if (message is null)
            {
                await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false); // Straight to dead letters.
                return;
            }

            try
            {
                await handler(message, stopping.Token);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
            }
            catch (Exception)
            {
                if (channel.IsOpen)
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
            }
        };
        var tag = await channel.BasicConsumeAsync(queue, autoAck: false, consumer, cancellation);
        return new Subscription(channel, tag, stopping);
    }

    private async Task<IChannel> OpenChannelAsync(bool confirms, CancellationToken cancellation)
    {
        if (connection is not { IsOpen: true })
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.Uri),
                ClientProvidedName = options.ClientName,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
            };
            connection = await factory.CreateConnectionAsync(cancellation);
        }

        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: confirms, publisherConfirmationTrackingEnabled: confirms),
            cancellation);
        await channel.ExchangeDeclareAsync(EventTypes.Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellation);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false,
            cancellationToken: cancellation);
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellation);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, "", cancellationToken: cancellation);
        return channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (publisher is not null)
            await publisher.DisposeAsync();
        if (connection is not null)
            await connection.DisposeAsync();
        gate.Dispose();
    }

    private sealed class Subscription(IChannel channel, string tag, CancellationTokenSource stopping) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            if (channel.IsOpen)
            {
                await channel.BasicCancelAsync(tag);
                await channel.CloseAsync();
            }
            await channel.DisposeAsync();
            stopping.Dispose();
        }
    }
}
