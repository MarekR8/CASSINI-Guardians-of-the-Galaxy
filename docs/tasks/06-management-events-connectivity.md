# Task 6 — Management, events and satellite connectivity

**Owner:** __________
**Directories:** `src/Management/`, `src/Shared/Messaging/` (new), `compose.yaml` (profile `full`), `contracts/`
**Technologies:** RabbitMQ (outbox, `evacuation.events`), IRIS² / GOVSATCOM (link simulator), EWSS (alert broadcast, mock)

## What the demo shows

A "terrestrial / IRIS²" link switch. When the link drops, field commands wait in a queue; after switching to the satellite link they arrive with a delay and without duplicates. The coordinator sends an EWSS alert to the zone. The "confirmed outside the zone" counter increases only after an exit confirmation with position evidence.

## Technology concept

- **RabbitMQ**: topic exchange `evacuation.events`. The service writes the event to an outbox together with its state; a `BackgroundService` publishes with publisher confirms. The consumer deduplicates by `messageId`. `IEventBus` has `InMemoryEventBus` (default, offline) and `RabbitMqEventBus` (`Integrations:Bus:Mode=live`).
- **IRIS² / GOVSATCOM**: no public API, so an `ILinkSimulator` sits between the client and the Gateway with profiles `terrestrial`, `iris2` (higher latency, lower throughput) and `down`. A store-and-forward queue retries with the same `commandId`. GOVSATCOM is only a profile and a slide.
- **EWSS**: `IEwssBroadcaster`; the mock stores the message (level, zone as a polygon, text up to ~100 characters) and exposes it to Android and the dashboard.

## Scope

### Stage A (demo)
- [x] Extend `POST /commands/progress` with position and evidence (`galileoUsed`, `osnma`, `egnos`, `mode`) and distinguish `pickup` from `exitConfirmed`. Publish the contract in `contracts/` → **W2** for person 5.
- [x] `GET /queries/incident` returns counters: in the zone (estimate from task 1), confirmed outside the zone, unconfirmed.
- [x] Link simulator (e.g. Gateway middleware or a separate process) + `GET/POST /commands/link-profile`. Agree its location with person 7 (Gateway owner).
- [x] `POST /commands/ewss-alert` (mock) and `GET /queries/ewss-alerts`.

### Stage B (completion)
- [x] `IEventBus` + outbox + RabbitMQ in the Compose profile `full`; events `SituationChanged.v1`, `RoutePublished.v1`, `RouteWithdrawn.v1`, `AssignmentChanged.v1` → **W3**.
- [x] Management in PostgreSQL (`management_db`), EF Core migrations.

## Dependencies

- Waiting on you: 5 (W2), 2, 4 and 7 (W3). Deliver the W2 contract first, even as a draft.
- You wait for: task 1 (population estimate for counters), task 7 (simulator location in the Gateway).

## Where to find data

- RabbitMQ: image `rabbitmq:4.4-management`, `RabbitMQ.Client` 7.x, the outbox pattern in .NET docs.
- IRIS²/GOVSATCOM: link parameters (LEO latency ~30–50 ms, GEO ~600 ms) from ESA/EUSPA materials; record the source.
- EWSS: EUSPA, Common Alert Message Format (CAMF).

## How to test

```sh
python scripts/offline_demo.py --test                          # InMemoryEventBus by default
docker compose --profile full up -d rabbitmq                    # Stage B
python tests/offline_integration.py http://127.0.0.1:5080       # on a separate environment
```
- Test: profile `down` → command queued; switch to `iris2` → exactly one record; same `commandId` with different content → 409.
- Test: `pickup` does not increase the "outside the zone" counter; only `exitConfirmed` does.
- Stage B: stop the consumer, publish an event, start it again → the event is delivered once (dedup).

## Where to save results

- Contracts: `contracts/management/*.json`, `contracts/events/*.json` (example payloads).
- Section below: measured profile latencies, behavior when the link is lost.

## Results

Branch `feature/ml/06-management-events-connectivity`. All data in the tests is synthetic (cases `case-14`…`case-16`, positions at the demo exit nodes, `incident-42`).

### Stage A

