# Integration events (W3)

Every event uses the envelope in [`src/Shared/Messaging/IntegrationEvent.cs`](../../src/Shared/Messaging/IntegrationEvent.cs): `eventId`, `type`, `producer`, `subjectId`, `subjectVersion`, `occurredAt`, `correlationId`, `causationId` and `payload`. Timestamps are UTC. The files here are example messages with synthetic values.

| Event | Routing key on `evacuation.events` | Producer | Consumers | Status |
| --- | --- | --- | --- | --- |
| [`SituationChanged.v1`](SituationChanged.v1.json) | `situation.changed.v1` | Situation (task 2) | Routing, Management, dashboard | Contract; producer to be wired by the owner |
| [`AssemblyPointChanged.v1`](AssemblyPointChanged.v1.json) | `situation.assembly-point-changed.v1` | Situation (SH-T01) | Routing and coordinator projections (target) | Published from the site catalog outbox in the full profile |
| [`RoutePublished.v1`](RoutePublished.v1.json) | `routing.route-published.v1` | Routing (task 3) | Management, Dispatch | Contract; producer to be wired by the owner |
| [`RouteWithdrawn.v1`](RouteWithdrawn.v1.json) | `routing.route-withdrawn.v1` | Routing (task 3) | Management, Dispatch | Contract; producer to be wired by the owner |
| [`AssistanceRequested.v1`](AssistanceRequested.v1.json) | `dispatch.assistance-requested.v1` | Dispatch (task 4) | Management | Contract; producer to be wired by the owner |
| [`AssignmentChanged.v1`](AssignmentChanged.v1.json) | `dispatch.assignment-changed.v1` | Dispatch (task 4) | **Management (implemented)** | Management records `assistance` on the case |
| [`HazardAlertRecorded.v1`](HazardAlertRecorded.v1.json) | `management.hazard-alert-recorded.v1` | **Management (implemented)** | Situation | Published from the outbox |
| [`CaseProgressChanged.v1`](CaseProgressChanged.v1.json) | `management.case-progress-changed.v1` | **Management (implemented)** | Not required for the MVP monitor | Published for milestones |
| [`EvacuationGroupChanged.v1`](EvacuationGroupChanged.v1.json) | `grouping.evacuation-group-changed.v1` | Management Grouping (SH-T02) | Sheltering, coordinator | Published on registration and group revision changes |
| [`CareHandoverRecorded.v1`](CareHandoverRecorded.v1.json) | `grouping.care-handover-recorded.v1` | Management Grouping (SH-T02) | Authorized planners | Published with an opaque handover reference |
| [`TravelPlanApproved.v1`](TravelPlanApproved.v1.json) | `grouping.travel-plan-approved.v1` | Management Grouping (SH-T02) | Sheltering, coordinator | Published when an approved plan revision is committed |
| [`TravelPlanReviewRequired.v1`](TravelPlanReviewRequired.v1.json) | `grouping.travel-plan-review-required.v1` | Management Grouping (SH-T02) | Sheltering, coordinator | Published on the first transition to review for the affected revision |
| [`ShelterAdmissionConfirmed.v1`](ShelterAdmissionConfirmed.v1.json) | `sheltering.shelter-admission-confirmed.v1` | **Sheltering (SH-T05)** | Management, coordinator view | Published from the reception outbox (SH-T06 Stage B) |
| [`ShelterDepartureConfirmed.v1`](ShelterDepartureConfirmed.v1.json) | `sheltering.shelter-departure-confirmed.v1` | **Sheltering (SH-T05)** | Management, coordinator view | Published from the reception outbox (SH-T06 Stage B) |
| [`ShelterAssignmentChanged.v1`](ShelterAssignmentChanged.v1.json) | `sheltering.shelter-assignment-changed.v1` | **Sheltering (SH-T04)** | **Sheltering instruction board (implemented)**, coordinator view | Published from the allocation outbox; `subjectVersion` is the assignment version |
| [`ShelterAvailabilityChanged.v1`](ShelterAvailabilityChanged.v1.json) | `sheltering.shelter-availability-changed.v1` | **Sheltering (SH-T04)** | Operator availability projection | Published from the allocation outbox |

`RoutePublished.v1` and `RouteWithdrawn.v1` are also published by **Routing (SH-T06)** for `shelter-assignment` subjects. The payload carries the `subject`, the `routeVersion`, and the `assignment` with its `reservationId`, or the withdrawn `reservationId`. `subjectVersion` is the instruction's `aggregateVersion`. When a transfer supersedes a route, one commit emits `RouteWithdrawn.v1` with `reasonCode: "Superseded"` and `RoutePublished.v1`, both at the same `subjectVersion`.

### SH-W5 instruction events (SH-T06)

[`replay/sh-w5-instructions.replay.json`](replay/sh-w5-instructions.replay.json) replays one leg through booking, publication, a closure, a replacement and a transfer. It also includes duplicate, old, reordered and missing events, and group, site and admission facts. Each step gives the expected state. `dotnet run --project src/Sheltering/tests/Cassini.Sheltering.Tests.csproj` verifies it without a broker.

The board applies these rules:
- A known `eventId` changes nothing.
- An older `subjectVersion` is ignored.
- Distinct events at the same version are applied by `routeVersion`. A publication applies only above the current and last withdrawn route versions. A withdrawal clears only the route version it names.
- A gap in versions is filled by reading the owner instead of guessing.

## Rules for producers

1. Add the event to the outbox in the same commit as the state change. Publish it with `OutboxPublisher`, which removes the event only after the broker confirms it (at least once).
2. Set `subjectVersion` to your revision of the subject. Keep `routeVersion` explicit in route payloads.
3. Keep payloads minimal and use references for positions and assistance details.

Grouping subject IDs contain the escaped incident and group IDs, with `/care` and `/plans` suffixes for those streams. Group and care events use `groupRevision` as `subjectVersion`. Plan approval uses `2 × planRevision − 1` and its review event uses `2 × planRevision`. Consumers can therefore discard an older review once a newer plan has been approved. Read current details through the scoped [SH-W2 endpoints](../http/grouping/README.md).

## Rules for consumers

1. Implement `IEventConsumer` with a durable queue named `<service>.events`.
2. Commit the effect and the receipt `<queue>:<eventId>` together. A redelivered event must change nothing.
3. Ignore an event whose `subjectVersion` is older than the one you hold. For example, a late withdrawal of version 1 cannot retire version 2.
4. A handler that throws is redelivered. After 5 deliveries the event moves to `evacuation.events.dead-letter`.

The bus mode is `Integrations:Bus:Mode`: `memory` (default, offline, inside one process) or `live` (RabbitMQ, requires `-p:EnableRabbitMq=true` and `Integrations:Bus:Uri`).
