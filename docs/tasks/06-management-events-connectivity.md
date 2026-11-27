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
- [ ] Extend `POST /commands/progress` with position and evidence (`galileoUsed`, `osnma`, `egnos`, `mode`) and distinguish `pickup` from `exitConfirmed`. Publish the contract in `contracts/` → **W2** for person 5.
- [ ] `GET /queries/incident` returns counters: in the zone (estimate from task 1), confirmed outside the zone, unconfirmed.
- [ ] Link simulator (e.g. Gateway middleware or a separate process) + `GET/POST /commands/link-profile`. Agree its location with person 7 (Gateway owner).
- [ ] `POST /commands/ewss-alert` (mock) and `GET /queries/ewss-alerts`.

### Stage B (completion)
- [ ] `IEventBus` + outbox + RabbitMQ in the Compose profile `full`; events `SituationChanged.v1`, `RoutePublished.v1`, `RouteWithdrawn.v1`, `AssignmentChanged.v1` → **W3**.
- [ ] Management in PostgreSQL (`management_db`), EF Core migrations.

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

_(fill in)_
