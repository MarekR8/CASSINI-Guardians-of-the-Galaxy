#if DISPATCH_RABBITMQ
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Cassini.Dispatch;

internal sealed class RabbitMqRouteNotifications(Uri address, ReplanSignal signal,
    ILogger<RabbitMqRouteNotifications> logger) : BackgroundService
{
    private const string Exchange = "evacuation.events";
    private const string QueueName = "dispatch.events";
    private const string SituationChanged = "situation.changed.v1";
    private const string RouteWithdrawn = "routing.route-withdrawn.v1";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory { Uri = address, AutomaticRecoveryEnabled = false };
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Listen(factory, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Dispatch RabbitMQ subscription interrupted ({ErrorType}). Retrying.",
                    exception.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task Listen(ConnectionFactory factory, CancellationToken cancellation)
    {
        await using var connection = await factory.CreateConnectionAsync(cancellation);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellation);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.ChannelShutdownAsync += (_, _) =>
        {
            disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: cancellation);
        await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellation);
        await channel.QueueBindAsync(QueueName, Exchange, SituationChanged, cancellationToken: cancellation);
        await channel.QueueBindAsync(QueueName, Exchange, RouteWithdrawn, cancellationToken: cancellation);
        await channel.BasicQosAsync(0, 1, global: false, cancellationToken: cancellation);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.UnregisteredAsync += (_, _) =>
        {
            disconnected.TrySetResult();
            return Task.CompletedTask;
        };
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            if (!IsRouteNotification(delivery.RoutingKey, delivery.Body))
            {
                logger.LogWarning("Ignoring an invalid Dispatch route notification.");
                await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken: cancellation);
                return;
            }

            // Notifications wake reconciliation. The periodic review covers lost wakeups and unavailable owners.
            signal.Notify();
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: cancellation);
        };
        await channel.BasicConsumeAsync(QueueName, autoAck: false, consumer: consumer, cancellationToken: cancellation);
        await disconnected.Task.WaitAsync(cancellation);
    }

    private static bool IsRouteNotification(string key, ReadOnlyMemory<byte> body)
    {
        var expectedType = key switch
        {
            SituationChanged => "SituationChanged.v1",
            RouteWithdrawn => "RouteWithdrawn.v1",
            _ => null,
        };
        if (expectedType is null)
            return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String && type.GetString() == expectedType;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
#endif
