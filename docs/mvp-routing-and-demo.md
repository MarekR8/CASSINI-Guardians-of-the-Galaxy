# MVP routing rules, demo data and acceptance

This specification defines the common inputs and expected outcomes for the reference model, the Routing service and the browser demo. It complements the [implementation guide](mvp-implementation-guide.md), [state design](mvp-storage-and-state.md) and [interface specification](mvp-interfaces.md).

## Inputs to Routing

| Input | Source | Required contents |
| --- | --- | --- |
| Route subject | Management or Dispatch | `incidentId` and either `civilian-case` with its `caseId`, or `response-assignment` with its proposed or existing `assignmentId`. |
| Start and arrival profile | Case or unit position, task and mobility requirements | Observed origin with time and accuracy when available, travel mode, essential accessibility needs, and pickup and departure windows for the task. |
| Routing snapshot | Situation Data | `snapshotId`, routing revision, candidate exits, effective restrictions, affected and impassable geometry where known, coverage, observation age and source revisions. |
| Provider candidate | Recorded fixture or hosted OpenRouteService | Geometry, duration, distance, profile and candidate destination. Map the geometry to the scenario network or retain the geometry needed for authoritative restriction checks. |
| Departure time | Workflow and injected clock | Intended departure and estimated traversal windows. Keep the recorded observation and expected passage times distinct. |

Implement an `IRouteProvider` that takes start, destination, profile and avoidance geometry and returns a typed candidate or provider failure. `RecordedRouteProvider` uses fixtures keyed by normalized request and active restriction set. `OpenRouteServiceProvider` calls the Directions GeoJSON endpoint and translates its response into the same shape. A missing replay entry returns an explicit fixture error. Keep the provider choice fixed for each run so a live failure cannot substitute a response for unrelated inputs.

## Candidate evaluation and publication

1. Resolve the authorized subject and latest eligible start or pickup. If the position is stale, imprecise for a required check, or unavailable, request a confirmed start or return `PositionUnverified`.
2. Load `GetRoutingSnapshot`. For a civilian exit or assisted outbound leg, select candidate endpoints outside all applicable hazard zones at expected arrival. For an approach leg, use the task's pickup point and the response unit's profile.
3. Request a candidate per eligible endpoint. Pass avoid polygons for relevant confirmed restrictions, such as the closed crossing. Distinguish the affected area from impassable segments. When people start inside the affected area, apply verified restrictions and egress rules to the segments they can actually use.
4. Build expected traversal windows from provider step times when available. An estimate or geometry-only match must carry its limits. A confirmed closure conflicts when its time interval overlaps a traversal of the affected segment. When matching is uncertain, use the route geometry and authorized restriction geometry to check relevance. Keep the proposal pending if a mandatory check cannot resolve the uncertainty.
5. Call `ValidateRoute`. Reject a candidate with a confirmed access conflict, unsuitable travel mode, unverified essential accessibility, or ineligible exit. Rank remaining candidates by estimated time plus any explicitly supplied nonnegative congestion delay. Use the shortest estimated duration as the first demo rule and a stable exit ID as a tie-break. A missing traffic estimate remains unknown in the UI. The Python reference model can compute expected selections from the same fixture graph.
6. Return a short-lived proposal, including the original snapshot and reason codes for rejected candidates. Use an initial demo proposal TTL of 3 minutes and instruction TTL of 30 minutes, constrained by the authoritative evidence validity. Give both settings names in configuration. Use injected time to reproduce expiration in tests.
7. On `PublishRoute`, authorize the operator, check subject, proposal expiry and expected aggregate version, and run `ValidateRoute` with `useLatest: true`. Initial publication for a transport task validates approach and outbound legs. After pickup, validate the remaining outbound leg from the confirmed pickup or latest eligible unit position. Retain completed legs in history. Publish a new immutable version or return a reason such as `ProposalExpired`, `VersionConflict` or `RouteIneligible`.

An authoritative closed segment overrides the provider's returned candidate. Missing or expired evidence on a required check blocks publication. Return a `422` with `NoEligibleExit` or `RouteIneligible` when the check completes with no valid option. Return a `503` with `RoutingProviderUnavailable` or `SituationUnavailable` when the dependency cannot perform the check. The coordinator can see this difference and retry when the missing input becomes available.

For an existing route, use `route_segment_uses` and geometry footprints to select affected subjects from `SituationChanged.v1`. Revalidate their exact current versions. Persist withdrawals before proposing replacements. Approval of a replacement is a separate command. The recipient view reads current instructions after reconnecting and ignores earlier versions and recorded withdrawals. Report delivery separately from acknowledgement and physical progress.

## Deterministic demo dataset

Start with a synthetic graph and persons so CI can run independently of hosted services. Use the same fixture IDs throughout. For the optional sourced demo, choose one Copernicus area, confirm the real crossings and endpoints against that area's geometry, and store its own source references and provider responses. Keep the synthetic and sourced graph fixtures under separate version IDs.

