# SH-T02 Grouping and required care

- **Owner:** __________
- **Directories:** `src/Management/Grouping/`, `data/demo/sheltering/`, `contracts/http/grouping/`
- **Technologies:** C#, versioned graph of typed case references and deterministic candidate evaluation
- **Baseline:** [03 routing algorithm](../03-route-distribution-algorithm.md) and [06 Management](../06-management-events-connectivity.md).

## What the demo shows

Two household members start on opposite sides of an obstruction. They can leave separately and still prefer the same shelter. A linked person requiring care travels with the assigned caregiver or triggers a review.

## Scope

### Plan for independent development

1. Add Grouping to Management and validate case references and mobility against the current Management snapshot.
2. Publish SH-W2 through Management's existing Gateway prefix. Keep proposals read-only. Approve by group revision, leg and evidence ID, and retain the approved plan revision for consumers.
3. Replay recorded route evidence locally until SH-T03 supplies current candidates. Use the sample incident and the site identifiers already used in SH-T01. Confirm the leg IDs and exercise clock with the team as part of SH-W0.
4. Verify SH-A02 and SH-A03 through the Gateway. In the combined exercise, Routing supplies route eligibility and Sheltering checks availability and reserves places before publication.

### Stage A (demo)

- [x] Implement the `EvacuationGroup` root in a separate Grouping module hosted by Management. Store incident-scoped membership, typed relationships and confirmed care handovers.
- [x] Implement `RegisterEvacuationGroup`, `UpdateGroupComposition`, `RecordCareHandover`, `ApproveTravelPlan` and `FlagTravelPlanForReview`. Keep travel-party proposals read-only and version the approved result.
- [x] Document the [SH-W2 HTTP contract](../../../contracts/http/grouping/README.md). Distinguish hard care requirements from a shared-destination preference. Compare travel and waiting time against clearance constraints. Use fixed route evidence until SH-T03 is ready.

### Stage B (completion)

- [ ] Add durable Grouping persistence and emit group, care and travel-plan events through the owning outbox. Verify that stale evidence flags only the affected plan revision.
- [ ] Enforce incident scope for group reads and writes. Use references or scoped queries for care details.

## Dependencies and verification

Receive SH-W0 and Management case/mobility reads. Deliver SH-W2 to SH-T03, SH-T04 and SH-T07. Save examples of a household split and a care handover in `contracts/http/grouping/`. Verify independent departures to one feasible shelter, separate destinations when a common one is infeasible and rejection of an unauthorized care split. Accept SH-A02 and SH-A03.

## Results

- Synthetic evidence in [sample-grouping.json](../../../data/demo/sheltering/sample-grouping.json) uses `incident-42`, cases `case-14`–`case-16`, the provisional `leg-shelter` ID, and the SH-T01 sample shelter IDs and clock.
- The [integration replay](../../../tests/grouping_integration.py) covers independent departures and a recorded closure (SH-A02), rejected care separation and a confirmed handover (SH-A03), command replay, group and plan revisions, stale evidence and local restart recovery.
- Grouping runs on recorded route candidates and synthetic roles. Confirm SH-W0 and SH-W2 with other owners before the joint exercise. The replay uses local HTTP without Routing candidates computed live, shelter capacity checks, or hardware.
