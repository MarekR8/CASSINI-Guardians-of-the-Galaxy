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

The [domain invariants](domain-invariants.md) define the conditions these workflows enforce.
