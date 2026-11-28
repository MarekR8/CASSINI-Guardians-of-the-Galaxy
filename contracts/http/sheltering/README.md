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
| [Confirm admission](confirm-shelter-admission.request.json) and [result](confirm-shelter-admission.response.json) | Request to and response from `POST /api/sheltering/commands/confirm-shelter-admission`. | SH-T05 |
| [Confirm departure](confirm-shelter-departure.request.json) | Request to `POST /api/sheltering/commands/confirm-shelter-departure`. The response has the same shape as an admission. | SH-T05 |
| [Shelter reception](shelter-reception.response.json) | Illustrative response to `GET /api/sheltering/queries/shelter-reception?shelterId=shelter-a&at=2026-11-27T19:51:00Z`. | SH-W5 (SH-T05) |
| Instruction state: [pending](shelter-instruction.pending.response.json), [current](shelter-instruction.current.response.json) and [withdrawn](shelter-instruction.withdrawn.response.json) | Responses to `GET /api/sheltering/queries/shelter-instruction` and `POST /api/sheltering/commands/reconcile-shelter-instruction`. | SH-W5 (SH-T06) |

The catalog command examples use separate IDs from the seeded catalog so they can create entries with `expectedVersion: 0`. Repeating either command with the same payload returns its original result. For a subsequent change, use a new command ID and the site's returned version.

## SH-W4 assignments and reservations

The allocation examples were recorded from one fresh exercise for `case-14` on `leg-shelter`. Evidence IDs come from Routing and differ when the shelter catalog or Situation revision changes. Read the current candidates instead of reusing these IDs.

| Method and Gateway path | Behaviour |
| --- | --- |
| `GET /api/sheltering/queries/shelter-assignment?incidentId&caseId&legId` | One assignment with `version`, `status`, `needs`, optional `groupPlanRef`, the current `reservation` and the last `withdrawn` hold. `404 AssignmentNotFound` when none was opened. |
| `GET /api/sheltering/queries/shelter-assignments?incidentId` | All assignments in the incident, for the coordinator view. |
| `GET /api/sheltering/queries/shelter-candidates` | Takes `incidentId`, `legId`, `caseIds`, `mode` and `at`, plus optional `node`, `clearBy`, `stepFree`, `groupId`, `groupRevision` and `transportId`. It lists only feasible shelters, ranked by group members already held there and then by earlier arrival. Each candidate carries the `route` object for a reservation. `rejected` gives the reason for every other shelter. |
| `GET /api/sheltering/queries/allocation-evidence?incidentId&caseId&legId` | Reconciliation evidence for every committed assignment change: decision, status and version, shelter and its version, reservation, Routing evidence ID, Situation and plan revisions, withdrawn reservation, finding and evidence reference. `caseId` and `legId` are optional filters. |
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

A `route.query` names either `caseIds`, which must include the assignment's case, or a graph `node` for synthetic exercise people that Management does not track. Each hold is written into the shelter's `reception.holds` with its commitments in the same commit as the assignment, so SH-T05 admission converts exactly that hold. The assignment then reads as `Admitted`, and as `Closed` after a confirmed departure. A hold released by a walk-in elsewhere leaves it `AwaitingReplacement`. Allocation commands never move or release admitted occupancy or the baseline occupancy configured in the catalog.

Replay the exercise with `python tests/sheltering_reservations_integration.py`. With `-p:EnableShelteringPostgres=true` and `ConnectionStrings:Sheltering`, the same contract is served from PostgreSQL. `python tests/sheltering_postgres_integration.py` runs it on two instances against a fresh database.

## SH-W5 instruction publication (SH-T06)

A committed booking is not a route. The coordinator publishes it through Routing in a separate step, after the booking commits. The examples were recorded from `python tests/sheltering_publication_integration.py`.

