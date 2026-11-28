using Cassini.Messaging;

namespace Cassini.Demo.SiteCatalog;

internal sealed class CatalogEventOutbox<T>(CatalogStore<T> catalog, TimeProvider clock) : IOutboxStore where T : class
{
    public IReadOnlyList<IntegrationEvent> PendingEvents() => catalog.ReadOutbox()
        .Where(entry => entry.PublishedAt is null)
        .Select(entry =>
        {
            var fact = entry.Event;
            return new IntegrationEvent(fact.EventId.ToString("D"), fact.Type, fact.Producer, fact.SubjectId,
                fact.SubjectVersion, fact.OccurredAt, fact.CorrelationId, fact.CausationId, fact.Payload);
        }).ToArray();

    public void MarkPublished(IReadOnlyCollection<string> eventIds) => ConfirmCatalogEvents(eventIds);

    internal string[] ConfirmCatalogEvents(IReadOnlyCollection<string> eventIds) =>
        catalog.MarkCatalogPublished(eventIds, clock.GetUtcNow());
}
