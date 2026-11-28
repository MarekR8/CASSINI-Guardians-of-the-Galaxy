# Sheltering and Grouping team tasks

Build the offline acceptance slice for [SH-01 through SH-06](../../domain/business-processes.md#detailed-sheltering-and-grouping-workflows). These tasks use the Stage A and Stage B format of the [original team tasks](../README.md). Start Stage B after the shared Stage A exercise is green. Use the [implementation guide](../../sheltering-implementation-guide.md) for ownership, order and the SH-A01–SH-A08 acceptance checks.

| Task | Scope | Builds on the original task |
| --- | --- | --- |
| SH-T01 | [Assembly points and shelter resources](01-sites-and-resources.md) | [01 hazard layers](../01-copernicus-hazard-layers.md), [02 road network and closures](../02-road-network-flypix-closures.md) |
| SH-T02 | [Grouping and required care](02-grouping-and-care.md) | [03 routing algorithm](../03-route-distribution-algorithm.md), [06 Management](../06-management-events-connectivity.md) |
| SH-T03 | [Eligible destinations and the first leg](03-eligible-destinations.md) | [02 road network](../02-road-network-flypix-closures.md), [03 routing](../03-route-distribution-algorithm.md), [04 route providers](../04-dispatch-openrouteservice.md) |
| SH-T04 | [Reservations and reassignment](04-reservations-and-reassignment.md) | [04 Dispatch booking](../04-dispatch-openrouteservice.md) |
| SH-T05 | [Admissions and departures](05-admissions-and-departures.md) | [05 Android progress evidence](../05-android-galileo.md), [06 Management progress](../06-management-events-connectivity.md) |
| SH-T06 | [Publication and events](06-publication-and-events.md) | [06 event delivery](../06-management-events-connectivity.md), [04 route review](../04-dispatch-openrouteservice.md) |
| SH-T07 | [Dashboard and offline rehearsal](07-dashboard-and-rehearsal.md) | [07 dashboard and rehearsal](../07-dashboard-map-demo.md) |

### Shared rules

1. Agree on fixture IDs and contracts first. Use synthetic case references, one incident, one assembly point, two shelters, different starting points and a shared demo clock. Document source and revision for every imported observation.
2. Keep the `sample` demo available and the new slice opt-in. Stage A runs without remote providers or keys. Stage B dependencies go under the optional Compose profile.
3. Writes run through the owning aggregate's command handler. Use command receipts, expected revisions and authorized roles. Track an approved route, a reservation, admission and departure independently.
4. Each task publishes a fixture or DTO example, verifies its checks and records results. Leave the Owner and Results fields open until the team assigns work and runs verification.

### Integration handoffs

| ID | Provider → consumer | Required handoff | Temporary fixture |
| --- | --- | --- | --- |
| SH-W0 | All owners → all tasks | Shared scenario manifest and IDs, including separate exit and shelter legs. | One minimal synthetic JSON scenario from the first implementation checkpoint. |
| SH-W1 | SH-T01 → SH-T03, SH-T04, SH-T07 | Site status, operating window, place reference, availability and their revisions. | Two static shelter entries and an assembly point. |
| SH-W2 | SH-T02 → SH-T03, SH-T04, SH-T07 | Travel party proposal and approved group plan revision with required care. | One household in two parties and one required-care link. |
| SH-W3 | SH-T03 → SH-T04, SH-T06 | Route eligibility, expected arrival, first-leg fallback and reason codes. | Recorded route candidates and one unavailable corridor. |
| SH-W4 | SH-T04 → SH-T05, SH-T06, SH-T07 | Current assignment, reservation and replacement contract per case and leg. | Reserved and pending assignment examples. |
| SH-W5 | SH-T05, SH-T06 → SH-T07 | Admission and departure facts, authoritative counters and current publication state. | Partial arrival and withdrawn route examples. |

Agree on SH-W0 together before branching work. SH-T01 and SH-T02 can start in parallel. SH-T03 and SH-T04 can develop against fixture contracts until the upstream providers are ready. SH-T07 owns the joint rehearsal after all handoffs are verified.
