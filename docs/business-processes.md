# Evacuation business processes

The MVP covers four workflows. A client can submit an alert through the application or API. Crisis coordinators authorize civilian instructions, and dispatchers assign field units.

## 1. Get the situation

**Trigger.** The client reports a hazard with a location or an affected area. The report includes its source and time when available.

1. The system acknowledges the alert and links it to an incident or opens a new incident for review.
2. Situation Data gathers the infrastructure relevant to the affected area, such as roads, pedestrian crossings and access points. It overlays reported and observed hazards and marks potential hazards as such.
3. It returns available population estimates and the coverage and freshness of individual observations where authorized. Unknown coverage and data gaps stay visible.
4. The client receives an alert ID and a situation snapshot or a pending response with a status reference. A coordinator can correct or confirm the affected area as evidence changes.

**Outcome.** A versioned view shows the relevant infrastructure, hazards and available information about people. Unverified alerts remain identifiable alongside confirmed facts.

## 2. Route people out of danger

**Trigger.** A resident or operator requests routing for a person associated with an incident.

1. Evacuation Routing determines the person's start point, travel mode and functional access needs.
2. It uses the current situation snapshot to find an eligible safe point outside the danger zone and a route through permitted segments. Current congestion and observed or estimated throughput inform the choice among eligible routes.
3. The coordinator authorizes the instruction. The system records the route version and validity window and delivers it through an available channel.
4. If the system cannot find a viable route, the case remains open with a reason. The coordinator can verify access or start an assistance request.

**Outcome.** The person has a current instruction to exit the affected area, or a visible request for a route or assistance. The destination is a safe point used for routing.

## 3. Dispatch and route assistance units

**Trigger.** A resident, caregiver or operator requests help, or a coordinator identifies a person unable to leave independently.

1. The dispatcher records the pickup location, required capabilities and the state of the report.
2. The system finds a suitable, available field unit and proposes an eligible approach route for that unit. When the task includes transport, it also checks the route out against the needs of the unit and passenger.
3. The dispatcher assigns the unit. The unit receives the task and acknowledges it, then reports arrival, pickup and task completion as applicable.
4. If no eligible unit or route is available, the request stays open with the reason visible. The dispatcher can review alternatives when new information arrives.

**Outcome.** The person has an accountable assistance assignment or a visible unresolved need. Unit acknowledgement and pickup are recorded separately.

## 4. Monitor and revise the evacuation

**Trigger.** The system receives a new position report, a changed hazard or road restriction, a unit status update, or a confirmation of exit.

1. The system updates the situation view and links affected route versions to their residents and assigned units.
2. It withdraws invalid instructions known to the system, updates the visible state, and computes alternatives from the latest available position or asks an operator to confirm the start point.
3. An authorized coordinator or dispatcher approves a replacement. Delivery, acknowledgement and execution are tracked separately across available channels.
4. A field report or a verified, attributable self-report confirms exit. Cases with missing contact or uncertain progress stay visible for follow-up.

**Outcome.** The coordinator can see current instructions, confirmed exits, and cases needing action. Late messages retain their original time and version.

## Outside the MVP

- **Sheltering.** Find, allocate and manage shelter places.
- **Relationship-based grouping.** Keep or route people together based on household, family, caregiving or other relationships.

See the [invariants](domain-invariants.md) and [architecture](domain-architecture.md).
