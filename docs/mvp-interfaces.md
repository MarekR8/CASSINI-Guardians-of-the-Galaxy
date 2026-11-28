# MVP interfaces between services and aggregates

This specification proposes the interfaces for the [MVP workflows](business-processes.md). It gives the [command and event catalog](mvp-commands-events.md) an HTTP or RabbitMQ address and identifies the owner of each write. See the [technology stack](mvp-technology-stack.md) for runtime versions, provider integrations and deployment settings.

## Where each kind of interaction runs

| Boundary | Transport and technology | Ownership rule |
| --- | --- | --- |
| Aggregates within one service | In-process C# calls through an application handler. The handler loads the relevant roots and commits through the service's PostgreSQL transaction. | An aggregate cannot modify another root by changing its fields. The handler calls the root's domain operations. |
| Commands and queries between services | HTTP with JSON on ASP.NET Core .NET 10 and a versioned OpenAPI contract. `management`, `situation`, `routing` and `dispatch` are private service names in Docker Compose. Use HTTPS across deployment trust boundaries. | Call the service that owns the write or authoritative query. Exchange IDs, DTOs and snapshots. Each service owns its database. |
| Committed facts between services | RabbitMQ using AMQP 0-9-1, durable topic exchange `evacuation.events`, persistent JSON messages and a durable queue per subscriber. | Producers write an outbox record in the same transaction as the state change. Consumers commit effects and deduplication before acknowledging. |
| Web app and external providers | Web panel uses HTTP queries and commands via the web entry point. The Routing service calls OpenRouteService through an HTTP adapter. Situation Data imports Copernicus and FlyPix GeoJSON. | Only Routing decides whether to publish a provider candidate. Provider configuration stays on the server. The panel composes read views and sends authorized commands. |

