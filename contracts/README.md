# Contracts

Example payloads and schemas shared between services and clients. Paths are relative to the owning service; the Gateway exposes them under `/api/<service>/…`.

| Directory | Owner | Consumers |
| --- | --- | --- |
| [`management/`](management/) | Management (task 6) | Android (task 5, **W2**), dashboard (task 7), Dispatch |
| [`events/`](events/) | Producer named in each file (task 6 delivers the envelope, **W3**) | Services subscribed to `evacuation.events` |
| [`gateway/`](gateway/) | Gateway link simulator (task 6, hosted by task 7) | Dashboard, field clients |

## Management

| Method and path | Request | Success | Rejections |
| --- | --- | --- | --- |
| `POST /commands/progress` | [schema](management/progress-command.schema.json), examples: [exit](management/progress-exit-confirmed.example.json), [pickup](management/progress-pickup.example.json) | `200` with the updated case | `400 InvalidProgress`, `400 PositionEvidenceRequired`, `404 CaseNotFound`, `409 VersionConflict`, `409 InvalidTransition`, `409 ExitAlreadyConfirmed`, `409 CommandIdConflict` |
| `GET /queries/incident` | — | `200` with `incidentId`, `alerts`, `cases` and [`counters`](management/incident-counters.example.json) | — |
| `POST /commands/ewss-alert` | [example](management/ewss-alert-command.example.json) | `200` with the stored broadcast | `400 InvalidEwssAlert`, `409 CommandIdConflict` |
| `GET /queries/ewss-alerts` | — | `200` [list](management/ewss-alerts-query.example.json) | — |

Progress states: `Open → Acknowledged → Moving`, `Open|Acknowledged|Moving → PickedUp` and `Open|Acknowledged|Moving|PickedUp → ExitConfirmed`. Pickup never counts as being outside the zone. `confirmedOutside` counts exit confirmations that carry a position fix; `confirmedOutsideSimulated` is the subset whose `mode` is not `measured`. `inZoneEstimate` is the Situation population estimate (GHSL, task 1) or `null` with `inZoneStatus: "unavailable"`.

The EWSS mock stores the message and marks it `delivery: "simulated"`. It never reports that a receiver got the alert.
