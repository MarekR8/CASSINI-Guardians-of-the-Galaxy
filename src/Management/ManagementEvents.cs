using Cassini.Messaging;

namespace Cassini.Management;

internal sealed record HazardAlertRecordedPayload(string IncidentId, string AlertId, string EvidenceStatus, DateTimeOffset ObservedAt);

/// <summary><c>CaseProgressChanged.v1</c>: a confirmed milestone. Position observations do not publish it.</summary>
internal sealed record CaseProgressChangedPayload(
    string IncidentId, string CaseId, string Progress, DateTimeOffset ObservedAt, string ConfirmationRef,
    string? EvidenceMode);

/// <summary><c>AssignmentChanged.v1</c> from Dispatch (contracts/events/AssignmentChanged.v1.json).</summary>
internal sealed record AssignmentChangedPayload(
    string? RequestId, string? AssignmentId, string? CaseId, string? UnitId, string? State, DateTimeOffset? ReportedAt);

/// <summary>What Management knows about the assistance task of a case. It never confirms the person's exit.</summary>
internal sealed record AssistanceStatus(
    string? RequestId, string? AssignmentId, string? UnitId, string State, long SubjectVersion, DateTimeOffset? ReportedAt);

internal static class ManagementEvents
{
    internal const string Producer = "management";

    internal static IntegrationEvent AlertRecorded(IncidentView incident, HazardAlert alert, DateTimeOffset now) =>
        IntegrationEvent.Create(EventTypes.HazardAlertRecorded, Producer, alert.AlertId, 1, now,
            new HazardAlertRecordedPayload(incident.IncidentId, alert.AlertId, alert.Source, alert.ObservedAt),
            correlationId: incident.IncidentId);

    internal static IntegrationEvent ProgressChanged(
        IncidentView incident, EvacuationCase item, string commandId, DateTimeOffset observedAt, DateTimeOffset now) =>
        IntegrationEvent.Create(EventTypes.CaseProgressChanged, Producer, item.CaseId, item.Version, now,
            new CaseProgressChangedPayload(incident.IncidentId, item.CaseId, item.Progress, observedAt.ToUniversalTime(),
                commandId, (item.Progress == ProgressStates.ExitConfirmed ? item.Exit : item.Pickup)?.Mode),
            correlationId: incident.IncidentId, causationId: commandId);
}

/// <summary>Management's outbox lives in its own state, so events commit together with the change.</summary>
internal sealed class ManagementOutbox(IManagementPersistence persistence) : IOutboxStore
{
    public IReadOnlyList<IntegrationEvent> PendingEvents() => persistence.Read().Outbox;

    public void MarkPublished(IReadOnlyCollection<string> eventIds) => persistence.Update(state =>
    {
        var remaining = state.Outbox.Where(message => !eventIds.Contains(message.EventId)).ToArray();
        return (remaining.Length == state.Outbox.Length ? null : state with { Outbox = remaining }, Results.Ok());
    });
}

/// <summary>
/// Reflects Dispatch assignment changes on the case. The receipt <c>management.events:eventId</c> commits with the
/// effect, and an older <c>subjectVersion</c> never overwrites a newer one. Pickup leaves the exit unconfirmed.
/// </summary>
internal sealed class AssignmentChangedConsumer(ManagementStore store) : IEventConsumer
{
    public string Queue => "management.events";

    public IReadOnlyCollection<string> Bindings { get; } = [EventTypes.RoutingKey(EventTypes.AssignmentChanged)];

    public Task HandleAsync(IntegrationEvent message, CancellationToken cancellation)
    {
        if (message.Type != EventTypes.AssignmentChanged)
            return Task.CompletedTask;

        var payload = message.ReadPayload<AssignmentChangedPayload>();
        store.Apply(state =>
        {
            if (state.Processed(Queue, message.EventId))
                return null;

            var next = state.MarkProcessed(Queue, message.EventId);
            var index = Array.FindIndex(state.Incident.Cases, item => item.CaseId == payload.CaseId);
            if (index < 0 || string.IsNullOrWhiteSpace(payload.State))
                return next;

            var item = state.Incident.Cases[index];
            if (item.Assistance is { } known && known.AssignmentId == payload.AssignmentId &&
                known.SubjectVersion >= message.SubjectVersion)
                return next;

            var cases = (EvacuationCase[])state.Incident.Cases.Clone();
            cases[index] = item with
            {
                Assistance = new(payload.RequestId, payload.AssignmentId, payload.UnitId, payload.State,
                    message.SubjectVersion, payload.ReportedAt),
            };
            return next with { Incident = next.Incident with { Cases = cases } };
        });
        return Task.CompletedTask;
    }
}