References: [RabbitMQ topic bindings](https://www.rabbitmq.com/tutorials/tutorial-five-dotnet), [RabbitMQ delivery acknowledgements and publisher confirms](https://www.rabbitmq.com/docs/confirms).

## Aggregate interactions inside each service

| Initiator | Target or reference | How they interact | What the interaction guarantees |
| --- | --- | --- | --- |
| Management case handler | `EvacuationIncident` and `EvacuationCase` | `OpenEvacuationCase` verifies the incident by `incidentId`, then creates the case through its own root. Case progress is recorded on `EvacuationCase`. | One case per known subject and incident. Progress reports do not change the incident. |
| Situation Data snapshot and validation handlers | Revisions of `HazardAssessment` and `NetworkSegment` | Query handler reads hazard extents and effective segment restrictions into a versioned snapshot. The same handlers expose roads and hazards as GeoJSON for the map. | Each root owns its revisions. A snapshot carries the evidence revisions and freshness that were used. |
| Situation Data operator | `HazardAssessment` or `NetworkSegment` | Record a hazard revision first. If authorized evidence supports a closure, `SetAccessRestriction` addresses the relevant segment by ID and can reference the hazard evidence. | A FlyPix review feature can appear on the map without becoming a road closure. |
| Dispatch assignment handler | `AssistanceRequest` and `ResponseUnit` | Verify the unit by ID, its readiness and capability match. Obtain a route proposal. In one Dispatch transaction, recheck concurrency and save the assignment and unit booking. | One active assignment per request and one active assignment per unit. The unit stays booked visibly if route publication must be retried. |
| Routing proposal and publication handlers | `RouteInstruction` and references to `EvacuationCase` or `DispatchAssignment` | The proposal handler stores a short-lived candidate. The publication handler passes the candidate, approval and current evidence to `RouteInstruction`. | Each civilian case or dispatch assignment has at most one current route version. Routing owns publication and withdrawal. |

## HTTP endpoints

`M`, `S`, `R` and `D` stand for Management, Situation Data, Routing and Dispatch. Paths are relative to the receiving service. The proposed web entry point can proxy these service paths. Browser clients never need RabbitMQ access.

| Caller → owner | Method and path | Operation and request | Success response |
| --- | --- | --- | --- |
| Reporter → M | `POST /v1/alerts` | `SubmitHazardAlert` with `clientAlertId`, `hazardType`, reported GeoJSON geometry, `sourceRef` and `observedAt` | `201` with `alertId`, `incidentId`, `situationStatus` and either `snapshotId` or `situationPath`. The alert is persisted even when the snapshot is pending. |
| M → S | `GET /v1/incidents/{incidentId}/situation` | `GetSituation`; optionally pass an area of interest by reference | `200` with `snapshotId`, `asOf`, layer data or references, population coverage, source revisions and gaps. `202` with readiness information while the snapshot is building. |
| Coordinator → M | `POST /v1/incidents/{incidentId}/cases` | `OpenEvacuationCase` with `subjectRef` and `mobilityProfile` | `201` with `caseId` and `caseVersion`; `200` with the existing case for a new command identifying the same subject and incident. |
| D, R or authorized client → M | `GET /v1/cases/{caseId}` | Read the case and its incident reference | `200` with `incidentId`, case version and role-scoped fields. Routing can request the mobility profile and latest position evidence for an authorized case. |
| Coordinator → M | `GET /v1/incidents/{incidentId}/monitor` | Read the case and operations projection for the authorized incident | `200` with coverage and projection timestamps, scoped case positions and progress, and current route and assignment references. Fetch terrain layers from Situation Data. |
| Resident or operator → M | `POST /v1/cases/{caseId}/progress-reports` | `ReportEvacuationProgress` with `reportId`, `reportType`, `sourceRef`, `observedAt` and route version where relevant. Position reports include the point and accuracy if known. | `200` with accepted report, current case version and progress state. A position does not confirm exit. |
| Operator or importer → S | `POST /v1/hazards/{hazardId}/revisions` | `RecordHazardRevision` with GeoJSON extent, `effectiveWindow`, and evidence metadata | `201` with hazard revision and review status. |
| Operator → S | `POST /v1/segments/{segmentId}/restrictions` | `SetAccessRestriction` with `restriction`, `affectedModes`, `effectiveWindow`, `evidence` and expected segment version | `200` with updated segment version and restriction ID. |
| R → S | `GET /v1/incidents/{incidentId}/routing-snapshot` | `GetRoutingSnapshot` | `200` with `snapshotId`, evidence revisions, validated exits, access constraints and their effective windows. |
| R → S | `POST /v1/route-validations` | `ValidateRoute`, a read-only evaluation of `incidentId`, route legs with traversal windows, travel and access requirements, and either `snapshotId` or `useLatest: true` | `200` with `eligible`, `conflicts` and the snapshot or revisions checked. `eligible: false` is a completed evaluation. |
| Coordinator or D → R | `POST /v1/route-proposals` | `ProposeRoute` with `incidentId`, route subject, origin or pickup, travel mode and requirements | `201` with `proposalId`, `expiresAt`, proposal legs and snapshot reference. |
| M, D or authorized operator → R | `GET /v1/route-instructions/{kind}/{id}` | `GetCurrentRoute` by subject | `200` with `state`, `aggregateVersion`, current route if one exists, and latest withdrawal or unresolved reason if applicable. |
| Authorized operator → R | `GET /v1/route-instructions/{kind}/{id}/proposals` | Read pending replacement proposals by subject | `200` with proposals still available to review. |
| Coordinator or authorized D → R | `POST /v1/route-instructions/{kind}/{id}/publications` | `PublishRoute` with `proposalId` and expected instruction aggregate version | `200` with `routeVersion`, `aggregateVersion`, validity and snapshot reference after revalidation. |
| Authorized operator or Routing workflow → R | `POST /v1/route-instructions/{kind}/{id}/withdrawals` | `WithdrawRoute` with target `routeVersion`, expected instruction aggregate version, reason and evidence reference | `200` with the withdrawal result. A stale command cannot withdraw a newer route. The event-driven handler can call this operation in process. |
| Resident or operator → D | `POST /v1/assistance-requests` | `RequestAssistance` with `caseId`, pickup point, required capabilities and outbound transport requirement | `201` with `requestId`, `requestVersion` and state `Open`. |
| Dispatcher → D | `GET /v1/response-units?requestId={requestId}` | `GetAvailableUnits` for the request | `200` with eligible unit candidates and their last known readiness. Availability is rechecked when booking. |
| Dispatcher → D | `POST /v1/assistance-requests/{requestId}/assignments` | `AssignUnit` with `unitId` and expected request version | `201` with `assignmentId` and `PendingRoutePublication`. Dispatch obtains the route proposal before booking and publishes after the booking commits. |
| M, authorized D client or unit → D | `GET /v1/assignments/{assignmentId}` | `GetAssignment` | `200` with role-scoped assignment state and current route reference if ready. |
| Authorized unit or dispatcher → D | `GET /v1/response-units/{unitId}/active-assignment` | Find the current booking, with task details scoped to the caller | `200` with the assignment if it exists and its readiness; `204` when the authorized unit has no booking. |
| Unit or dispatcher → D | `POST /v1/assignments/{assignmentId}/progress-reports` | `ReportAssignmentProgress` with `reportId`, step, report source and observation time | `200` with assignment state and version. Reports can acknowledge, record arrival at pickup, confirm pickup, or complete the task. An authorized dispatcher may record a confirmed cancellation and reason. |

Use `kind = civilian-case` with a `caseId`, or `kind = response-assignment` with an `assignmentId`. Authorization checks the requested incident, case or assignment. Dispatch can publish only for a booked assignment it owns and under dispatcher authority. Browsers and service callers do not supply the actor's role as a request field. Resident and responder views use authorized route details for their case or assignment. Operators use read views for follow-up work, including incomplete and withdrawn instructions.

Mutating requests carry `commandId` and `correlationId`. Use stable IDs on retry, and `expectedVersion` on operations that can conflict with another update. For a route subject with no instruction aggregate, the first publication uses `expectedVersion: 0`. IDs are opaque strings. Timestamps are UTC and GeoJSON coordinates use longitude, then latitude. Use a finite validity window for issued routes.

Use `400` for invalid input, `403` for insufficient permission, `404` for an unknown resource under the caller's scope, `409` for concurrent or lifecycle conflicts, `422` for a valid request with no eligible route, and `503` for a required dependency that cannot respond. Use `application/problem+json` with a stable `code`, such as `VersionConflict`, `UnitUnavailable`, `NoEligibleExit` or `RoutingProviderUnavailable`, and a `correlationId`. The caller can read current state before retrying a version conflict. [RFC 9457 Problem Details](https://www.rfc-editor.org/rfc/rfc9457.html).

## RabbitMQ bindings and consumers

Each row is a binding from the durable topic exchange `evacuation.events`. A service with multiple bindings receives its own copy through its queue. Producers declare their exchange and consumers declare their queues and bindings before the demo starts.

| Publisher | Event and routing key | Queue receiving the event | Processing result |
| --- | --- | --- | --- |
| Management | `HazardAlertRecorded.v1` → `management.hazard-alert-recorded.v1` | `situation.events` | Refresh the snapshot for the reported area, preserving the evidence status. |
| Situation Data | `SituationChanged.v1` → `situation.changed.v1` | `routing.events`, `management.events`, `dispatch.events` | Routing revalidates affected instructions. Management marks the relevant map layer for refresh. Dispatch checks active response routes. |
| Routing | `RoutePublished.v1` → `routing.route-published.v1` | `management.events`, `dispatch.events` | Management updates the monitor. Dispatch handles response subjects, associates the version with the assignment and makes a booked task ready. |
| Routing | `RouteWithdrawn.v1` → `routing.route-withdrawn.v1` | `management.events`, `dispatch.events` | Mark the named version withdrawn. Dispatch tracks affected tasks for route review. The monitor keeps a later current version if it already exists. |
| Dispatch | `AssistanceRequested.v1` → `dispatch.assistance-requested.v1` | `management.events` | Show that the case has an open request. |
| Dispatch | `AssignmentChanged.v1` → `dispatch.assignment-changed.v1` | `management.events` | Update the assistance status shown for the case. Pickup leaves the exit confirmation unchanged. |
| Management | `CaseProgressChanged.v1` → `management.case-progress-changed.v1` | No remote binding is required for the MVP monitor. | Management serves confirmed case progress through its own read view. The event is available if a downstream projection is added. |

Keep shared event payloads minimal. Consumers fetch detailed positions, routes or pickup details from the owner under the applicable role. Deduplicate using `(consumerName, eventId)` and commit a consumer receipt with its state change. Use publisher confirms and verify broker routing for messages that require a subscriber before marking the outbox entry sent. Send processing failures to a dead-letter queue after bounded retries. For a missing revision that affects an outcome, read current state from the owner. Ordering protection uses the producer's `subjectVersion` and the target `routeVersion` for route events. A late withdrawal of version 1 cannot retire version 2.

The offline Dispatch adapter uses both notifications to wake its periodic reconciliation. It acknowledges the wakeup and looks up current restrictions in Routing. The Dispatch command checks the assignment version before writing. [Demo API, broker settings and integration test](tasks/04-dispatch-openrouteservice.md#ranking-i-automatyczne-przeliczenie).

## Worked contract example for the bridge closure

The authorized operator calls `POST /v1/segments/bridge-2/restrictions` on Situation Data.

```json
{
  "commandId": "cmd-close-bridge-2",
  "correlationId": "incident-42",
  "expectedVersion": 6,
  "restriction": "closed",
  "affectedModes": ["walking", "driving"],
  "effectiveWindow": { "start": "2026-11-27T19:30:00Z", "end": null },
  "evidence": {
    "sourceRef": "field-report-12",
    "observedAt": "2026-11-27T19:29:00Z",
    "status": "confirmed"
  }
}
```

Situation Data returns `200` after committing the version and the outbox record. The HTTP response confirms the segment update independently of downstream processing.

```json
{
  "segmentId": "bridge-2",
  "segmentVersion": 7,
  "restrictionId": "restriction-31"
}
```

Routing receives the following event on `routing.events` through the binding `situation.changed.v1`. The affected modes apply to civilian and response vehicles using the same travel mode under the current access rules.

```json
{
  "eventId": "evt-983",
  "type": "SituationChanged.v1",
  "producer": "situation-data",
  "subjectId": "bridge-2",
  "subjectVersion": 7,
  "occurredAt": "2026-11-27T19:30:05Z",
  "correlationId": "incident-42",
  "causationId": "cmd-close-bridge-2",
  "payload": {
    "affectedSegmentIds": ["bridge-2"],
    "affectedModes": ["walking", "driving"],
    "restriction": "closed",
    "evidenceStatus": "confirmed",
    "effectiveWindow": { "start": "2026-11-27T19:30:00Z", "end": null }
  }
}
```

Routing selects current instructions that intersect the affected segment during the closure. It validates them with Situation Data, withdraws invalid versions through the relevant `RouteInstruction` roots and emits `RouteWithdrawn.v1`. It can then store a replacement proposal for review.

To approve a replacement for civilian case `case-14`, the coordinator calls `POST /v1/route-instructions/civilian-case/case-14/publications`. The expected aggregate version is the value returned by `GetCurrentRoute` after withdrawal. It is a concurrency token distinct from the displayed route version.

```json
{
  "commandId": "cmd-publish-case-14",
  "correlationId": "incident-42",
  "expectedVersion": 2,
  "proposalId": "proposal-27"
}
```

Before committing, Routing calls `ValidateRoute` with `useLatest: true`. If the proposal still qualifies and version 2 is current, Routing commits the replacement and emits `RoutePublished.v1`. If another operation already moved the aggregate forward, the response is `409 VersionConflict` and the caller reloads current state. See both interaction paths in the [sequence diagrams](mvp-commands-events.md#sequence-2-bridge-closure-and-decision-update).

## Worked contract example for unit booking

The dispatcher calls `POST /v1/assistance-requests/request-8/assignments` on Dispatch.

```json
{
  "commandId": "cmd-assign-request-8",
  "correlationId": "incident-42",
  "expectedVersion": 1,
  "unitId": "unit-3"
}
```

Dispatch generates a stable proposed `assignmentId` for this command and calls `POST /v1/route-proposals` on Routing with the `response-assignment` subject and task requirements. After obtaining an eligible proposal, it rechecks the request version and unit availability in its own transaction. It commits the booking and returns `201` with the following state.

```json
{
  "assignmentId": "assignment-11",
  "requestId": "request-8",
  "requestVersion": 2,
  "unitId": "unit-3",
  "state": "PendingRoutePublication"
}
```

After booking commits, Dispatch calls `PublishRoute` for `response-assignment/assignment-11` with the proposal and dispatcher authorization. Receiving `RoutePublished.v1` for that assignment lets Dispatch mark the task ready. The unit reads it through `GetAssignment`. If the route cannot be published or its outcome is unknown, Dispatch keeps the task visible and reconciles with Routing before issuing a different instruction or reassigning the unit.
