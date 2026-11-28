namespace Cassini.Messaging;

/// <summary>Handles one delivered event. Throwing leaves the event unacknowledged for redelivery.</summary>
public delegate Task EventHandler(IntegrationEvent message, CancellationToken cancellation);

public interface IEventBus
{
    /// <summary>Completes once the broker has confirmed the event. Throws when it cannot be published.</summary>
    Task PublishAsync(IntegrationEvent message, CancellationToken cancellation);

    /// <summary>
    /// Declares a durable subscriber queue bound to topic patterns and starts consuming. A subscriber that
    /// stops and starts again with the same queue name receives the events published in between.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync(string queue, IReadOnlyCollection<string> bindings, EventHandler handler,
        CancellationToken cancellation);
}

/// <summary>AMQP topic matching: <c>*</c> matches one word, <c>#</c> matches zero or more words.</summary>
public static class TopicPattern
{
    public static bool Matches(string pattern, string routingKey) =>
        Match(pattern.Split('.'), 0, routingKey.Split('.'), 0);

    private static bool Match(string[] pattern, int p, string[] key, int k)
    {
        if (p == pattern.Length)
            return k == key.Length;
        if (pattern[p] == "#")
            return Enumerable.Range(k, key.Length - k + 1).Any(next => Match(pattern, p + 1, key, next));
        return k < key.Length && (pattern[p] == "*" || pattern[p] == key[k]) && Match(pattern, p + 1, key, k + 1);
    }
}
