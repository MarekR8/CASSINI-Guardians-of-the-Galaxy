# SH-T06 Publication and events

- **Owner:** __________
- **Directories:** `src/Sheltering/`, `src/Routing/` (publication contract), `contracts/events/`, optional Compose profile
- **Technologies:** .NET application handlers, transactional outbox, scripted event replay, RabbitMQ in Stage B
- **Baseline:** [06 Management events](../06-management-events-connectivity.md) and [04 route review](../04-dispatch-openrouteservice.md).

## What the demo shows

A committed reservation appears as pending publication until Routing approves the instruction. After a closure, a replacement goes out with a newer version. Replaying the old event does not restore the old instruction.

## Scope

### Stage A (demo)

- [x] Make destination publication a separate step after successful booking. Wire the coordinator workflow to Routing's existing propose, publish and withdraw operations.
- [x] Read both owners on retries and handle missing publication, changed route evidence and stale assignment revisions. Preserve the receipt key for a repeated command.
- [x] Add event envelopes and replay fixtures for the new group, site, allocation and admission facts. Verify the SH-W5 instruction state without a live broker.

### Stage B (completion)

- [x] Connect the owner outboxes to the shared RabbitMQ topic exchange under the optional `full` profile. Add consumer deduplication and reconciliation against authoritative versions.
- [x] Test restart after commit, broker redelivery, and reordering across multiple events at the same subject revision. Count only committed facts in operational views.

## Dependencies and verification

Receive SH-W3 and SH-W4 and coordinate event payloads with each owner. Deliver current, pending and withdrawn instruction examples for SH-W5. Ensure a capacity hold is distinct from an issued route and that publishing fails safely when current eligibility cannot be validated. Cover SH-A06 and SH-A07.

## Results

