# SH-T04 Reservations and reassignment

- **Owner:** __________
- **Directories:** `src/Sheltering/`, `contracts/http/sheltering/`, `tests/sheltering_integration.py`
- **Technologies:** C#, CQRS, transactional state and outbox, optional PostgreSQL for Stage B
- **Baseline:** [04 Dispatch booking and route review](../04-dispatch-openrouteservice.md).

## What the demo shows

Two requests compete for the last place. Later a shelter closes while a reserved party is en route. A replacement either takes effect together with the new holds or stays explicitly unresolved.

## Scope

### Stage A (demo)

- [x] Implement `OpenShelterAssignment`, `ReserveShelterPlaces`, `ReassignShelterPlaces`, `InvalidateShelterAssignment` and `ReleaseShelterReservation`.
- [x] Rank only feasible shelters, preferring a common destination where appropriate and then a shorter eligible journey. Recheck actual capacity, intake windows, care constraints and route evidence before assigning.
- [x] Atomically persist all parts of each booking or transfer with receipts and outbox entries. Verify one current assignment per case and leg, rejection of incomplete batches, and no release of existing occupancy during a transfer. Deliver the SH-W4 state and contract.

### Stage B (completion)

- [x] Move the transaction into an owned PostgreSQL store with migrations and version checks spanning all affected shelters and assignments. Verify concurrency from two service instances.
- [x] Store reconciliation evidence and test duplicate, stale and restart cases against persisted state.

## Dependencies and verification

Receive SH-W1, SH-W2 and SH-W3. Give SH-W4 to SH-T05, SH-T06 and SH-T07. Coordinate ownership of the shared HTTP test suite and fixture IDs with SH-T07. Cover SH-A04 and SH-A06, including simultaneous final-place requests and a failed transfer with an original route that remains eligible.

## Results

