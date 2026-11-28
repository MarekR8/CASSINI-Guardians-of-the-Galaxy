# Contracts

Example payloads and schemas shared between services and clients. Paths are relative to the owning service; the Gateway exposes them under `/api/<service>/…`.

| Directory | Owner | Consumers |
| --- | --- | --- |
| [`management/`](management/) | Management (task 6) | Android (task 5, **W2**), dashboard (task 7), Dispatch |
| [`events/`](events/) | Producer named in each file (task 6 delivers the envelope, **W3**) | Services subscribed to `evacuation.events` |
| [`gateway/`](gateway/) | Gateway link simulator (task 6, hosted by task 7) | Dashboard, field clients, Android |
| [`http/grouping/`](http/grouping/) | Management Grouping | Routing, Sheltering, coordinator |
| [`http/routing/`](http/routing/) | Routing | Sheltering, Dispatch, coordinator |
| [`http/sheltering/`](http/sheltering/) | Situation assembly-point catalog and Sheltering (separate owners) | Routing, Management, coordinator, shelter operator |

## Common wire conventions and migration limits

- Keep query and command handlers separate: `GET /queries/…` reads; `POST /commands/…` writes only at the owner. Gateway prepends `/api/{service}` without owning state.
- IDs are opaque strings; pass `incidentId`, `caseId`, `legId`, `shelterId` and `commandId` where the owner contract requires them. Reuse a command ID only for an identical retry; reconcile unknown results before taking a new decision.
- Keep aggregate version, catalog revision, Situation revision, and route version separate. Mutation checks belong to the owner and stale evidence must not become an eligible route.
- Use ISO 8601 UTC instants; position geometry uses WGS 84 longitude/latitude, distances metres, travel durations seconds except where an existing field explicitly says otherwise. Sheltering place and intake units are separate.
- Existing clients receive either a problem response with stable `code` or a JSON error object with `code` and `detail`; migrate this shape additively, not by silently changing existing codes/statuses. Status 409 is a version or command conflict, 422 is a rejected decision and 503 marks unavailable/uncertain evidence where documented.
- The [event envelope](events/README.md) has explicit `eventId`, producer, subject version, correlation and causation identifiers. Local owner outboxes and broker confirms have different meanings from business delivery, pickup, exit, reservation, route publication or admission. Never infer those milestones from one another.

The mandatory sample flow uses these existing contracts directly. A single common error/configuration implementation and authenticated service-to-service claims remain migration work; no DTO or existing on-disk state format is replaced by this document.

## Management

| Method and path | Request | Success | Rejections |
| --- | --- | --- | --- |
| `POST /commands/progress` | [schema](management/progress-command.schema.json), examples: [exit](management/progress-exit-confirmed.example.json), [pickup](management/progress-pickup.example.json) | `200` with the updated case | `400 InvalidProgress`, `400 PositionEvidenceRequired`, `404 CaseNotFound`, `409 VersionConflict`, `409 InvalidTransition`, `409 ExitAlreadyConfirmed`, `409 CommandIdConflict` |
| `GET /queries/incident` | — | `200` with `incidentId`, `alerts`, `cases` and [`counters`](management/incident-counters.example.json) | — |
| `POST /commands/ewss-alert` | [example](management/ewss-alert-command.example.json) | `200` with the stored broadcast | `400 InvalidEwssAlert`, `409 CommandIdConflict` |
| `GET /queries/ewss-alerts` | — | `200` [list](management/ewss-alerts-query.example.json) | — |

Progress states: `Open → Acknowledged → Moving`, `Open|Acknowledged|Moving → PickedUp` and `Open|Acknowledged|Moving|PickedUp → ExitConfirmed`. Pickup never counts as being outside the zone. `confirmedOutside` counts exit confirmations that carry a position fix; `confirmedOutsideSimulated` is the subset whose `mode` is not `measured`. `inZoneEstimate` is the Situation population estimate (GHSL, task 1) or `null` with `inZoneStatus: "unavailable"`.

The EWSS mock stores the message and marks it `delivery: "simulated"`. It never reports that a receiver got the alert.
