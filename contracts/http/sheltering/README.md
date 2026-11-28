# Sheltering HTTP examples

These examples use the [Sheltering and Grouping interfaces](../../../docs/sheltering-interfaces.md) and synthetic `sample` scenario. Read the current Situation revision before submitting a command with verified evidence. The examples assume a fresh exercise with Situation revision `0`. Read availability at `2026-11-27T19:20:00Z` to evaluate the seeded exercise period.

| Example | Request or response | Scope |
| --- | --- | --- |
| [Set assembly point](set-assembly-point.request.json) | Request to `POST /api/situation/commands/set-assembly-point`. | SH-T01 |
| [Configure shelter](configure-shelter.request.json) | Request to `POST /api/sheltering/commands/configure-shelter`. | SH-T01 |
| [Shelter availability](shelter-availability.response.json) | Illustrative response to `GET /api/sheltering/queries/shelter-availability?at=2026-11-27T19:20:00Z`. | SH-T01 |
| [Routing planning input](shelters.v1.example.json) | Flat nominal-capacity projection for the `cassini.shelters.v1` planning input. It is not a live availability response. | Contract alignment with Routing PR #11 |
| [Open an assignment](open-shelter-assignment.request.json) | Request to `POST /api/sheltering/commands/open-shelter-assignment`. It returns the [pending assignment](shelter-assignment.pending.response.json). | SH-T04, SH-W4 |
| [Ranked shelters](shelter-candidates.response.json) | Response to `GET /api/sheltering/queries/shelter-candidates`. | SH-T04 |
| [Reserve a place](reserve-shelter-places.request.json) | Request to `POST /api/sheltering/commands/reserve-shelter-places`. It returns the [reserved assignments](reserve-shelter-places.response.json). | SH-T04, SH-W4 |
| [Transfer a hold](reassign-shelter-places.request.json) | Request to `POST /api/sheltering/commands/reassign-shelter-places` after `shelter-a` closes. See the [response](reassign-shelter-places.response.json). | SH-T04, SH-W4 |
| [Invalidate an assignment](invalidate-shelter-assignment.request.json) | Request to `POST /api/sheltering/commands/invalidate-shelter-assignment` when no shelter remains. It returns an [assignment awaiting replacement](shelter-assignment.awaiting-replacement.response.json). | SH-T04, SH-W4 |
| [Release a reservation](release-shelter-reservation.request.json) | Request to `POST /api/sheltering/commands/release-shelter-reservation`. | SH-T04 |

The catalog command examples use separate IDs from the seeded catalog so they can create entries with `expectedVersion: 0`. Repeating either command with the same payload returns its original result. For a subsequent change, use a new command ID and the site's returned version.

## SH-W4 assignments and reservations

The allocation examples were recorded from one fresh exercise for `case-14` on `leg-shelter`. Evidence IDs come from Routing and differ when the shelter catalog or Situation revision changes. Read the current candidates instead of reusing these IDs.

| Method and Gateway path | Behaviour |
| --- | --- |
| `GET /api/sheltering/queries/shelter-assignment?incidentId&caseId&legId` | One assignment with `version`, `status`, `needs`, optional `groupPlanRef`, the current `reservation` and the last `withdrawn` hold. `404 AssignmentNotFound` when none was opened. |
| `GET /api/sheltering/queries/shelter-assignments?incidentId` | All assignments in the incident, for the coordinator view. |
| `GET /api/sheltering/queries/shelter-candidates` | Takes `incidentId`, `legId`, `caseIds`, `mode` and `at`, plus optional `node`, `clearBy`, `stepFree`, `groupId`, `groupRevision` and `transportId`. It lists only feasible shelters, ranked by group members already held there and then by earlier arrival. Each candidate carries the `route` object for a reservation. `rejected` gives the reason for every other shelter. |
| `GET /api/sheltering/queries/allocation-outbox?after` | Committed `ShelterAssignmentChanged.v1` and `ShelterAvailabilityChanged.v1` events in the shared envelope, numbered by `sequence`, for SH-T06. |
| `POST /api/sheltering/commands/open-shelter-assignment` | Opens a `Pending` assignment per case and leg with `needs.places` (1–10) and `needs.accessiblePlaces`. A `groupPlanRef` must name the current approved Grouping plan containing the case. The same needs return the existing assignment; different needs return `409 AssignmentAlreadyOpen`. |
| `POST /api/sheltering/commands/reserve-shelter-places` | Holds every listed `Pending` or `AwaitingReplacement` assignment at one shelter or rejects the whole batch. Each entry carries `expectedVersion` and `route: { evidenceId, query }`. |
| `POST /api/sheltering/commands/reassign-shelter-places` | Moves current holds from `fromShelterId` to `shelterId` with expected versions for both shelters. The new hold and the release of the old one commit together. On failure the original hold stays unchanged. |
| `POST /api/sheltering/commands/invalidate-shelter-assignment` | Withdraws a `Reserved` assignment only when the shelter is no longer open or Routing no longer confirms the route. The result is `AwaitingReplacement` with the checked reason. Optional `at` rechecks at the current exercise time. |
| `POST /api/sheltering/commands/release-shelter-reservation` | `Cancelled` or `HoldExpired` (with `observedAt` at or after `holdsExpireAt`) and `stillRequired`. The result is `AwaitingReplacement` or `Closed`. |

Before a booking or transfer commits, Sheltering repeats Routing's `destination-route` query with `expectedEvidenceId`. It then rechecks the following against current state:

- the shelter version, opening, evidence and operating window at the expected arrival;
- remaining places and accessible places, including the configured baseline commitments;
- arrivals already held in the same clock hour against `intakePerHour`;
- a hold validity that covers arrival;
- the approved plan and its required-care pairs, which must be held together at the same shelter and leg.

Business rejections are `422` with `InsufficientCapacity`, `InsufficientAccessiblePlaces`, `IntakeLimitReached`, `RouteIneligible`, `ShelterIneligible`, `RequiredCareSplit`, `HoldExpiresBeforeArrival`, `GroupPlanReviewRequired`, `OccupancyCannotTransfer` or `AssignmentStillEligible`. Stale versions return `409` (`AssignmentVersionConflict`, `ShelterVersionConflict`, `GroupPlanChanged`, `RouteEvidenceStale`). An unavailable Routing or Grouping owner returns `503`.

A `route.query` names either `caseIds`, which must include the assignment's case, or a graph `node` for synthetic exercise people that Management does not track. Admission and departure (SH-T05) will set `Admitted` and convert holds to occupancy. Allocation commands never move or release admitted occupancy or the baseline occupancy configured in the catalog.

Replay the exercise with `python tests/sheltering_reservations_integration.py`.