Stage A is implemented in `src/Sheltering/Allocation/`. The [SH-W4 contract and examples](../../../contracts/http/sheltering/README.md#sh-w4-assignments-and-reservations) describe it. Run `python tests/sheltering_reservations_integration.py`, which is also a CI step. It starts fresh local services, replays the exercise and repeats the checks after a restart.

- **Concurrency.** `shelter-a` is reduced to one free place. Two simultaneous single-place requests from synthetic, node-routed people `case-31` and `case-32` give exactly one `200` and one `422 InsufficientCapacity`. In repeated runs each person won at least once. The loser stays `Pending` at version 1 and is then booked at `shelter-b`. Replaying the winning command returns its receipt. Reusing the command ID with changed content returns `409 CommandConflict`.
- **Rejected batches.** The required-care pair `case-15` and `case-16` (Grouping plan revision 2, assisted transport `transport-demo-north`) needs two accessible places where one remains. The whole batch gets `InsufficientAccessiblePlaces`, and availability and both assignments are unchanged. The following are also rejected:
  - reserving one member of the pair: `RequiredCareSplit`;
  - omitting the plan reference: `GroupPlanRefMismatch`;
  - using route evidence from before a catalog change: `RouteIneligible`;
  - adding `case-14` after `shelter-b` reached three arrivals in that hour: `IntakeLimitReached`.
- **Transfers.** A transfer to a shelter at its intake limit fails, and the original hold and assignment stay unchanged. A fresh Routing check confirms that the original route is still eligible, and invalidation is refused with `AssignmentStillEligible`. After `shelter-a` closes, `ReassignShelterPlaces` moves the hold to `shelter-b` in one commit, and the withdrawn reservation is recorded. Baseline occupancy at `shelter-a` is unchanged. After `shelter-b` also closes, no candidate remains. `InvalidateShelterAssignment` sets the assignment to `AwaitingReplacement` with the reason `SiteClosed` and releases its hold. The outbox holds the `ShelterAssignmentChanged.v1` events with withdrawn shelter references for SH-T06.
- **Storage.** Holds are written into each shelter's `reception.holds` and commitments (the SH-T05 ledger). Assignments, receipts and the allocation outbox are stored in the same catalog state file. `CatalogStore.Mutate` commits all of them in one write: a temporary file, then an atomic rename. Shelter configuration, admissions and departures take the same lock. After a restart the receipt, the assignment version, the hold counts and the outbox sequence are unchanged.
- **Admission handoff.** An SH-T05 admission of an SH-T04 hold converts exactly that hold, and the free places do not change. The assignment then reads as `Admitted`, and invalidating or releasing it is refused with `OccupancyRequiresDeparture`.
- **Regression.** These all pass: `offline_demo.py --test`, `offline_demo.py --sheltering --test`, `destination_routing_integration.py`, `grouping_integration.py`, and the Routing, Management, Gateway and Messaging test projects.

**Stage B.** Build with `-p:EnableShelteringPostgres=true` and set `ConnectionStrings:Sheltering` (Compose: add `compose.sheltering-postgres.yaml` with the `full` profile). Sheltering then keeps its whole state in its own PostgreSQL database instead of the JSON file. The code is in `src/Sheltering/Infrastructure/PostgresShelteringPersistence.cs`. Everything else goes through the same `CatalogStore` commands and queries as Stage A, so the file and database modes share the same decisions.

- **Schema and migrations.** The tables are `shelters`, `shelter_assignments`, `sheltering_receipts`, `sheltering_outbox`, `allocation_evidence` and `sheltering_meta`. Migrations are numbered, recorded in `sheltering_schema_migrations` and applied under an advisory lock, so several instances can start at once. The first instance seeds the catalog.
- **Transactions and version checks.** Every write locks the `sheltering_meta` row and then reads the latest committed state. So in any number of instances each command decides on current shelters and assignments. Only the changed rows are written:
  - every update of a shelter or an assignment requires its `row_version` to be unchanged;
  - the meta row requires the same `commit_seq`;
  - the schema enforces one assignment per case and leg, one assignment per reservation and one receipt per command.

  Reads use a consistent `REPEATABLE READ` snapshot.
- **Reconciliation evidence.** Every committed assignment change records an `allocation_evidence` row in the same transaction. The row holds the decision, the resulting status and version, the shelter and its version, the reservation, the Routing evidence ID, the Situation revision, the plan revision, any withdrawn reservation, the finding and the evidence reference. Read it with `GET /queries/allocation-evidence`. File mode stores the same evidence.
- **Verification.** `python tests/sheltering_postgres_integration.py` needs a fresh database in `ConnectionStrings__Sheltering` and is a CI job. It runs the full SH-A04/SH-A06 exercise with two Sheltering processes on one database; the final-place race sends one request to each instance. It also checks:
  - **duplicates:** the same `open-shelter-assignment` sent concurrently to both instances returns one receipt and creates one row; changed content returns `409 CommandConflict`;
  - **stale writes:** a reservation with an assignment version, or a shelter edit with a shelter version, that the other instance already advanced returns `409`;
  - **persisted evidence:** each instance reads the evidence written through the other;
  - **restart:** after a restart of every process, receipts, holds, versions, the outbox and the evidence come from the database unchanged.

  It passed 3 times on fresh `postgres:17` databases. The image `cassini-offline/sheltering:postgres` builds from the Dockerfile.

Unresolved limits:

- Writers across instances are serialized by one row lock. That suits the exercise load. Independent shelters are not committed in parallel.
- Each request reads the whole Sheltering state, which is sized for the exercise and not for production volumes. The outbox is stored but not published to RabbitMQ (SH-T06).
- Routing is called before the lock. A Situation change between that call and the commit is caught only by the shelter evidence revision.
- Routing evidence IDs include the shelter catalog revision, so any shelter edit invalidates outstanding candidates and they must be read again.
- Intake is counted per clock hour: holds with an arrival window plus admissions observed in that hour. Seeded baseline commitments without a ledger entry do not count.
- Hold expiry is not applied automatically. It takes an explicit `HoldExpired` release with an observation time.
- `Admitted` and `Closed` after departure are derived from the reception ledger and not stored. Publication or withdrawal through Routing belongs to SH-T06.
- SH-T04 holds do not change the catalog revision or the shelter version. `allocationRevision` tracks them.
- Node-routed synthetic people are not bound to a Management case.
