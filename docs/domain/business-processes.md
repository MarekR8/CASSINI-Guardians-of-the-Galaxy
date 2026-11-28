# Evacuation business processes

These workflows describe the proposed [evacuation coordination application](../pitch/project-one-pager.md). The crisis coordinator owns the action plan. Field teams validate conditions, transport operators carry out pickups, and shelter operators report usable capacity and confirm arrivals.

## 1. Open an incident and verify the situation

**Trigger.** A crisis coordinator receives an alert or a field report about an incident that may require evacuation.

1. The coordinator records the affected area, priority zones, and the decision time frame.
2. The system combines timestamped hazard layers, the road and pedestrian network, traffic observations, and available transport and shelters.
3. Field teams confirm key crossings, closures, and pickup points. Authorized control notices enter the system as road access restrictions with an effective time window.
4. The coordinator confirms the planning inputs and requests checks on unresolved conditions that could affect the plan.

**Outcome.** A versioned snapshot of the situation records the source, observation time, and validity of each operational input. A crossing with unresolved access remains marked as unknown.

## 2. Plan and activate phased evacuation

**Trigger.** The coordinator starts planning from the verified incident snapshot.

1. The coordinator defines evacuation zones, estimated group sizes, and the latest acceptable time for each zone to clear.
2. The system proposes departure windows, travel modes, routes, and destinations. It checks time-dependent hazards and the shared capacity of roads, crossings, pickups, and reception points.
3. The coordinator reviews unmet needs and groups for which no feasible path exists, then approves a plan version for release.
4. Operators distribute each group's departure instructions through the app and designated field or public communication channels. The coordinator releases later phases as conditions allow.

**Outcome.** Each released group receives a time, route or pickup instruction, and destination. If a group cannot clear its zone by the required time, the system escalates the conflict for a coordinator decision.

## 3. Arrange accessible evacuation and assisted pickup

**Trigger.** A resident, caregiver, or field operator reports a need, or the coordinator identifies a group without a feasible way to leave.

1. An operator records group size and functional needs, such as step-free access, an accessible vehicle, a shorter walking distance, or caregiver accompaniment.
2. The system proposes an eligible walking route or an accessible pickup point, vehicle, and shelter. Field staff can register people who have no phone or connection.
3. The transport operator accepts the task with a pickup window and confirms boarding. The receiving point confirms arrival.

**Outcome.** Each request has a viable plan and an assigned operator, or stays open with a reason and a responsible owner. Where accessibility data for a critical route feature are missing, operators verify it or arrange another option.

## 4. Allocate and reassign shelter places

**Trigger.** A group needs a destination, or a shelter's capacity or operating status changes.

1. The shelter operator reports available places, supported needs, opening times, and the rate at which arrivals can be admitted.
2. The system checks that the group can reach the shelter in time, that the shelter can meet its needs, and that places are available. It reserves both general places and any limited accessible resources the group needs.
3. The receiving operator confirms arrivals. The system converts the corresponding reservations into occupied places.
4. If a shelter closes or loses capacity, the coordinator reviews affected reservations and groups already en route. The system proposes alternatives, replaces assignments when approved, and tracks whether updated instructions reach each group.

**Outcome.** Each group has a current destination or a visible pending reassignment. A capacity reduction below actual occupancy blocks new assignments and raises a coordination alert.