- **W2 contract**: [`contracts/management/`](../../contracts/management/) holds the schema and examples, and [`contracts/README.md`](../../contracts/README.md) lists the error codes. `exit-confirmed` requires `latitude`, `longitude`, `accuracy` (≤ 100 m), `observedAt` (at most 2 min in the future), `evidence { galileoUsed, osnma: simulated|verified|none, egnos: applied|unknown }` and `mode: measured|simulated|replay`. A late field report may omit `expectedVersion`. `pickup` is a separate `PickedUp` milestone. Android's existing `ExitReport` JSON matches the contract once it adds `caseId`.
- **Counters** in `GET /queries/incident`: `inZoneEstimate` (Situation GHSL estimate, `null` with `inZoneStatus: "unavailable"` when Situation is unreachable), `confirmedOutside` (exits with a position fix only), `confirmedOutsideSimulated`, `unconfirmed` (registered cases) and `estimatedWithoutConfirmedExit`.
- **Link simulator**: implemented in the Gateway (`src/Gateway/Link/`) as agreed with the operator in place of person 7. Only requests with `X-Cassini-Link: field` use the field link. `GET /api/link/queries/link-status` and `POST /api/link/commands/link-profile`; the query is a `GET` under `queries/` to keep CQRS. While the link is `down`, commands are queued (`202 Queued`, never a delivery confirmation) and persisted in `link-simulator.json`. After switching to `iris2` they are delivered in order with an unchanged body, so the owner's `commandId` receipts prevent duplicates. Contract: [`contracts/gateway/README.md`](../../contracts/gateway/README.md).
- **EWSS mock**: `POST /commands/ewss-alert` (CAP-style `level`, text ≤ 100 characters, one GeoJSON Polygon ring) and `GET /queries/ewss-alerts`. Each broadcast carries `delivery: "simulated"` and `provenance.mode: "mock"`. `Integrations:Ewss:Mode=live` is rejected at startup because EWSS has no public broadcast interface.
- The dashboard sends the exit node position labeled `SYMULACJA` (`mode: simulated`, `osnma: none`, `egnos: unknown`) and shows the counters.

Measured on the development laptop, Gateway → Management over loopback (`tests/connectivity_integration.py`, one field command each, total / simulated ms): terrestrial 27–98 / 10, iris2 56–170 / 40, govsatcom 616–699 / 600. The first request includes warm-up. The profile values are demo assumptions taken from this task (LEO round trip ~30–50 ms, GEO ~600 ms). They are not IRIS² or GOVSATCOM specifications, and no public API exists for either.

When the link is lost, commands wait in the queue; a retry returns the same entry, and the same `commandId` with another body returns `409`. When a delivered command is retried while the link is down, the Gateway returns the stored answer with `X-Cassini-Link-Replay`. If the owner is unreachable or answers 5xx, the command stays queued with backoff up to 8 s. A 4xx answer ends delivery as `Rejected`.

### Stage B

- `src/Shared/Messaging`: envelope, routing keys, `IEventBus`, `InMemoryEventBus` (default, offline), `OutboxPublisher` and `EventSubscription`. `src/Shared/Messaging/RabbitMq`: `RabbitMqEventBus` with the durable topic exchange `evacuation.events`, persistent messages with `MessageId = eventId`, publisher confirms, quorum queues with manual ack, a delivery limit of 5 and the `evacuation.events.dead-letter` queue.
- Management commits `CaseProgressChanged.v1` (milestones only) and `HazardAlertRecorded.v1` to its outbox in the same write as the state. It consumes `AssignmentChanged.v1` into `case.assistance` with an inbox receipt per `eventId` and the newest `subjectVersion`. Pickup never confirms the exit.
- [`contracts/events/`](../../contracts/events/) has example payloads for all seven events (W3). Situation, Routing and Dispatch still poll over HTTP. Each owner wires its producer with `IOutboxStore` + `OutboxPublisher`, following [`contracts/events/README.md`](../../contracts/events/README.md).
- Management in PostgreSQL (`management_db`): EF Core migration `InitialManagement`, enabled with `-p:EnableManagementPostgres=true` and `ConnectionStrings:Management`. RabbitMQ needs `-p:EnableRabbitMq=true`, `Integrations:Bus:Mode=live` and `Integrations:Bus:Uri`. The default build adds no packages. Compose: `docker compose -f compose.yaml -f compose.management-full.yaml --profile full up -d --build`.

### Checks run

- `python scripts/offline_demo.py --test`: passed (`offline_integration.py` and the new `connectivity_integration.py`).
- `dotnet run` for `src/Management/tests`, `src/Gateway/tests`, `src/Shared/Messaging/tests` and `src/Routing/tests`: passed.
- Against local RabbitMQ 4.4.0 and PostgreSQL 17 from conda-forge (no Docker on the machine): `Cassini.Messaging.Tests` with `-p:EnableRabbitMq=true` (stop → publish → start delivered once, duplicate `eventId` ignored) and `tests/management_full_integration.py` (outbox → RabbitMQ, stop/publish/start applied once, one inbox row per event, state and receipts survive a restart). Both passed, including a second run on the same database.

### Not verified

- Docker: `docker compose config`, the image build and `compose.management-full.yaml` were not run because Docker is not installed here. The new CI job `verify-management-full` covers RabbitMQ and PostgreSQL with service containers.
- The dashboard was not opened in a browser (no Node or Playwright here); the `app.js` change was reviewed by hand and exercised indirectly only through the HTTP contract.
- Android delivery to `POST /commands/progress` (task 5) and real GNSS evidence need a device.
