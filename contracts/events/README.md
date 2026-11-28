# Integration events (W3)

Every event uses the envelope in [`src/Shared/Messaging/IntegrationEvent.cs`](../../src/Shared/Messaging/IntegrationEvent.cs): `eventId`, `type`, `producer`, `subjectId`, `subjectVersion`, `occurredAt`, `correlationId`, `causationId` and `payload`. Timestamps are UTC. The files here are example messages with synthetic values.

| Event | Routing key on `evacuation.events` | Producer | Consumers | Status |
| --- | --- | --- | --- | --- |
| [`SituationChanged.v1`](SituationChanged.v1.json) | `situation.changed.v1` | Situation (task 2) | Routing, Management, dashboard | Contract; producer to be wired by the owner |
| [`RoutePublished.v1`](RoutePublished.v1.json) | `routing.route-published.v1` | Routing (task 3) | Management, Dispatch | Contract; producer to be wired by the owner |
| [`RouteWithdrawn.v1`](RouteWithdrawn.v1.json) | `routing.route-withdrawn.v1` | Routing (task 3) | Management, Dispatch | Contract; producer to be wired by the owner |
| [`AssistanceRequested.v1`](AssistanceRequested.v1.json) | `dispatch.assistance-requested.v1` | Dispatch (task 4) | Management | Contract; producer to be wired by the owner |
| [`AssignmentChanged.v1`](AssignmentChanged.v1.json) | `dispatch.assignment-changed.v1` | Dispatch (task 4) | **Management (implemented)** | Management records `assistance` on the case |
| [`HazardAlertRecorded.v1`](HazardAlertRecorded.v1.json) | `management.hazard-alert-recorded.v1` | **Management (implemented)** | Situation | Published from the outbox |
| [`CaseProgressChanged.v1`](CaseProgressChanged.v1.json) | `management.case-progress-changed.v1` | **Management (implemented)** | Not required for the MVP monitor | Published for every milestone; a position update does not publish |

## Rules for producers

1. Add the event to the outbox in the same commit as the state change. Publish it with `OutboxPublisher`, which removes the event only after the broker confirms it (at least once).
2. Set `subjectVersion` to your revision of the subject. Keep `routeVersion` explicit in route payloads.
3. Keep payloads minimal and use references for positions and assistance details.

## Rules for consumers

1. Implement `IEventConsumer` with a durable queue named `<service>.events`.
2. Commit the effect and the receipt `<queue>:<eventId>` together. A redelivered event must change nothing.
3. Ignore an event whose `subjectVersion` is older than the one you hold. For example, a late withdrawal of version 1 cannot retire version 2.
4. A handler that throws is redelivered. After 5 deliveries the event moves to `evacuation.events.dead-letter`.

The bus mode is `Integrations:Bus:Mode`: `memory` (default, offline, inside one process) or `live` (RabbitMQ, requires `-p:EnableRabbitMq=true` and `Integrations:Bus:Uri`).
