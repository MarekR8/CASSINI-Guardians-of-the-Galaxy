# SH-T04 Reservations and reassignment

- **Owner:** __________
- **Directories:** `src/Sheltering/`, `contracts/http/sheltering/`, `tests/sheltering_integration.py`
- **Technologies:** C#, CQRS, transactional state and outbox, optional PostgreSQL for Stage B
- **Baseline:** [04 Dispatch booking and route review](../04-dispatch-openrouteservice.md).

## What the demo shows

Two requests compete for the last place. Later a shelter closes while a reserved party is en route. A replacement either takes effect together with the new holds or stays explicitly unresolved.

## Scope

### Stage A (demo)

- [ ] Implement `OpenShelterAssignment`, `ReserveShelterPlaces`, `ReassignShelterPlaces`, `InvalidateShelterAssignment` and `ReleaseShelterReservation`.
- [ ] Rank only feasible shelters, preferring a common destination where appropriate and then a shorter eligible journey. Recheck actual capacity, intake windows, care constraints and route evidence before assigning.
- [ ] Atomically persist all parts of each booking or transfer with receipts and outbox entries. Verify one current assignment per case and leg, rejection of incomplete batches, and no release of existing occupancy during a transfer. Deliver the SH-W4 state and contract.

### Stage B (completion)

- [ ] Move the transaction into an owned PostgreSQL store with migrations and version checks spanning all affected shelters and assignments. Verify concurrency from two service instances.
- [ ] Store reconciliation evidence and test duplicate, stale and restart cases against persisted state.

## Dependencies and verification

Receive SH-W1, SH-W2 and SH-W3. Give SH-W4 to SH-T05, SH-T06 and SH-T07. Coordinate ownership of the shared HTTP test suite and fixture IDs with SH-T07. Cover SH-A04 and SH-A06, including simultaneous final-place requests and a failed transfer with an original route that remains eligible.

## Results

_(Fill in concurrency outcomes, rejected cases, storage checks and unresolved limits.)_
