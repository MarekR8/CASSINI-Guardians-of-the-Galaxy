# MVP implementation work packages

The seven work packages cover the four [MVP workflows](business-processes.md) and follow the ownership boundaries in the [aggregate diagram](domain-architecture.md#aggregate-diagram). Use the same flood scenario, graph and IDs across services. The [command and event catalog](mvp-commands-events.md) shows how the services interact during the demo. Use the [implementation guide](mvp-implementation-guide.md) and [selected technology stack](mvp-technology-stack.md) for each package. Fill in the owner fields when the team assigns the work.

## 1. Receive alerts and open incidents

**Owner:** __________

**Aggregate:** `EvacuationIncident` in Evacuation Management.

**Proposed location:** `src/Management/Incidents` and the alert form in `apps/web`.

- [ ] Define `SubmitHazardAlert` with `clientAlertId`, source, observed time, hazard type and a point or polygon. Store the alert before requesting the situation view. Accept an authorized review or correction without rewriting the original report.
- [ ] Return `alertId`, `incidentId`, and either the situation snapshot or a pending status with a way to retrieve it. Repeating the same client alert returns the same IDs.
- [ ] Connect the form and incident view to `GetSituation`. Show infrastructure, observed and potential hazards, available population estimates, data timestamps, and known gaps.
- [ ] Provide the shared demo entry point and reset steps so all packages can start from the same incident and fixture version.

**Handoff:** `incidentId` and `snapshotId` to the coordinator view, Routing and Dispatch.

**Acceptance:** Submitting the demo flood alert opens one incident and displays its initial situation. With Situation Data temporarily unavailable, the alert is still recorded and the client can retrieve the situation later.

## 2. Build the map and track evacuation progress

**Owner:** __________

**Aggregate:** `EvacuationCase` in Evacuation Management.

**Proposed location:** `src/Management/Cases` and `apps/web`.

- [ ] Add position and progress inputs with `caseId`, source, `observedAt`, and, for a position, coordinates and horizontal accuracy when available. Record whether a supported device verified Galileo participation. Record EGNOS use only when a compatible receiver supplies that evidence.
- [ ] Read coordinates from a compatible phone or replay a labeled GNSS trace. On Android, use `GnssStatus` to check Galileo participation in a fix where the device exposes it. If EGNOS equipment is unavailable, run the position path with Galileo or the labeled trace.
- [ ] Build the coordinator map with React and Leaflet. Draw the incident area, hazard and review layers, roads, closures, current and withdrawn routes, exit points, and authorized case and unit status. Include a legend, last update time and selected layer provenance.
- [ ] Provide a resident view with their current route or an instruction-pending state. Give dispatch operators access to pickup and unit details through their view. Use synthetic resident and unit positions in the shared demo.
- [ ] Handle `RoutePublished.v1`, `RouteWithdrawn.v1` and `AssignmentChanged.v1` in the monitor. Show delivery, acknowledgement, pickup and confirmed exit as separate facts. Require an explicit exit confirmation before closing a case.

**Handoff:** A map that combines Situation Data, Routing, Management and Dispatch outputs.

**Acceptance:** A new position moves a marker without completing the case. A withdrawn route loses its current styling, and a delayed older route event cannot bring it back.

Positioning references: [Galileo Open Service](https://www.euspa.europa.eu/galileo-os), [Android GnssStatus](https://developer.android.com/reference/android/location/GnssStatus), [EGNOS services](https://www.euspa.europa.eu/eu-space-programme/egnos/services).

## 3. Prepare Copernicus and FlyPix AI hazard layers

**Owner:** __________

**Aggregate:** `HazardAssessment` in Situation Data.

**Proposed location:** `src/Situation/Hazards` and `data/demo/hazards`.

- [ ] Select a single Copernicus EMS flood delineation product for the demo area. Use an area of interest from `EMSR756` if it supports the route scenario. Convert or extract the required extent as GeoJSON with longitude and latitude coordinates.
- [ ] Record the activation or product reference, area of interest, observation time, and evidence status. Separate the historical observation time from the incident time used in the replay.
- [ ] Open suitable imagery in FlyPix AI. Produce an example review layer with an available model or annotations and export it as GeoJSON. Label the method used and retain the imagery date and any confidence supplied. Confirm that the two data sources have compatible geographic coverage before combining them on the map.
- [ ] Import the two layers as separate evidence. Use the flood extent for hazard assessment. Show FlyPix findings as items to review until an authorized operator verifies their operational effect. Send any verified road closure through the network restriction workflow.

**Handoff:** Versioned GeoJSON hazard and review layers through `GetSituation`, plus relevant extents through `GetRoutingSnapshot`.

**Acceptance:** The map shows the Copernicus extent and the FlyPix review layer with distinct provenance and styles. An unverified FlyPix feature leaves route eligibility unchanged.

Data references: [Copernicus EMS Poland flood activation](https://mapping.emergency.copernicus.eu/news/information-bulletin-173-the-copernicus-emergency-management-service-monitors-floods-in-central-and-eastern-europe/), [FlyPix AI model workflow](https://docs.flypix.ai/docs/guides/ai/apply-ai-model-workflow), [FlyPix AI GeoJSON export](https://docs.flypix.ai/docs/guides/annotation/edit-and-manage-vectors).

## 4. Prepare the road network and closure updates

**Owner:** __________

**Aggregate:** `NetworkSegment` in Situation Data.

**Proposed location:** `src/Situation/Network` and `data/demo/network`.

- [ ] Select a bridge and a realistic alternate corridor on the same base map. Give demo segments stable IDs and record their geometries, directions, permitted modes and known accessibility attributes. Label simulated traffic estimates and field observations separately.
- [ ] Add a demo action that records a confirmed bridge closure with its source, effective time, affected modes and segment IDs. Preserve a confirmed closure until it expires or an authorized update lifts it.
- [ ] Expose the closure as a map layer and supply its geometry to Routing. Select a focused avoidance polygon that covers the closed crossing while leaving the other corridor available. Keep any other restrictions as separate evidence.
- [ ] Publish `SituationChanged.v1` after the restriction is stored. Include the changed IDs or geometry reference, evidence version and effective window. Expose refreshed evidence through `GetRoutingSnapshot` and `ValidateRoute`.

**Handoff:** Road and closure GeoJSON to the map, versioned access constraints to Routing, and before and after snapshots to the routing model package.

**Acceptance:** The bridge closure appears on the map, produces a committed change event, and causes routes crossing it during the restriction to fail validation. An unaffected route stays eligible.

## 5. Define and test routing rules

**Owner:** __________

**Aggregate:** Rules for `RouteInstruction` in Evacuation Routing.

**Proposed location:** `experiments/routing` and `data/demo/routing`.

- [ ] Define the inputs that the mathematical model needs. Include the candidate route geometry, travel mode, essential access needs, traversal times or time estimates, effective closures, hazard extent, and candidate points outside the zone.
- [ ] Write the eligibility rules for pedestrian, civilian vehicle, and response unit legs. Describe which restrictions are mandatory, how to represent missing evidence, and how to handle an origin or pickup inside the affected area. Identify excluded candidates before ranking the rest by estimated travel time.
- [ ] Define the output for success and failure. For a feasible route, return the selected candidate and its score or estimated duration. For a rejected route, return a reason such as `ClosedSegment`, `AccessibilityUnverified` or `NoEligibleExit`.
- [ ] Produce reusable test fixtures or a small reference notebook for three cases. Check the selected exit and expected behavior before closure, after closure with an alternate corridor, and when all eligible exits are unavailable. Add a case with an inaccessible pedestrian path or an assistance unit approaching a pickup.

**Handoff:** Written rules, shared inputs and expected outputs to the implementation of `RouteInstruction`.

**Acceptance:** The expected outcomes can be explained from the fixture data and are usable by the C# implementation without depending on a live OpenRouteService response during every test.

## 6. Handle requests for assistance and assign units

**Owner:** __________

**Aggregate:** `AssistanceRequest` in Assistance Dispatch.

**Proposed location:** `src/Dispatch/AssistanceRequests` and the dispatch panel in `apps/web`.

- [ ] Accept a request with `caseId`, pickup location, needed capabilities, and whether the task includes outbound transport. Store the request and make it visible to the dispatcher.
- [ ] Query eligible units and request a route proposal for an assignment. For transport tasks, require a valid approach leg and a valid outbound leg. Keep a visible reason when a unit or route is unavailable.
- [ ] Commit the active assignment and unit booking in Dispatch under the same concurrency rules. Enforce one active assignment per request and one per unit. Use the same command ID when retrying an uncertain outcome.
- [ ] Record acknowledgement, arrival at pickup, pickup and completion as distinct steps. Publish `AssignmentChanged.v1` with the request, assignment, unit, state and report time. Keep a booked task visible for review if its route needs replacement.

**Handoff:** Assignment reference to Routing, task details to the authorized unit, and progress events to Management.

**Acceptance:** An eligible request gets a unit and a route. A competing request cannot reserve the same active unit. Confirming pickup changes the task state and leaves the case open until exit is confirmed separately.

## 7. Expose response units and integrate OpenRouteService

**Owner:** __________

**Aggregates:** `ResponseUnit` in Assistance Dispatch and implementation of `RouteInstruction` in Evacuation Routing.

**Proposed location:** `src/Dispatch/Units` and `src/Routing`.

- [ ] Seed one available unit with a starting location, travel mode, and capabilities. Expose the unit's readiness and position to authorized dispatch operators. Use the booking state from package 6 when determining availability.
- [ ] Implement the server-side OpenRouteService Directions adapter. Pass the start, candidate destination, profile and applicable avoidance polygons. Return the route geometry and estimated travel time. Keep the provider key in local configuration.
- [ ] Implement package 5's rules in Routing. Validate the provider's geometry and destination against current Situation Data before publication. Test the fixture with a civilian origin inside the hazard area so an overly broad avoidance polygon cannot make the demonstrated exit unusable.
- [ ] Publish a route only after authorization, with its subject, version, snapshot reference and validity. Revalidate affected routes on `SituationChanged.v1`. Withdraw the invalid version and propose a replacement. For provider failure or no eligible route, keep the subject pending with a reason.
- [ ] Store a recorded provider response for repeatable tests, keyed by request inputs and restrictions. Validate replayed results against the same rules. Use a distinct fixture for the closure scenario.

**Handoff:** Current civilian and unit route instructions, versioned geometry, and `RoutePublished.v1` or `RouteWithdrawn.v1` events.

**Acceptance:** The unit has an approach route and, when required, an outbound route. Closing the bridge withdraws any affected civilian or unit instruction. A replacement becomes current after validation and approval.

Routing reference: [OpenRouteService Directions options](https://giscience.github.io/openrouteservice/api-reference/endpoints/directions/routing-options).

## Map ownership and data handoffs

Situation Data owns the terrain layers and exposes their current state in `SituationSnapshot`. Package 2 composes the map in the web app. Each operational layer stays with its authoritative service.

| Layer or input | Producer | Minimum data | Consumer |
| --- | --- | --- | --- |
| Incident area | Package 1, Management | `incidentId`, reported geometry, source, review status | Coordinator map and situation query |
| Hazard and review layers | Package 3, Situation Data | Feature ID, geometry, evidence kind, source, observation time and revision | Coordinator map, routing validation for verified operational constraints |
| Roads and restrictions | Package 4, Situation Data | Stable `segmentId`, geometry, modes, effective restrictions, evidence version | Coordinator map and Routing |
| Position and progress | Package 2, Management | `caseId`, last known position, observed time, accuracy if known, confirmed progress | Coordinator map and authorized workflows |
| Pickup and unit status | Packages 6 and 7, Dispatch | Request and assignment references, pickup point, unit readiness and assignment state | Dispatch view, coordinator monitor and Routing |
| Civilian and unit routes | Package 7, Routing | Subject, version, validity, route legs as geometry and route state | Coordinator, resident and unit views as applicable |

Use one map extent containing the hazard area, the approach to the bridge, the alternate corridor, the pickup, and both exit points. Use GeoJSON longitude then latitude and UTC timestamps. Define shared example requests, responses and events under the proposed `contracts/` directory. Use stable fixture IDs under `data/demo/` so the frontend and services can develop against the same examples.

## Communication in the demo and later deployments

Package 1 can demonstrate interruption and recovery using the report and routing contracts. Queue a synthetic report while the network is unavailable, replay it with the same command ID after reconnection, and show one resulting record. Keep earlier route versions superseded when a client reconnects.

GOVSATCOM can provide connectivity for authorized public authorities. IRIS² is a future connectivity option for equipped sites. Represent both on the deployment roadmap as potential links between a coordination point and the backend. Run the hackathon scenario over ordinary connectivity with a labeled interruption simulation. EU SST operates services for space-object collision, fragmentation and re-entry analysis. Its data are outside this flood demo. [GOVSATCOM Hub](https://www.euspa.europa.eu/govsatcom-hub), [ESA IRIS² programme](https://resilience.esa.int/esa-programme-related-to-eu-secure-connectivity-and-iris%C2%B2), [EU SST services](https://www.eusst.eu/services).

## Integration order and demo acceptance

1. **Agree on fixtures and contracts.** Select the exact area, crossing, two exit points and one pickup. Commit example payloads for alert acknowledgement, situation snapshot, route proposal, assignment, and bridge closure. Keep one baseline and one closure fixture.
2. **Connect alert, situation and map.** Packages 1–4 load the fixture and show the alert area, two distinct hazard and review layers, the roads and synthetic population or case data. The routing packages can work against the same snapshots in parallel.
3. **Add routes and dispatch.** Verify a pedestrian route and an assistance assignment with the required legs. Confirm that a second request cannot book the same unit and that pickup and exit appear as separate facts.
4. **Run the closure scenario.** The bridge closes, the affected instruction is withdrawn, and the affected civilian or unit gets a replacement proposal or a visible unresolved state. After approval the new version appears. A replay of the previous version leaves the new instruction in place.