| Method and Gateway path | Behaviour |
| --- | --- |
| `GET /api/sheltering/queries/shelter-instruction?incidentId&caseId&legId` | Reads the assignment here and the [Routing instruction](../routing/README.md#shelter-instruction-publication-sh-t06) for the same leg. Returns `state`, `reason`, `assignment` and `instruction`. |
| `POST /api/sheltering/commands/reconcile-shelter-instruction` | Takes `commandId`, `correlationId`, `incidentId`, `caseId`, `legId` and `expectedAssignmentVersion`. Reads both owners, then publishes the current hold, withdraws guidance the assignment no longer supports, or changes nothing. Returns the new state. |
| `GET /api/sheltering/queries/shelter-instruction-board?incidentId` | Stage B only (`Integrations:Bus:Mode=live`). The event-driven board, with one row per leg and `counts` per state. |

The `state` field takes these values:

| State | Meaning |
| --- | --- |
| `PendingPublication` | The hold is reserved, but Routing has no instruction for exactly this reservation. This is the state after booking, after a failed publication and after a transfer until it is published. |
| `Current` | Routing's current route was published for this reservation. |
| `WithdrawalPending` | Guidance is still current, but the assignment no longer holds that reservation, for example after a closure. |
| `Withdrawn` | No guidance is current. The last withdrawal carries its reason. |
| `NoInstruction` | Nothing was ever published, for example while the assignment is `Pending`. |
| `PublicationUnknown` | Routing did not answer. This state is never shown as an issued route. |

Before publishing, the reconcile command repeats the `destination-route` proposal with the hold's route inputs. The hold must still cover the expected arrival. Routing then rechecks the proposal's evidence ID before it commits.

The `commandId` is passed to Routing unchanged. A retry after an unknown outcome therefore finds the instruction already committed, or Routing's receipt, and never adds a route version. An old command cannot restore earlier guidance: it fails with `409 AssignmentVersionConflict` once the assignment has moved on.

Errors:
- `422 RouteIneligible` or `HoldExpiresBeforeArrival`: the booking stays visibly pending. Invalidate or transfer it before reconciling again.
- `409 AssignmentChanged`: the assignment changed while Routing committed. Reconcile again with a new command ID.
- `503 RoutingUnavailable`: the outcome is unknown. Read the instruction state before retrying.

In Stage B, both Sheltering outboxes go to RabbitMQ: the allocation outbox and each shelter's reception events. An event stays pending until the broker confirms it. The confirmed event IDs are stored next to the outbox, in the PostgreSQL table `sheltering_published` when that store is used. The board consumes `ShelterAssignmentChanged.v1`, `RoutePublished.v1` and `RouteWithdrawn.v1` from the `sheltering.events` queue. It counts only committed facts. The ordering rules are in [`contracts/events/README.md`](../../events/README.md#sh-w5-instruction-events-sh-t06).

## Admissions and departures (SH-T05)

These examples use the [hold fixture](../../../data/demo/sheltering/sample-holds.json), enabled with `Sheltering:HoldsFile`, so they run without SH-T04 commands. A hold is per case and leg. Holds issued by `reserve-shelter-places` and `reassign-shelter-places` are written into the same shelter record (`reception.holds`) and are admitted in the same way.

- **Admission** converts only the hold of each reported person into occupied places, so free places do not change. A person without a hold here is a walk-in and must state `resources`. A walk-in needs an open shelter, a time within its operating window and free places, including accessible ones. Any hold the person has at another shelter for the same leg is released in the same transaction and listed in `releasedHolds`.
- **Each report counts once.** A `reportId` is processed once across all shelters. A retry with another `commandId` or a stale `expectedVersion` returns the original result. The same `reportId` with different content returns `409 ReportConflict`. A new report for someone already present returns `AlreadyAdmitted` and changes nothing.
- **Departure** frees the admitted places once. A second report returns `AlreadyDeparted`.
- **Batches are all or nothing.** If any person in a report cannot be applied, nothing changes.

| Code | Status | Meaning |
| --- | --- | --- |
| `InvalidReport` | 400 | Missing IDs or time, more than 100 people, or the same person twice |
| `UnknownShelter`, `NotAdmitted` | 404 | No such shelter, or no admission to depart |
| `VersionConflict` | 409 | Read the shelter again. Two concurrent reports for one person admit them once. |
| `ReportConflict` | 409 | The `reportId` was used for different content |
| `AdmittedElsewhere` | 409 | The person is present at another shelter for this leg. Confirm that departure first. |
| `ReservationMismatch`, `ResourceMismatch` | 409 | The report does not match the hold here |
| `NoAvailablePlaces` | 409 | The walk-in does not fit. Record the unmet need with the coordinator. |
| `WalkInResourcesRequired`, `ShelterNotAdmitting`, `OutsideOperatingWindow`, `DepartureBeforeAdmission` | 422 | The report cannot be accepted as stated |

`GET queries/shelter-reception` returns separate counters:
- awaiting admission (active holds);
- overdue arrivals (the arrival window has passed and the hold is kept);
- people present;
- confirmed admissions and departures;
- the authoritative commitments;
- seeded places that no tracked case accounts for.

`GET queries/shelter-reception-events` lists the [`ShelterAdmissionConfirmed.v1`](../../events/ShelterAdmissionConfirmed.v1.json) and [`ShelterDepartureConfirmed.v1`](../../events/ShelterDepartureConfirmed.v1.json) entries. Each is written in the same state change as its admission or departure. SH-T06 publishes them.

### Operator scope and transfers (SH-T05 Stage B)

When `Sheltering:OperatorsFile` is set, the reception commands and queries require `Authorization: Bearer <token>`. The Gateway forwards the header. Each account in the file has `operatorId`, `role` (`shelter-operator` or `coordinator`), `incidentId`, `shelterIds` and `tokenSha256`. Only the hash is stored. `scripts/offline_demo.py` writes random exercise tokens into the temporary state directory.

| Code | Status | Meaning |
| --- | --- | --- |
| `AuthenticationRequired` | 401 | No token, or a token that matches no account |
| `RoleNotPermitted` | 403 | Only a shelter operator reports admissions and departures. A coordinator reads. |
| `ShelterNotAssigned`, `IncidentNotAssigned` | 403 | Outside the account's shelters or incident |

Admissions and departures record the reporting `operatorId`.

A person moves between shelters as two legs. The person is admitted at the new shelter on the next leg, for example `leg-transfer`, while still present at the first. The admission result and the event then carry `transferFrom` and `transferFromShelterId`. The first shelter keeps its occupied places. Its view lists the stay's `onwardAdmission` and counts it under `transferredAwaitingDeparture` until that shelter confirms the departure. A person present at a shelter cannot be admitted there again on another leg (`409 AlreadyPresent`).
