using System.Text.Json;
using System.Text.Json.Serialization;
using Cassini.Messaging;

namespace Cassini.Management;

internal sealed record HazardAlert(string AlertId, DateTimeOffset ObservedAt, string Source);

internal sealed record EvacuationCase(
    string CaseId, string Name, string Node, string Mobility, string Progress, int Version,
    DateTimeOffset? PositionObservedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PositionFix? Pickup = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PositionFix? Exit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AssistanceStatus? Assistance = null)
{
    /// <summary>Only an exit confirmation that carries a position fix counts as confirmed outside the zone.</summary>
    [JsonIgnore]
    public bool ConfirmedOutside => Progress == ProgressStates.ExitConfirmed && Exit is not null;
}

internal static class ProgressStates
{
    internal const string Open = "Open";
    internal const string Acknowledged = "Acknowledged";
    internal const string Moving = "Moving";
    internal const string PickedUp = "PickedUp";
    internal const string ExitConfirmed = "ExitConfirmed";
}

internal sealed record IncidentView(string IncidentId, HazardAlert[] Alerts, EvacuationCase[] Cases);
internal sealed record Receipt(string Command, JsonElement Result);

/// <summary>
/// Everything Management commits at once. <c>Outbox</c> holds events not yet confirmed by the bus;
/// <c>Inbox</c> holds the last processed <c>consumer:eventId</c> receipts.
/// </summary>
internal sealed record ManagementState(
    IncidentView Incident, Dictionary<string, Receipt> Receipts, EwssBroadcast[]? Broadcasts = null,
    IntegrationEvent[]? Outbox = null, string[]? Inbox = null)
{
    internal const int MaxInbox = 2000;

    public EwssBroadcast[] Broadcasts { get; init; } = Broadcasts ?? [];
    public IntegrationEvent[] Outbox { get; init; } = Outbox ?? [];
    public string[] Inbox { get; init; } = Inbox ?? [];

    internal ManagementState Publish(IntegrationEvent message) => this with { Outbox = [.. Outbox, message] };

    internal bool Processed(string consumer, string eventId) => Inbox.Contains(consumer + ":" + eventId);

    internal ManagementState MarkProcessed(string consumer, string eventId) =>
        this with { Inbox = [.. Inbox.Skip(Math.Max(Inbox.Length + 1 - MaxInbox, 0)), consumer + ":" + eventId] };

    internal static ManagementState Initial() => new(
        new IncidentView("incident-42", [],
        [
            new("case-14", "Mieszkaniec 14", "walk-start", "Walking", ProgressStates.Open, 1),
            new("case-15", "Mieszkaniec 15", "pickup", "Assisted", ProgressStates.Open, 1),
            new("case-16", "Mieszkaniec 16", "pickup", "Assisted", ProgressStates.Open, 1),
        ]), []);
}

/// <summary>A domain change produced by a command handler, or a rejection when <see cref="Changed"/> is null.</summary>
internal readonly record struct Outcome(ManagementState? Changed, object Payload, int Status)
{
    internal static Outcome Accept(ManagementState changed, object payload) => new(changed, payload, 200);
    internal static Outcome Reject(string code, string detail, int status) => new(null, new { code, detail }, status);
}

internal sealed class ManagementStore(IManagementPersistence persistence, OutboxSignal? outbox = null)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ManagementState Read() => persistence.Read();

    /// <summary>
    /// Applies a command once. An exact retry returns the original result; another body under the same ID is a conflict.
    /// </summary>
    public IResult Execute<TCommand>(string? id, TCommand command, Func<ManagementState, Outcome> apply)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            return Error("CommandIdRequired", "Provide a command ID of at most 128 characters.", 400);

        var identity = typeof(TCommand).Name + ":" + JsonSerializer.Serialize(command, Json);
        try
        {
            return persistence.Update(state =>
            {
                if (state.Receipts.TryGetValue(id, out var receipt))
                    return (null, receipt.Command == identity
                        ? Results.Ok(receipt.Result)
                        : Error("CommandIdConflict", "This command ID was used for another request.", 409));

                var outcome = apply(state);
                if (outcome.Changed is null)
                    return (null, Results.Json(outcome.Payload, statusCode: outcome.Status));

                var result = JsonSerializer.SerializeToElement(outcome.Payload, Json);
                var receipts = new Dictionary<string, Receipt>(outcome.Changed.Receipts) { [id] = new(identity, result) };
                return (outcome.Changed with { Receipts = receipts }, Results.Ok(result));
            }, NotifyOutbox);
        }
        catch (ManagementPersistenceException)
        {
            return Error("StateUnavailable", "Management could not write its local state.", 503);
        }
    }

    /// <summary>Applies a state change without a command receipt, used by event consumers.</summary>
    internal void Apply(Func<ManagementState, ManagementState?> apply) =>
        persistence.Update(state => (apply(state), Results.Ok()), NotifyOutbox);

    private void NotifyOutbox(ManagementState committed)
    {
        if (committed.Outbox.Length > 0)
            outbox?.Notify();
    }

    internal static IResult Error(string code, string detail, int status) =>
        Results.Json(new { code, detail }, statusCode: status);
}
