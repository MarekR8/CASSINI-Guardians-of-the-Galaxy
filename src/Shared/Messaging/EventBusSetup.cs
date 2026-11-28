using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cassini.Messaging;

public static class EventBusSetup
{
    /// <summary>
    /// With <c>Integrations:Bus:Mode=live</c>, registers the RabbitMQ bus from <paramref name="rabbitMq"/> (null when the
    /// service was built without <c>-p:EnableRabbitMq=true</c>) and the outbox publisher. Returns false in the default
    /// <c>memory</c> mode, where the owner's outbox is only read over HTTP.
    /// </summary>
    public static bool AddLiveEventBus(this IServiceCollection services, IConfiguration configuration,
        Func<string, IEventBus>? rabbitMq)
    {
        switch (configuration["Integrations:Bus:Mode"] ?? "memory")
        {
            case "memory":
                return false;
            case "live":
                var uri = configuration["Integrations:Bus:Uri"]
                    ?? throw new InvalidOperationException("Set Integrations:Bus:Uri for the RabbitMQ bus.");
                services.AddSingleton(rabbitMq?.Invoke(uri)
                    ?? throw new InvalidOperationException("Build with -p:EnableRabbitMq=true to use Integrations:Bus:Mode=live."));
                services.AddSingleton<OutboxSignal>();
                services.AddSingleton<OutboxPublisher>();
                services.AddHostedService(provider => provider.GetRequiredService<OutboxPublisher>());
                return true;
            default:
                throw new InvalidOperationException("Integrations:Bus:Mode supports 'memory' or 'live'.");
        }
    }
}