| Fixture | Minimum content |
| --- | --- |
| `data/demo/manifest.json` | `fixtureVersion`, `incidentId: incident-42`, baseline clock `2026-11-27T19:20:00Z`, IDs of source layers and reference files. |
| `data/demo/hazards/flood.geojson` | One synthetic affected polygon for repeatable tests. Keep a separate sourced Copernicus extent with its product and observation time. |
| `data/demo/hazards/review.geojson` | One review feature with provenance and `reviewStatus: unverified`; test that it does not create a closure by itself. |
| `data/demo/network/segments.geojson` | Stable segment and node IDs, lines, direction, supported modes, baseline seconds per mode and accessibility evidence. |
| `data/demo/network/exits.geojson` | `exit-east` and `exit-north`; both lie outside the synthetic hazard polygon. |
| `data/demo/people-and-units.json` | `case-14` for the pedestrian, `case-15` for a resident requiring pickup, `unit-3` for accessible transport, and synthetic positions. |
| `data/demo/routing/` | Recorded responses and expected selections for baseline, closure, no alternate and accessibility rejection. Include the expected interval on `bridge-2`. |

Use the following schematic graph with coordinates in longitude and latitude order. It is a test graph with synthetic segment geometry and times. It can be rendered locally as GeoJSON. Set the affected polygon to the rectangle between `(16.6470, 50.4320)` and `(16.6570, 50.4380)`. The affected polygon represents the area to leave. Segment evidence represents where travel is permitted.

| Node | Coordinates | Purpose |
| --- | --- | --- |
| `walk-start` | `[16.6500, 50.4360]` | Initial pedestrian position |
| `pickup` | `[16.6520, 50.4350]` | Assisted resident pickup and later pedestrian position |
| `east-junction` | `[16.6560, 50.4350]` | Approach to the crossing from inside the zone |
| `exit-east` | `[16.6590, 50.4350]` | Initially preferred exit and unit start |
| `north-junction` | `[16.6530, 50.4385]` | Detour outside the affected area |
| `exit-north` | `[16.6540, 50.4410]` | Alternative exit |

Rows with `↔` support both directions. Use a separate segment flag to represent the accessibility rejection case.

| Segment ID | Connection | Walking seconds | Driving seconds |
| --- | --- | ---: | ---: |
| `walk-to-pickup` | `walk-start` → `pickup` | 300 | — |
| `east-link` | `pickup` ↔ `east-junction` | 360 | 150 |
| `bridge-2` | `east-junction` ↔ `exit-east` | 120 | 90 |
| `north-link` | `pickup` ↔ `north-junction` | 600 | 260 |
| `north-exit-link` | `north-junction` ↔ `exit-north` | 180 | 80 |
| `outside-connector` | `exit-east` ↔ `exit-north` | — | 300 |

At `19:20Z`, the pedestrian proposal to `exit-east` takes 780 seconds and uses `bridge-2` at `19:31Z`. The unit can approach from `exit-east` and arrive at `pickup` at `19:24Z`. Set pickup and outbound departure to `19:29Z`. The original outbound leg is expected to reach the crossing at `19:31:30Z`. Simulate closure effective at `19:30Z` for walking and driving. Both active instructions require review. Replay the pedestrian's latest accepted position at `pickup`. For the unit, retain the acknowledged pickup and propose a new outbound leg. Both replacement paths end at `exit-north`. Closing `north-link` as well produces the no-alternate case for both subjects at `pickup`.

## Acceptance matrix

| Check | Setup and action | Expected result |
| --- | --- | --- |
| `A01` Alert retry | Submit the same alert with the same command ID twice. | One alert and incident assignment. Both responses return the same IDs. |
| `A02` Snapshot pending | Submit the alert before Situation Data finishes the snapshot. | The alert is retained. A subsequent read returns the snapshot and its coverage and source times. |
| `A03` Distinct layer evidence | Import an affected polygon and an unverified review feature. | Two labeled layers appear. Only authorized operational restrictions affect route rejection. |
| `A04` Baseline and accessibility | Request the baseline pedestrian route and a case with an unverified essential accessibility requirement. | Baseline uses `bridge-2`; the accessibility case reports the unresolved requirement and issues no route. |
| `A05` Double booking | Submit two concurrent assignments for `unit-3` from different requests. | One active booking commits. The other returns `409 UnitUnavailable` and stays available for follow-up. |
| `A06` Pickup independence | Report arrival and pickup for the unit task. | Assignment reaches `PickupConfirmed`. The case remains open until an attributable exit confirmation. |
| `A07` Closure and detour | Apply the `19:30Z` closure while both routes still plan future bridge traversal. | The affected instructions are withdrawn. A reviewed, current proposal to `exit-north` can be approved. A route unaffected by the closure keeps its version. |
| `A08` No alternate | Close `north-link` as well, or make required evidence unavailable. | Routing shows no executable option or a dependency or evidence failure. The case remains visible and no route is issued. |
| `A09` Older event | Deliver a duplicate or delayed version-one event after version two is current. | Version two stays current. A redelivery has no duplicate effect. |
| `A10` Unauthorized read | Request another resident's route or an unassigned unit task. | The endpoint rejects access and returns no exact position or task details. |
| `A11` Publication timeout | Time out a unit route publication after booking and retry. | Dispatch reconciles the outcome by assignment and command ID. The unit is still booked, and only one route becomes current. |
| `A12` Expiration and reconnect | Advance the clock past validity or reconnect with an earlier instruction cached. | The client shows the latest current or unresolved state and never presents an expired or withdrawn instruction as active. |

Use xUnit for the policy, state and concurrency checks and a Playwright Chromium journey for the main flood demo. In the browser show the alert, pedestrian route, unit assignment, bridge closure, replacement approval and explicit exit confirmation. Store test output and a failed-run trace in CI artifacts.
