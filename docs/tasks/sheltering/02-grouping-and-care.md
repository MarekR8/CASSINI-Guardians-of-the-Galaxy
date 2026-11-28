# SH-T02 Grouping and required care

- **Owner:** __________
- **Directories:** `src/Management/Grouping/`, `data/demo/sheltering/`, `contracts/http/grouping/`
- **Technologies:** C#, versioned graph of typed case references and deterministic candidate evaluation
- **Baseline:** [03 routing algorithm](../03-route-distribution-algorithm.md) and [06 Management](../06-management-events-connectivity.md).

## What the demo shows

Two household members start on opposite sides of an obstruction. They can leave separately and still prefer the same shelter. A linked person requiring care travels with the assigned caregiver or triggers a review.

## Scope

### Stage A (demo)

- [ ] Implement the `EvacuationGroup` root in a separate Grouping module hosted by Management. Store incident-scoped membership, typed relationships and confirmed care handovers.
- [ ] Implement `RegisterEvacuationGroup`, `UpdateGroupComposition`, `RecordCareHandover`, `ApproveTravelPlan` and `FlagTravelPlanForReview`. Keep travel-party proposals read-only and version the approved result.
- [ ] Publish the SH-W2 contract. Distinguish hard care requirements from a shared-destination preference. Compare eligible travel or waiting time against clearance constraints rather than straight-line distance. Use fixed route evidence fixtures until SH-T03 is ready.

### Stage B (completion)

- [ ] Add durable Grouping persistence and emit group, care and travel-plan events through the owning outbox. Verify that stale evidence flags only the affected plan revision.
- [ ] Enforce incident scope for group reads and writes. Use references or scoped queries for care details.

## Dependencies and verification

Receive SH-W0 and Management case/mobility reads. Deliver SH-W2 to SH-T03, SH-T04 and SH-T07. Save examples of a household split and a care handover in `contracts/http/grouping/`. Verify independent departures to one feasible shelter, separate destinations when a common one is infeasible and rejection of an unauthorized care split. Accept SH-A02 and SH-A03.

## Results

_(Fill in the cases, group revision behavior, checks and evidence limitations.)_