See [Detailed sheltering and grouping workflows](#detailed-sheltering-and-grouping-workflows) for the handoffs between coordinators, shelter operators and Routing.

## 5. Monitor flows and revise the active plan

**Trigger.** A closure, hazard update, crowding report, traffic change, or missed pickup puts part of the active plan in doubt.

1. The system records the observation and identifies the affected routes, phases, and groups, including those already moving.
2. Confirmed restrictions invalidate affected recommendations. The coordinator reviews options such as rerouting, holding a later phase when safe, dispatching additional transport, or reassigning destinations.
3. The coordinator authorizes a revised version. The system distributes the updated instructions and tracks delivery, acknowledgement, and execution as distinct states.
4. Operators follow up through field or alternate communication channels for groups whose instructions could not be confirmed.

**Outcome.** The coordinator sees the latest plan, groups affected by the change, and outstanding contacts or tasks. A device that goes offline continues to show the timestamp and validity of the last received instruction.

## 6. Compare scenarios for the next few hours

**Trigger.** A coordinator wants to test a decision or anticipate a potential disruption.

1. The coordinator chooses an incident snapshot, a time horizon, and assumptions such as forecast hazard growth, a closed crossing, reduced road capacity, or an extra accessible bus.
2. The system simulates departures, queues, corridor use, arrivals, shelter demand, and unmet assistance needs over the selected horizon.
3. The coordinator compares outcomes for each zone and group under the same baseline, with assumptions and uncertainty visible.
4. If a scenario is selected, the coordinator rechecks it against current conditions and approves it through the plan publication workflow.

**Outcome.** The team can compare decisions before taking action. A simulation can report infeasible demand without changing the active operation or consuming live capacity.

## Detailed sheltering and grouping workflows

A **verified exit point** is outside the applicable hazard and can be reached through eligible segments. An **assembly point** is an authorized temporary stop for check-in, assistance or transport. A **shelter** admits people and tracks beds, places and other limited resources. One physical site may serve more than one function, with separate capacities and operating hours.

The crisis coordinator authorizes destinations, redistribution and exceptions. A shelter operator updates usable resources and admits or releases people. Field operators verify conditions, group membership and handovers. Routing supplies eligible paths and estimated arrival times for the incident revision. The process first clears the hazard, then accepts any suitable shelter, and reassigns people when their current destination is no longer feasible or the coordinator authorizes a necessary transfer.

### SH-01 · Activate a point and declare its resources

**Trigger.** The coordinator opens an incident or needs an additional assembly point or shelter.

1. The coordinator identifies the site, its functions, verified entrances, available transport modes and operating window. The responsible operator confirms which services are actually available.
2. For a shelter, the operator declares usable capacity, current occupancy and resource limits, including any essential accessibility or support requirements the site can meet. When applicable, the operator also records the maximum number of arrivals per time window.
3. The coordinator checks current hazard coverage and access, then activates the site for the incident. Changes to resources, access and availability retain their source, observation time and validity.

**Outcome.** The site can be considered for its authorized functions. Unverified, closed or expired availability is visible to the coordinator. If capacity falls below current occupancy or promises already made, the system blocks further reservations and raises a conflict for review.

### SH-02 · Form a travelling group and choose when to split it

**Trigger.** People are registered or a report changes their location, relationships or ability to travel together.

1. A field operator records relationships for this incident, with the relevant context and confidence. Required care links a dependent person to a current caregiver or an authorized handover. Household or similar links express a preference for a common destination. An institution link applies to the people actually present under its supervision at the time of evacuation.
2. The system evaluates whether people can depart together without delaying anyone beyond the applicable clearance deadline. It considers eligible routes, extra journey or waiting time and accessibility. Membership in the same school or household does not by itself require a shared departure.
3. If meeting first is infeasible, the system proposes separate departures and still looks for a common shelter they can each reach safely. If that also fails, it assigns feasible subgroups or individuals separately and flags the preference for later reunification.
4. A required care link stays in force unless a responsible operator records a handover. If the available routes or resources cannot support that requirement, the system escalates for assistance.

**Outcome.** Each person has a travelling unit and, where applicable, a separate common-destination preference. The operator can see why a group was split and which care obligations remain assigned.

### SH-03 · Issue the first leg out of the hazard

**Trigger.** A person or group requires evacuation from an area affected by the incident.

1. Routing checks the current start, travel mode, access restrictions and latest acceptable clearance time. If an eligible route can take the group directly to a suitable shelter with a confirmed reservation, the coordinator can issue that destination.
2. If a shelter is full, unknown or still being assigned, the system proposes a route to a verified exit point or an authorized assembly point outside the hazard. The issued instruction identifies the next destination and records that shelter allocation is pending.
3. If the evidence does not support any feasible first leg, the coordinator receives a visible unresolved case and requests route verification or assisted evacuation.

**Outcome.** A feasible departure is available independently of lodging. Clearing the hazard and being admitted to a shelter are separate facts.

### SH-04 · Assign a shelter and reserve capacity

**Trigger.** A person or group needs a shelter, whether still departing or already at a safe point.

1. The system checks which shelters can be reached on eligible routes and can receive the party at its estimated arrival time. A candidate must cover general places and every required limited resource, with intake capacity checked where recorded.
2. Among feasible choices, prefer a common shelter for people who wish to reunite when this respects the clearance and access constraints. Then prefer a shorter feasible journey. If one shelter cannot accommodate the entire party, retry by feasible subgroup or individual while enforcing required care. Show the reason for any split or unassigned person.
3. Before publishing, recheck route and resource availability. Reserve the required quantities as one operation so two concurrent assignments cannot use the same remaining capacity. Record the expected arrival, reservation validity and owner responsible for follow-up.
4. Publish one current destination instruction per person for this leg. Track publication, delivery and acknowledgement separately.

**Outcome.** Every eligible recipient has a current assignment backed by a reservation, or remains explicitly unassigned at their latest verified stage. An unsuccessful reservation has no effect on occupied places.

### SH-05 · Confirm arrivals and reconcile occupancy

**Trigger.** Someone reaches the designated shelter, reports a departure or misses an expected arrival window.

1. The shelter operator confirms the people physically received. For a partial arrival, convert only those people's reserved places and resources into occupied quantities. Keep the remaining reservation visible until it is released or explicitly reconciled.
2. Deduplicate repeated arrival reports. An unannounced arrival goes through admission and capacity checks before it consumes a place. If no suitable capacity remains, record the unmet need and notify the coordinator.
3. When the expected arrival window is missed, flag the outstanding people for follow-up. Release a reservation only according to its agreed expiry or an authorized decision. When an admitted person leaves, confirm the departure before releasing occupied resources.

**Outcome.** The operator can account for people awaiting arrival, actually received or departed. A GPS report or a delivered instruction alone does not change occupancy.

### SH-06 · Reassign when a destination stops being feasible

**Trigger.** A shelter closes, loses a required resource, becomes unreachable, or the coordinator authorizes a necessary transfer.

1. Identify people still in the hazard, en route, waiting at a safe point and already admitted. Recompute from each person's latest usable location and check whether the original destination or approach is still valid.
2. Find an eligible alternative and verify its capacity. Replace the assignment and move the reservation in one operation. If renewal fails, state whether the original instruction can remain in force. Withdraw it promptly when its route or destination is no longer valid.
3. Publish the new instruction as a later version and flag people whose receipt is unconfirmed. If no replacement exists, assign a follow-up owner and use a safe assembly point when a feasible route to it is available.
4. For people already admitted, plan any necessary transfer as a separate leg. Keep their original places marked occupied until actual departure and confirm reception at the new site.

**Outcome.** Reassignment leaves one current destination for the leg, or a visible unresolved state. Allocation history shows which resources were held and when each instruction was superseded.

For the first exercise, use two shelters, one assembly point and synthetic evacuees. Verify a household that reaches one shelter by different routes, a split caused by an infeasible common destination, a care link that triggers assistance, two reservations competing for the last place, a partial arrival reported twice, reassignment after a closure, and a first leg that can proceed even when no suitable shelter has a free place.

The [domain invariants](domain-invariants.md) define the conditions these workflows enforce.
