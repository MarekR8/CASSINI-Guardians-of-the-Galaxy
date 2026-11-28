# SH-T03 Eligible destinations and the first leg

- **Owner:** __________
- **Directories:** `src/Routing/`, `contracts/http/routing/`, `data/demo/sheltering/`
- **Technologies:** C#, existing graph and route proposal validation, recorded provider evidence where needed
- **Baseline:** [02 road network and closures](../02-road-network-flypix-closures.md), [03 routing algorithm](../03-route-distribution-algorithm.md) and [04 route providers](../04-dispatch-openrouteservice.md).

## What the demo shows

An eligible first leg leads out of the hazard although both shelters have no available places. When a shelter becomes available, the system checks a separate leg from the latest usable location.

## Scope

### Stage A (demo)

- [ ] Add candidate access to a named assembly point or shelter without changing the behavior of existing exit-only proposals. Return the expected arrival window and the route evidence revision for SH-W3.
- [ ] Use the same checks for segment access, travel mode, required accessibility and clearance deadlines across candidate types. Return explicit reasons when evidence or eligibility is missing.
- [ ] Exercise a direct route with a confirmed shelter booking, a safe temporary destination when no reservation is available, and a visible escalation when no eligible first leg exists.

### Stage B (completion)

- [ ] Add contract and regression coverage for archived or recorded provider candidates. Recheck current evidence before publication even if earlier evidence was recorded.
- [ ] Check competing departure and arrival windows against current transport evidence and measure candidate latency for the exercise.

## Dependencies and verification

Receive SH-W1 for sites and SH-W2 for party constraints. Deliver SH-W3 with an eligible sample route and a blocked or unknown case to SH-T04 and SH-T06. Keep a provider fixture runnable without a key. Verify SH-A01 and the route eligibility conditions in SH-A02 and SH-A06.

## Results

_(Fill in the scenario, route evidence, measured results and any provider limits.)_
