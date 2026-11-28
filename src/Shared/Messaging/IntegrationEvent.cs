using System.Text.Json;

namespace Cassini.Messaging;

/// <summary>
/// The common envelope for committed facts on <c>evacuation.events</c> (docs/mvp-commands-events.md).
/// <c>SubjectVersion</c> is the producer's revision of the subject; consumers use it to ignore stale facts.
/// </summary>
public sealed record IntegrationEvent(
    string EventId, string Type, string Producer, string SubjectId, long SubjectVersion,
    DateTimeOffset OccurredAt, string? CorrelationId, string? CausationId, JsonElement Payload)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string RoutingKey => EventTypes.RoutingKey(Type);

    public static IntegrationEvent Create<TPayload>(
        string type, string producer, string subjectId, long subjectVersion, DateTimeOffset occurredAt,
        TPayload payload, string? correlationId = null, string? causationId = null) =>
        new(Guid.NewGuid().ToString("D"), type, producer, subjectId, subjectVersion, occurredAt.ToUniversalTime(),
            correlationId, causationId, JsonSerializer.SerializeToElement(payload, Json));

    public TPayload ReadPayload<TPayload>() =>
        Payload.Deserialize<TPayload>(Json) ?? throw new JsonException($"Empty payload in {Type}.");
}

/// <summary>Event types and their topic routing keys (docs/mvp-interfaces.md, RabbitMQ bindings).</summary>
public static class EventTypes
{
    public const string Exchange = "evacuation.events";

    public const string HazardAlertRecorded = "HazardAlertRecorded.v1";
    public const string SituationChanged = "SituationChanged.v1";
    public const string RoutePublished = "RoutePublished.v1";
    public const string RouteWithdrawn = "RouteWithdrawn.v1";
    public const string AssistanceRequested = "AssistanceRequested.v1";
    public const string AssignmentChanged = "AssignmentChanged.v1";
    public const string CaseProgressChanged = "CaseProgressChanged.v1";
    public const string ShelterAvailabilityChanged = "ShelterAvailabilityChanged.v1";
    public const string ShelterAssignmentChanged = "ShelterAssignmentChanged.v1";
    public const string EvacuationGroupChanged = "EvacuationGroupChanged.v1";
    public const string CareHandoverRecorded = "CareHandoverRecorded.v1";
    public const string TravelPlanApproved = "TravelPlanApproved.v1";
    public const string TravelPlanReviewRequired = "TravelPlanReviewRequired.v1";
    public const string ShelterAdmissionConfirmed = "ShelterAdmissionConfirmed.v1";
    public const string ShelterDepartureConfirmed = "ShelterDepartureConfirmed.v1";

    private static readonly Dictionary<string, string> Keys = new()
    {
        [HazardAlertRecorded] = "management.hazard-alert-recorded.v1",
        [SituationChanged] = "situation.changed.v1",
        [RoutePublished] = "routing.route-published.v1",
        [RouteWithdrawn] = "routing.route-withdrawn.v1",
        [AssistanceRequested] = "dispatch.assistance-requested.v1",
        [AssignmentChanged] = "dispatch.assignment-changed.v1",
        [CaseProgressChanged] = "management.case-progress-changed.v1",
        [ShelterAvailabilityChanged] = "sheltering.shelter-availability-changed.v1",
        [ShelterAssignmentChanged] = "sheltering.shelter-assignment-changed.v1",
        [EvacuationGroupChanged] = "grouping.evacuation-group-changed.v1",
        [CareHandoverRecorded] = "grouping.care-handover-recorded.v1",
        [TravelPlanApproved] = "grouping.travel-plan-approved.v1",
        [TravelPlanReviewRequired] = "grouping.travel-plan-review-required.v1",
        [ShelterAdmissionConfirmed] = "sheltering.shelter-admission-confirmed.v1",
        [ShelterDepartureConfirmed] = "sheltering.shelter-departure-confirmed.v1",
    };

    public static IReadOnlyCollection<string> All => Keys.Keys;

    public static string RoutingKey(string type) =>
        Keys.TryGetValue(type, out var key) ? key : throw new ArgumentException($"Unknown event type {type}.", nameof(type));
}
