# Evacuation domain invariants

These rules describe valid system states and transitions for the proposed application. A field report may reveal that reality has breached a planning assumption. The system records that fact, invalidates affected recommendations, and makes the conflict visible to the coordinator.

| ID | Invariant | What the system must enforce |
| --- | --- | --- |
| INV-01 | A usable instruction contains only eligible route segments. | Check each segment over its expected traversal window against confirmed closures, hazard constraints, and access restrictions. A conflicting restriction invalidates the affected route, even for groups already moving. Segments that require explicit confirmation remain ineligible while their status is unknown. |
| INV-02 | Every active route matches the group's travel mode and essential access needs. | Verify mode, step-free access where required, walking limits, and feasible pickup and transfer points. An unresolved mandatory requirement leaves the group pending assistance or verification. |
| INV-03 | An approved schedule fits the operative capacity of shared resources. | Sum planned demand across groups, modes that compete for the same capacity, and overlapping time windows. Check road throughput, pickup capacity, and shelter intake rates in compatible units. Record observed overload independently of the planned limit. |
| INV-04 | A scheduled phase allows the group to clear its hazard zone by the applicable deadline. | Consider departure time, expected queues and travel, pickup delays, and the approved planning margin. If the deadline cannot be met, mark the plan infeasible for that group and escalate. |
| INV-05 | Each group has at most one active destination assignment per evacuation leg. | Update the destination and transfer the reservation as one domain operation. If replacement cannot be completed, record a pending reassignment and expose whether the existing destination remains valid. |
| INV-06 | A new shelter assignment fits every applicable capacity limit. | For each required resource, check `occupied + active reservations + requested places <= usable capacity`. Apply the check atomically across concurrent assignments. If capacity drops below existing commitments, record the new capacity, block conflicting allocations, and flag affected groups. |
| INV-07 | A group consumes each shelter place only once. | A confirmed arrival converts the corresponding reserved places into occupied places. Duplicate arrival reports do not increment occupancy. A released reservation no longer consumes availability. |
| INV-08 | A transport resource has a feasible schedule and load. | Check seats, accessibility, pickup and unloading time, and travel between assignments. Multiple groups may share one trip if their combined needs fit the vehicle and its schedule. |
| INV-09 | Required care stays assigned throughout the journey. | Keep a person with their caregiver or record an authorized handover before publishing an instruction that separates them. |
| INV-10 | Route eligibility and capacity decisions retain their operational evidence. | Record the input source, observation time, and validity window. Treat stale or missing evidence as uncertain and preserve confirmed access restrictions until an authorized update or explicit expiry. |
| INV-11 | Only an authorized plan version can issue operational instructions. | Record the approving role, version, issue time, validity, and intended groups. A revision supersedes the affected instruction while retaining delivery and execution history. |
| INV-12 | Completion requires an explicit confirmation. | Track issuance, delivery, acknowledgement, pickup, and arrival independently. A missing location update or a device appearing near a shelter is insufficient to mark the group received. |
| INV-13 | Simulations cannot mutate live operations. | Run scenarios on a stated snapshot with separate state. Any adopted proposal passes current feasibility checks and plan approval before it can affect live routes, capacity, or communications. |
| INV-14 | Public instructions expose only information necessary for civilian evacuation. | Show the practical effect of an authorized access restriction and the relevant travel guidance. Limit access to individual locations and assistance needs to authorized roles that need them. |

## Key distinctions

- **Hard constraints** include confirmed access restrictions, essential accessibility needs, approved zone clearance deadlines, and available transport or shelter capacity. An unmet hard constraint leaves a visible planning conflict.
- **Planning objectives** rank feasible options by exposure time, congestion, accessibility of pickup, and travel time. Faster journeys cannot justify a constraint violation.
- **Observed conditions** describe what field teams or feeds report. **Forecasts** describe what may happen. **Instructions** express approved decisions and carry a version and validity window.

See how these rules apply in the [business processes](business-processes.md).
