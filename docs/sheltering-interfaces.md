# Sheltering and Grouping interfaces

This contract describes the communication needed for the [sheltering and grouping workflows](domain/business-processes.md#detailed-sheltering-and-grouping-workflows). [SH-T01](tasks/sheltering/01-sites-and-resources.md) introduces the site catalog and destination reads. The workflows for grouping, reservations, attendance and publication are target interfaces for SH-T02 through SH-T07. See the [implementation guide](sheltering-implementation-guide.md) and the [command and event catalog](mvp-commands-events.md#sheltering-and-grouping-extension) for their implementation order and state transitions.

## Service ownership and dependencies

| Owner | Authoritative state | Uses |
| --- | --- | --- |
| Situation | Assembly points and incident-scoped access evidence, alongside hazard and road snapshots | The incident reference and the current Situation revision. It does not change shelter capacity. |
| Grouping, initially hosted in Management | Group membership, required care and versioned travel parties | Management case references, relevant needs and versioned route evidence. |
| Sheltering | Shelter configuration, usable resources, holds and actual occupancy. Since SH-T04 it also owns one assignment per case and leg. | Situation evidence. Allocation uses the approved group plan and Routing's route evidence. |
| Routing | Route candidates, eligibility and the publication or withdrawal of instructions | Situation snapshots and the separate destination reads from Situation and Sheltering. A destination search does not publish a route. |
| Dispatch | Assistance requests, unit bookings and pickup | The current Management case and Routing evidence. Pickup does not admit a person to a shelter. |
| Gateway and coordinator view | Composed read views and authorized command forwarding | The owning service for each query or command. An unavailable owner is shown separately from a valid empty result. |

Gateway clients use `/api/{service}/...`. Calls between services use private HTTP endpoints with the same path after removing that prefix. Grouping uses the Management service prefix while hosted there, with a separate handler and DTO boundary. The SH-T01 slice is opt-in for the offline `sample` exercise over local HTTP. Catalog writes also support the authenticated [SH-T01 Stage B configuration](tasks/sheltering/01-sites-and-resources.md#stage-b-operation).

## SH-T01 site catalog and destination reads

| Caller → owner | Method and Gateway path | Request and success response |
| --- | --- | --- |
| Coordinator → Situation | `POST /api/situation/commands/set-assembly-point` | `SetAssemblyPoint` accepts `commandId`, `expectedVersion`, `incidentId`, `pointId`, `name`, `place`, `stepFree`, `status`, `operatingWindow` and `evidence`. Returns `200` with `{ revision, item }`. |
| Coordinator or Routing → Situation | `GET /api/situation/queries/assembly-points` | Query by `incidentId`, optionally at an explicit `at` and with `eligibleOnly=true`. Returns `{ revision, situationRevision, at, assemblyPoints }`. Each point includes its configuration, `version`, `eligible` and `ineligibleReason`. |
| Shelter operator or coordinator → Sheltering | `POST /api/sheltering/commands/configure-shelter` | `ConfigureShelter` accepts `commandId`, `expectedVersion`, `shelterId`, `name`, `place`, `stepFree`, `status`, `operatingWindow`, `evidence` and `resources`. Returns `200` with `{ revision, item }`. It cannot write allocation or occupancy counts. |
| Coordinator or Routing → Sheltering | `GET /api/sheltering/queries/shelter-availability` | Accepts optional `at` and `eligibleOnly=true`. Returns `{ revision, situationRevision, at, shelters }`. Each shelter includes its configuration, `version`, `commitments`, `available`, `overcommitted`, `eligible` and `ineligibleReason`. |
| Sheltering → Situation | `GET /api/situation/queries/snapshot` | Reads the current Situation revision. Sheltering compares it with a site's evidence revision. It does not write to the Situation store. |
| Coordinator or an application handler → Routing | `GET /api/routing/queries/destinations` | Accepts `incidentId` and optional `at`. Combines both owners' eligible-only reads at the same instant. Returns `{ incidentId, situationRevision, at, assemblyPointRevision, shelterRevision, assemblyPoints, shelters }`. |

Use these shared fields for both site types:

| Field | Wire format and meaning |
| --- | --- |
| `place` | WGS 84 `longitude` and `latitude`. A `graphNodeId` is an optional string ID in the active graph, such as `exit-north` in `sample`. A `nodeId` is an optional positive integer OSM node ID. They are separate fields. |
| `stepFree` | Boolean record of confirmed step-free site access. It does not guarantee that the entire journey is accessible. |
| `status` | `draft`, `open`, `suspended` or `closed`. An open site is only a candidate while its time window and evidence still qualify. |
| `operatingWindow` | `{ opensAt, closesAt? }`. A closing time is exclusive. A null or omitted closing time means no scheduled closing instant. |
| `evidence` | `{ source, mode, verification, observedAt, validUntil?, situationRevision }`. Modes are `synthetic`, `recorded` and `client`. Verification is `verified` or `unverified`. Verified evidence requires an explicit `validUntil` and the Situation revision used to verify it. |
| `resources` | For shelters, `{ places, accessiblePlaces, intakePerHour }`. Accessible places are a subset of total places. A null intake rate means no configured hourly limit. |
| `commitments` and `available` | The response gives `heldPlaces`, `heldAccessiblePlaces`, `occupiedPlaces` and `occupiedAccessiblePlaces`; `available` reports remaining `places` and `accessiblePlaces`, never negative. A capacity reduction may set `overcommitted` while keeping existing commitments visible. SH-T01 reads these counts; SH-T04 and SH-T05 own the future write operations. |

`expectedVersion: 0` creates a site. For edits, pass the last read item `version`. A successful write returns the resulting item and the owner's new catalog `revision`. An identical retry by the same actor with the same `commandId` returns the original write result. Reusing the ID with changed content or writing on a stale item version conflicts. In OIDC mode the coordinator needs the incident scope. The operator needs both the incident and shelter scopes and may only change the resources of an existing shelter. Compare point and shelter catalog revisions only within their own catalogs. The `situationRevision` is separate and is the evidence revision that the destination read checked.

Times use ISO 8601. Read handlers use the current UTC instant when `at` is omitted. For the seeded exercise, pass `at=2026-11-27T19:20:00Z` or use the dashboard's exercise clock. An eligibility check uses verified evidence at the queried instant and the current Situation revision. A change in that revision makes earlier site evidence stale until it is verified again. An operating window and the evidence interval are checked separately.

By default, operator reads retain closed, unavailable and overcommitted sites with their reasons. `eligibleOnly=true` excludes them. Reasons can include `SiteDraft`, `SiteSuspended`, `SiteClosed`, `OutsideOperatingWindow`, `UnverifiedEvidence`, `EvidenceOutsideValidity`, `StaleSituationEvidence`, `SituationUnavailable`, `Overcommitted` or `NoAvailablePlaces`. A normal result with no qualifying sites is `200` with an empty list. If evidence cannot be checked, the eligible-only read fails closed. When the two owner reads use different Situation revisions, Routing returns `503 DestinationEvidenceChanged` and the caller retries the whole query. An eligible result means the site can be considered. It does not validate road traversal, reserve resources, approve a route or guarantee intake for a particular party.

The [request and response examples](../contracts/http/sheltering/README.md) use synthetic IDs. For compatibility with the `cassini.shelters.v1` input discussed in [Routing PR #11](https://github.com/MarekR8/CASSINI-Guardians-of-the-Galaxy/pull/11), flatten `shelterId → id`, the name and WGS 84 coordinates, numeric OSM `nodeId` when available, `resources.places → capacity`, `resources.intakePerHour → intakePerHour`, and `stepFree`. This planning input contains nominal capacity, not held, occupied or currently available places. A sample graph string never becomes an OSM ID. The owning catalog remains authoritative for current availability and future allocation.

## Draft interfaces for SH-T02 through SH-T07

The following paths and fields are proposals for integration. SH-T04 implements the Sheltering assignment, reservation, transfer, invalidation and release rows in Stage A. The [SH-W4 examples](../contracts/http/sheltering/README.md#sh-w4-assignments-and-reservations) give their exact payloads, the candidate query and the allocation outbox. Keep the current baseline endpoints available. Draft writes also carry `correlationId`, with `expectedVersion` or explicit expected versions for all records in a multi-aggregate command. Use the role and incident or site scope derived from authentication. Clients cannot grant authority by including a role in JSON.

| Caller → owner | Method and proposed Gateway path | Payload or query and outcome |
| --- | --- | --- |
| Grouping → Management | `GET /api/management/queries/cases/{caseId}` | Reuse the existing case read where possible. Consume the case reference, incident, travel or mobility requirements, and relevant position or progress revision. Grouping does not edit the case. |
| Field operator → Grouping | `POST /api/management/commands/register-evacuation-group` | `incidentId`, `groupId`, `caseIds`, typed links and evidence. Returns the group and its version after validating case references. |
| Field operator → Grouping | `POST /api/management/commands/update-group-composition` | `incidentId`, `groupId`, membership and relationship changes, evidence. Changes the group version and flags affected plans. |
| Field operator → Grouping | `POST /api/management/commands/record-care-handover` | Group reference, `dependentCaseId`, `outgoingCaregiverCaseId`, `incomingCaregiverCaseId`, `effectiveAt` and confirmation evidence. Records care transfer only after checking current care and authority. |
| Coordinator or Sheltering → Grouping | `GET /api/management/queries/travel-party-proposal/{groupId}`; `GET /api/management/queries/approved-travel-plan/{groupId}` | Query by incident. Return the proposal or approved plan with party case IDs, required care, shared-destination preferences, source revisions and eligibility reasons. Return an explicit review-required state for an affected approved plan. |
| Coordinator or workflow → Grouping | `POST /api/management/commands/approve-travel-plan`; `POST /api/management/commands/flag-travel-plan-for-review` | Approve a proposal carrying the group version, parties and current routing evidence, or flag a specific plan revision with a reason. Return the accepted revision or a conflict. |
| Coordinator or Sheltering workflow → Routing | Existing proposal, validation, publication and withdrawal contracts in the [MVP interfaces](mvp-interfaces.md#http-endpoints) | Carry a typed destination reference (`exit`, `assemblyPoint` or `shelter`), origin, travel constraints, evidence revision and party or case references. A proposal returns estimated arrival, validity and checked evidence or a rejection reason. Publishing receives the shelter assignment reference when applicable and rechecks current eligibility. |
| Coordinator → Sheltering | `POST /api/sheltering/commands/open-shelter-assignment`; `GET /api/sheltering/queries/shelter-assignment` | Key by `incidentId`, `caseId` and `legId`. Opening records needs and optional `groupPlanRef`. Reads expose pending, reserved, awaiting replacement, admitted or closed state, current destination if any, and assignment revision. |
| Coordinator → Sheltering | `POST /api/sheltering/commands/reserve-shelter-places` | `shelterId`, expected shelter version, one or more assignment references and versions, resource needs and arrival window per person, hold validity, approval and group or routing evidence. Return all reservations issued or reject the entire batch. |
| Coordinator or workflow → Sheltering | `POST /api/sheltering/commands/reassign-shelter-places`; `POST /api/sheltering/commands/invalidate-shelter-assignment`; `POST /api/sheltering/commands/release-shelter-reservation` | Target exact assignment and reservation revisions. Reassignment adds the new eligible shelter and expected capacity versions for both sites. Invalidation and release include reason and current evidence or authorized expiry/cancellation. Return the new or pending assignment state. |
| Shelter operator → Sheltering | `POST /api/sheltering/commands/confirm-shelter-admission`; `POST /api/sheltering/commands/confirm-shelter-departure` | Include `shelterId`, case references, `reportId` and `observedAt`, reservation or admission references, and relevant expected versions. Return only the holds converted or occupied resources released. For a walk-in, check resources and reconcile any other active hold first. |
| Coordinator → Dispatch | Existing `POST /api/dispatch/commands/request` | Use the owning assistance request contract when the first leg cannot be done without help. A pickup location or an assistance request does not reserve shelter capacity. |

`GroupPlanRef` identifies `incidentId`, `groupId` and the approved `planRevision`. An assignment reference identifies `incidentId`, `caseId` and `legId`, and adds its version when mutating that assignment. A verified exit and a subsequent shelter journey use distinct leg IDs. The existing Routing subject remains the reference for the current instruction and its version. Pin the relevant plan, assignment, route and Situation revisions before applying a change. Where an older reference cannot be verified, return a conflict or pending state and reconcile against the owner.

### Write order and atomicity

1. Read the current case and, where applicable, the approved travel plan. Ask Routing to validate eligible paths and estimated arrival. If there is no timely confirmed shelter, the coordinator can issue an independently verified first leg. A missing route or unresolved required care is handled as a request for assistance or verification.
2. Sheltering rechecks versioned evidence and commits assignments, reservations, receipts and outbox entries in one local transaction. On reassignment, the old unused hold is released in the same transaction as the new hold. An admitted person's resources stay occupied until departure is confirmed.
3. An authorized publisher rechecks the current reservation and route before publishing through Routing. A failure after booking remains visibly pending. On retry, read both owners before publishing or withdrawing. A status change in one service does not prove that an operation in another already succeeded.
4. Admission converts only the arriving person's held resources to occupied resources. Use the admission or departure report ID to avoid double counting even if the report is retried with a different transport attempt ID. A route acknowledgement, pickup or GPS observation cannot count as admission.

### Failure and compatibility rules

| Result | Caller behavior |
| --- | --- |
| `400` invalid payload, ID, clock or configuration | Correct the input. Do not issue a replacement write based on an invalid read. |
| `403` forbidden in the authenticated target flow | Check actor or service scope without exposing incident or care details. |
| `404` unknown reference or optional interface disabled | Check that the module is enabled and that the supplied identifiers belong to the active scenario. |
| `409` version or idempotency conflict | Reload the owning record before deciding again. On a genuine retry or uncertain write outcome, preserve the original command ID and inputs. Use a fresh ID for a new decision. |
| `422` rejected decision or unavailable required capacity | Keep or withdraw the existing instruction according to current validity. Surface a reason such as `InsufficientCapacity`, `IntakeLimitReached`, `RouteIneligible` or an unresolved care requirement. |
| `503` dependency or evidence changed | Treat the result as unknown and retry the read or reconcile the command first. Never convert it to an eligible result or an empty collection of confirmed candidates. |

Return a stable `code` for business errors using the owner's HTTP problem response. The status of an unverified site or a reserved place remains distinct from a transport error.

## Target integration events

Use the existing [event envelope and RabbitMQ topology](mvp-interfaces.md#rabbitmq-bindings-and-consumers). These are routing keys on `evacuation.events`. SH-T01 commits point and shelter events to the owner outboxes and uses the SH-T06 publisher in the optional full profile. Subscribers bind the required topics and deduplicate by event ID.

| Publisher | Event → proposed routing key | Consumers and effect |
| --- | --- | --- |
| Situation | `AssemblyPointChanged.v1` → `situation.assembly-point-changed.v1` | Routing and the coordinator projection recheck affected first-leg destinations. |
| Grouping | `EvacuationGroupChanged.v1` → `grouping.evacuation-group-changed.v1`; `CareHandoverRecorded.v1` → `grouping.care-handover-recorded.v1` | Sheltering and authorized planners refresh the affected group or care reference. Events carry IDs and versions instead of private relationship details. |
| Grouping | `TravelPlanApproved.v1` → `grouping.travel-plan-approved.v1`; `TravelPlanReviewRequired.v1` → `grouping.travel-plan-review-required.v1` | Sheltering and the coordinator projection reconcile the plan revision. A stale review event cannot undo a later approval. |
| Sheltering | `ShelterAvailabilityChanged.v1` → `sheltering.shelter-availability-changed.v1` | Recheck impacted holds and refresh the operator's availability projection. |
| Sheltering | `ShelterAssignmentChanged.v1` → `sheltering.shelter-assignment-changed.v1` | The coordinator and authorized publication handler reconcile the assignment and route. The event does not itself publish an instruction. |
| Sheltering | `ShelterAdmissionConfirmed.v1` → `sheltering.shelter-admission-confirmed.v1`; `ShelterDepartureConfirmed.v1` → `sheltering.shelter-departure-confirmed.v1` | Management or the coordinator projection refreshes reception and occupancy views. This does not replace a separate hazard exit confirmation. |
| Situation and Routing | Existing `SituationChanged.v1` and `RouteWithdrawn.v1` keys | Grouping and Sheltering recheck relevant evidence and the affected revision. Re-read current state before invalidating or redirecting. |

Publish after commit through the owner's outbox. Deduplicate by consumer and event ID. A single subject revision can legitimately have more than one event. Process distinct events at the same revision and reconcile missing or out-of-order information using an authorized read from the owner. Keep personal details out of broadcast payloads and shared logs.
