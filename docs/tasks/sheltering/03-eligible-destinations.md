# SH-T03 Eligible destinations and the first leg

- **Owner:** __________
- **Directories:** `src/Routing/`, `contracts/http/routing/`, `data/demo/sheltering/`
- **Technologies:** C#, existing graph and route proposal validation, recorded provider evidence where needed
- **Baseline:** [02 road network and closures](../02-road-network-flypix-closures.md), [03 routing algorithm](../03-route-distribution-algorithm.md) and [04 route providers](../04-dispatch-openrouteservice.md).

## What the demo shows

An eligible first leg leads out of the hazard although both shelters have no available places. When a shelter becomes available, the system checks a separate leg from the latest usable location.

## Scope

### Stage A (demo)

- [x] Add candidate access to a named assembly point or shelter without changing the behavior of existing exit-only proposals. Return the expected arrival window and the route evidence revision for SH-W3.
- [x] Use the same checks for segment access, travel mode, required accessibility and clearance deadlines across candidate types. Return explicit reasons when evidence or eligibility is missing.
- [x] Exercise a direct route with a synthetic confirmed shelter booking, a safe temporary destination when no reservation is available, and a visible escalation when no eligible first leg exists.

### Stage B (completion)

- [x] Add contract and regression coverage for a recorded, graph-matched provider candidate. Expose the current-evidence recheck for SH-T06 to call before publication.
- [x] Check competing departure and arrival windows against the exercise transport evidence and measure candidate latency.
- [x] Read current reservations from Sheltering and recheck the booked route at SH-T04 reservation and SH-T06 publication. Use a labeled synthetic transport window in the offline exercise, where Dispatch has no assignment for group travel.

## Dependencies and verification

Receive SH-W1 for sites and SH-W2 for party constraints. Deliver SH-W3 with an eligible sample route and a blocked or unknown case to SH-T04 and SH-T06. Keep a provider fixture runnable without a key. Verify SH-A01 and the route eligibility conditions in SH-A02 and SH-A06.

## Results

The [SH-W3 contract and replay](../../../contracts/http/routing/README.md) cover:

- A direct candidate for `case-14` with a synthetic reservation at `shelter-a`, followed by routing to `assembly-north` when both shelters are full. The later `leg-shelter` uses the latest reported position at `exit-north` and the outside connector.
- A closed bridge invalidates the recorded east route. The north exit remains available until its approach is also closed, at which point the decision is `EscalationRequired`.
- A Grouping party preserves required care and uses a confirmed synthetic transport window. Routing rejects a departure beyond the confirmed window.
- The first-leg decision reads the current individual hold from Sheltering. Releasing that hold restores the safe first-leg fallback. Routing rejects publication for an unverified hold, a stale assignment version or route inputs that differ from the booking. Assisted publication requires a matching transport window from the exercise fixture.

Run `python tests/destination_routing_integration.py` through the local Gateway. It prints median end-to-end request latency for ten graph and ten recorded candidates. Run `python tests/sheltering_reservations_integration.py` and `python tests/sheltering_publication_integration.py` for booking and publication. Fixtures model graph-matched paths and transport windows on the `sample` graph. The replay requires no ORS key. Live group transport allocation remains outside the offline exercise.