All data in the tests is synthetic: `incident-42`, `case-31`, `leg-shelter`, `shelter-a` and `shelter-b`, with the SH-T04 fixtures. The same leg appears in the [SH-W5 examples](../../../contracts/http/sheltering/README.md#sh-w5-instruction-publication-sh-t06).

### Stage A

- **Routing publication contract** (`src/Routing/Publication/`): a `shelter-assignment` instruction aggregate with `publish-route`, `withdraw-route`, `route-instruction` and `route-outbox`.
  - Publishing repeats the SH-W3 `destination-route` proposal with `expectedEvidenceId`. A changed or unverifiable route commits nothing (`422 RouteIneligible`, or `503`).
  - Each write stores a receipt and outbox events. Route versions are never reused. A transfer supersedes the old route in the same commit.
  - The store reuses `CatalogStore` with an auxiliary journal and sits next to the draw journal.
- **Coordinator workflow** (`src/Sheltering/Publication/`):
  - `reconcile-shelter-instruction` reads the Sheltering assignment and the Routing instruction. It then publishes the current hold, withdraws guidance the assignment no longer supports, or does nothing.
  - The `commandId` is passed to Routing, so a retry finds the committed instruction or Routing's receipt.
  - A stale assignment version returns `409`. A change during publication returns `409 AssignmentChanged`.
  - `shelter-instruction` reports `PendingPublication`, `Current`, `WithdrawalPending`, `Withdrawn`, `NoInstruction` or `PublicationUnknown`. A capacity hold counts as `Current` only once Routing has committed an instruction for exactly that reservation.
- **Envelopes and replay:**
  - New examples for `ShelterAssignmentChanged.v1`, `ShelterAvailabilityChanged.v1` and `TravelPlanApproved.v1`.
  - [`contracts/events/replay/sh-w5-instructions.replay.json`](../../../contracts/events/replay/sh-w5-instructions.replay.json) has 18 events for one leg, with group, site and admission facts, a duplicate, a replayed old publication, a reordered pair at one revision and two gaps.
  - `src/Sheltering/tests` applies it to the instruction board without a broker: in order, redelivered, reversed and through `InMemoryEventBus`.

**SH-A07 publication failure.** After the booking, `shelter-a` closes. The reconcile returns `422 RouteIneligible`. The assignment stays `Reserved` and the state stays `PendingPublication`. Routing has no event. After invalidation the state is `NoInstruction`, because nothing was issued.

**SH-A06 replacement and withdrawal.**
- The booking at `shelter-b` is published as route 1. A retry with the same command returns the same result and adds no event.
- The transfer back to a reopened `shelter-a` publishes route 2. Route 1 is withdrawn as `Superseded` in the same commit: two events at instruction version 2.
- The second closure withdraws route 2 with `ShelterClosed`. The retry keeps one withdrawal.
- After a restart of every process, the state, the receipts and the events are unchanged.

**Versioning.**
- An old reconcile command cannot restore route 1 (`409 AssignmentVersionConflict`).
- A direct withdrawal with a stale aggregate version returns `409 VersionConflict`.
- A withdrawal of a route version that is not current returns `409 StaleRouteVersion`.

### Stage B

- **Broker connection:** `-p:EnableRabbitMq=true` with `Integrations:Bus:Mode=live` connects both outboxes to `evacuation.events` through the shared `OutboxPublisher`. The default build adds no packages. `EventBusSetup.AddLiveEventBus` is shared by Routing and Sheltering.
  - **Routing** publishes its instruction outbox. Delivery of the route events is required (mandatory).
  - **Sheltering** publishes its allocation outbox and every reception ledger outbox, including the SH-T05 admissions and departures. Confirmed event IDs are recorded in the state, or in `sheltering_published` (migration 2) with PostgreSQL.
- **Instruction board:** `shelter-instruction-board` consumes `sheltering.events`.
  - Deduplication is by `eventId`, and an older `subjectVersion` is ignored.
  - Same-revision events are ordered by `routeVersion`.
  - A version gap is filled with an authoritative read of the Sheltering assignment or the Routing instruction.
  - It counts only committed facts.
- **Compose:** [`compose.sheltering-full.yaml`](../../../compose.sheltering-full.yaml) builds Routing and Sheltering with RabbitMQ and adds the broker in the `full` profile.
- **`tests/sheltering_events_integration.py`** (CI job `verify-sheltering-events`, with PostgreSQL):
  - The Stage A exercise runs on the broker, and the board reaches `Withdrawn` from the events.
  - Every Routing event is redelivered with its ID, then replayed with new IDs in reverse order, including the same-revision transfer pair. The board is unchanged.
  - A booking is published while the broker is unreachable. Its events stay unconfirmed. After a restart with the broker they are published, and the board shows `Current` route 3.

### Checks run

On Windows with .NET SDK 10.0.404, Docker 28.5.1, RabbitMQ 4.4 and PostgreSQL 17 containers:
- `python tests/sheltering_publication_integration.py` passed.
- `dotnet run --project src/Sheltering/tests/Cassini.Sheltering.Tests.csproj` passed.
- `python tests/sheltering_events_integration.py` passed three times with the file store and once with the PostgreSQL store. All 14 Sheltering outbox events were marked in `sheltering_published`.
- `tests/sheltering_postgres_integration.py` passed on a fresh database.
- Regression on the default build passed:
  - `offline_demo.py --test` and `--sheltering --test`;
  - `sheltering_reservations_integration.py`, `destination_routing_integration.py` and `grouping_integration.py`;
  - the Routing, Management, Gateway and Messaging test projects.
- The Compose configuration of the new overlay passed.

### Limits

- The board is an in-memory projection. After a restart it is rebuilt lazily from the owners, so its counts are rebuilt rather than recovered from a store.
- An admission does not change the assignment version. The board shows `Admitted` only after an owner read.
- Management and Dispatch do not consume the shelter events yet.
- `TravelPlanApproved.v1` is a contract example. Grouping does not publish it yet.
- The workflow does not publish automatically after a booking. The coordinator, or a future job, calls `reconcile-shelter-instruction`.
