# SH-W3 · Eligible destination routes and the first leg

Routing exposes these read-only queries under `/api/routing` through the Gateway in the opt-in `sample` Sheltering exercise. It reads the current Situation revision, site-owner evidence (SH-W1), and the current Management cases or matching Grouping revision (SH-W2) when a case or party is supplied.

| Method and path | Result |
| --- | --- |
| `GET /queries/destination-route` | Assess a typed destination with `destinationKind=exit\|assemblyPoint\|shelter` and `destinationId`. Returns `Eligible` with route evidence or `Ineligible` with a reason. |
| `GET /queries/first-leg` | Evaluate a direct shelter option for an individual `caseIds` value and its current Sheltering `reservationId`. Otherwise evaluate safe exits and assembly points independently of shelter capacity. Returns `DirectShelter`, `ShelterPending`, `AlreadyOutsideHazard`, or `EscalationRequired`, plus relevant reasons. |

Both queries take `incidentId`, `legId`, `mode=walking\|driving`, optional `at`, and a `clearBy` time when starting inside the hazard. They accept a graph `node`, or `caseIds` to resolve the latest Management position. A party from Grouping supplies `groupId`, `groupRevision`, and its comma-separated `caseIds`. Routing checks that the party shares a usable origin, includes both sides of required-care links, and uses compatible mobility. Set `stepFree=true` when needed. Omitting `at` uses the current exercise clock. The sample uses `leg-exit` before hazard clearance and `leg-shelter` for a later journey.

An eligible result carries `departsAt`, `clearsHazardAt`, `arrivalWindow.earliest` and `arrivalWindow.latestExclusive`, path nodes and segments, and an `evidence` object with the evidence ID, source, mode, observation and validity, Situation revision, and relevant catalog revision and site version. The deterministic exercise uses a one-second arrival interval. Stage A checks all selected segments against active closures, allowed mode, confirmed step-free access when requested, and the clearance deadline. The exercise graph treats the verified exits as its known outside-hazard nodes. The second leg never re-enters the hazard. Site opening and evidence must cover arrival. A route can be eligible while a separate `availabilityReason` reports full, overcommitted or unknown shelter resources. SH-T04 checks party demand and intake and owns the reservation.

Use `transportId` to verify a matching synthetic exercise transport window and accessibility. Route assessment can start without a transport reference and returns `travel.requiresAssistance` for an assisted party. The first-leg decision and the before-publication recheck require transport evidence for a party with assisted cases. The window is loaded from `Routing:DestinationEvidenceFile` and checked against the cases, origin and Situation revision. Both departure and arrival must fit the window and the hazard clearance deadline still applies.

`recordedRouteId` replays a locally matched provider path. Recorded paths pass the same current graph checks as generated paths. Replay uses the graph's current timings. [The HTTP example](destination-route.example.json) includes the successful result and what changes after the bridge closure. For the SH-T06 publication workflow, repeat the named route query with the same resolved inputs and `expectedEvidenceId` from the original response. Routing recomputes against the current owners and returns an ineligible result if the route, evidence or input versions have changed. A confirmed reservation must be rechecked with its owner at publication.

`400` indicates invalid input or a request time before the exercise clock. `409 TravelSubjectChanged` requires a refreshed subject revision. `422` includes unresolved case needs, origins or required care. `503` signals unavailable evidence or inconsistent owner revisions. A named candidate can also return `200 Ineligible` with a business reason such as `StaleSituationEvidence`, `RouteAccessibilityUnverified`, `ClearanceDeadlineMissed` or `RecordedRouteIneligible`. When safe first-leg routing is unresolved, the response includes `NoEligibleFirstLeg` and no route.

## Shelter instruction publication (SH-T06)

Routing owns the guidance issued for a shelter assignment. The subject is `kind=shelter-assignment` with `id=incidentId/caseId/legId`. The instruction aggregate has two versions:
- `aggregateVersion` is the concurrency token. A subject that was never published reads as version `0`.
- Each `current.routeVersion` is new and is never reused.

| Method and path | Behaviour |
| --- | --- |
| `GET /queries/route-instruction?kind&id` | The instruction: `aggregateVersion`, `current` (route version, evidence ID, destination, assignment reference, Situation revision, arrival window, validity) and `lastWithdrawal`. See the [example](route-instruction.example.json). |
| `POST /commands/publish-route` | Takes `commandId`, `correlationId`, `subject`, `expectedVersion`, `assignment` and `route`. Routing repeats `destination-route` with the proposed `expectedEvidenceId` and reads the current assignment from Sheltering. The version, reservation ID and shelter must match. The hold must cover the route inputs, resources and arrival window. The instruction expires by the earlier of route evidence and reservation expiry. A current route for another hold is superseded in the same commit. |
| `POST /commands/withdraw-route` | Takes `commandId`, `subject`, `expectedVersion`, the current `routeVersion`, `reasonCode` and an optional `evidenceRef`. |
| `GET /queries/route-outbox?after` | Committed `RoutePublished.v1` and `RouteWithdrawn.v1` events with their `published` flag. |

Every write stores a receipt. The same `commandId` returns the original instruction; changed inputs return `409 CommandConflict`. A failure commits nothing.

| Code | Status | Meaning |
| --- | --- | --- |
| `VersionConflict` | 409 | The expected aggregate version is stale. Read the instruction again. |
| `StaleRouteVersion` | 409 | The withdrawal names a route version that is no longer current. A late withdrawal of version 1 cannot retire version 2. |
| `ReservationChanged` | 409 | The referenced assignment or its hold is missing, changed or already consumed. |
| `RouteIneligible` | 422 | The proposal no longer qualifies, for example after a closure or a change to the evidence. |
| `ReservationDoesNotCoverRoute` | 422 | The route inputs, arrival window, or required resources are outside the current hold. |
| `DestinationCatalogUnavailable` and other `503` codes | 503 | Eligibility cannot be validated. Nothing is published. |

With `-p:EnableRabbitMq=true` and `Integrations:Bus:Mode=live`, the outbox is published to `evacuation.events`. An entry is marked published only after the broker confirms it.

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

For example, call `GET /api/routing/queries/first-leg?incidentId=incident-42&legId=leg-exit&caseIds=case-14&mode=walking&clearBy=2026-11-27T19:58:00Z&reservationId=reservation-demo-east`. The synthetic reservation is used during this replay when Sheltering reports no individual assignment. Fixture-backed decisions return `fixtureMode=synthetic`. Publication always requires a hold in Sheltering. The test covers an owner hold and its release, both shelters full, and a later trip from the latest Management position. This sample requires no ORS key.
