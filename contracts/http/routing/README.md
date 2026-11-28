# SH-W3 · Eligible destination routes and the first leg

Routing exposes these read-only queries under `/api/routing` through the Gateway in the opt-in `sample` Sheltering exercise. It reads the current Situation revision, site-owner evidence (SH-W1), and the current Management cases or matching Grouping revision (SH-W2) when a case or party is supplied.

| Method and path | Result |
| --- | --- |
| `GET /queries/destination-route` | Assess a typed destination with `destinationKind=exit\|assemblyPoint\|shelter` and `destinationId`. Returns `Eligible` with route evidence or `Ineligible` with a reason. |
| `GET /queries/first-leg` | Evaluate a direct shelter option with a verified exercise `reservationId`. Otherwise evaluate safe exits and assembly points independently of the shelter catalog. Returns `DirectShelter`, `ShelterPending`, `AlreadyOutsideHazard`, or `EscalationRequired`, plus relevant reasons. |

Both queries take `incidentId`, `legId`, `mode=walking\|driving`, optional `at`, and a `clearBy` time when starting inside the hazard. They accept a graph `node`, or `caseIds` to resolve the latest Management position. A party from Grouping supplies `groupId`, `groupRevision`, and its comma-separated `caseIds`. Routing checks that the party shares a usable origin, includes both sides of required-care links, and uses compatible mobility. Set `stepFree=true` when needed. Omitting `at` uses the current exercise clock. The sample uses `leg-exit` before hazard clearance and `leg-shelter` for a later journey.

An eligible result carries `departsAt`, `clearsHazardAt`, `arrivalWindow.earliest` and `arrivalWindow.latestExclusive`, path nodes and segments, and an `evidence` object with the evidence ID, source, mode, observation and validity, Situation revision, and relevant catalog revision and site version. The deterministic exercise uses a one-second arrival interval. Stage A checks all selected segments against active closures, allowed mode, confirmed step-free access when requested, and the clearance deadline. The exercise graph treats the verified exits as its known outside-hazard nodes. The second leg never re-enters the hazard. Site opening and evidence must cover arrival. A route can be eligible while a separate `availabilityReason` reports full, overcommitted or unknown shelter resources. SH-T04 checks party demand and intake and owns the reservation.

Use `transportId` to verify a matching exercise transport window and accessibility. Route assessment can start without a transport reference and returns `travel.requiresAssistance` for an assisted party. The first-leg decision and the before-publication recheck require transport evidence for a party with assisted cases. A confirmed transport window can delay departure. Both departure and arrival must fit the window and the hazard clearance deadline still applies.

`recordedRouteId` replays a locally matched provider path. Recorded paths pass the same current graph checks as generated paths. Replay uses the graph's current timings. [The HTTP example](destination-route.example.json) includes the successful result and what changes after the bridge closure. For the SH-T06 publication workflow, repeat the named route query with the same resolved inputs and `expectedEvidenceId` from the original response. Routing recomputes against the current owners and returns an ineligible result if the route, evidence or input versions have changed. A confirmed reservation must be rechecked with its owner at publication.

`400` indicates invalid input or a request time before the exercise clock. `409 TravelSubjectChanged` requires a refreshed subject revision. `422` includes unresolved case needs, origins or required care. `503` signals unavailable evidence or inconsistent owner revisions. A named candidate can also return `200 Ineligible` with a business reason such as `StaleSituationEvidence`, `RouteAccessibilityUnverified`, `ClearanceDeadlineMissed` or `RecordedRouteIneligible`. When safe first-leg routing is unresolved, the response includes `NoEligibleFirstLeg` and no route.

## Replay the sample

Run the six services and exercise both endpoints using local data:

```powershell
python tests/destination_routing_integration.py
```

For manual calls, load the synthetic reservation, transport window and recorded route alongside the existing sample site data:

```powershell
$env:Routing__DestinationEvidenceFile = (Resolve-Path data/demo/sheltering/sample-destination-evidence.json).Path
$env:Grouping__EvidenceFile = (Resolve-Path data/demo/sheltering/sample-grouping.json).Path
python scripts/offline_demo.py --sheltering --port 5580 --data artifacts/destination-demo
```

For example, call `GET /api/routing/queries/first-leg?incidentId=incident-42&legId=leg-exit&caseIds=case-14&mode=walking&clearBy=2026-11-27T19:58:00Z&reservationId=reservation-demo-east`. Fixture-backed decisions return `fixtureMode=synthetic`. The test covers both shelters full and a later trip from the latest Management position. Production reservation, transport and publication integration belongs to the owning workflows. This sample requires no ORS key.
