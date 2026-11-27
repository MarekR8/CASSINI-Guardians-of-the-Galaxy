# MVP storage and state design

Use this document when creating migrations and aggregate handlers. The [aggregate model](domain-architecture.md) describes domain ownership. The [interface specification](mvp-interfaces.md) describes the public shape. Database names, tables and fields here are the initial implementation design.

## Common storage conventions

- Store opaque IDs as `text` so stable demo IDs and generated IDs use the same contracts. Generate production-shaped IDs in the owning service. Store times as `timestamptz`, and serialize them in UTC. Effective intervals are half-open. A missing end is allowed for an ongoing restriction.
- Give each root a `bigint` version. Configure it as an EF Core concurrency token. Change state with a compare-and-update inside a local transaction. Translate a lost update into `409 VersionConflict` and keep the committed state intact.
- Use local foreign keys inside a database. References such as `case_id` in Dispatch and `assignment_id` in Routing are opaque identifiers across services. Resolve needed details through authorized queries.
- In each writing service, store a `command_receipts` entry keyed by `(caller_scope, command_id)` with request hash, status and committed result reference. Store the receipt with the domain change. An identical retry returns that result. Reuse with a different payload returns a conflict. A transient dependency failure before a commit stays retryable with the same key.
- Add `outbox_messages` and `consumer_receipts` where the service publishes or subscribes. Keep a committed state update, its receipt and its outgoing events in the same database transaction. Store `(consumer_name, event_id)` as the consumer deduplication key.

## Management database

| Table | Key and minimum fields | Rule or index |
| --- | --- | --- |
| `incidents` | `incident_id`, state, reported geometry or area reference, `opened_at`, version | Alert ingestion can create an incident in `Open`; evidence status stays with the alert and situation revisions. |
| `hazard_alerts` | `alert_id`, `incident_id`, `client_alert_id`, reporting actor or source, hazard type, reported geometry, `observed_at`, review disposition | Unique `(reporter_scope, client_alert_id)`. Keep original source and observation while storing later review dispositions. |
| `evacuation_cases` | `case_id`, `incident_id`, `subject_ref`, mobility profile, confirmed progress, version | Unique `(incident_id, subject_ref)` for known subjects. Link the resident identity to the subject independently of display data. |
| `case_progress_reports` | `(case_id, report_id)`, report type, actor or source, `observed_at`, `recorded_at`, referenced route version and optional corrected report ID | Preserve history. Applying a correction is an explicit operation. A duplicate report cannot advance the case twice. |
| `case_last_positions` | `case_id`, latest accepted source, coordinates, observed time, accuracy and optional GNSS evidence | Keep the current known measurement separate from confirmed progress. A delayed observation cannot overwrite a newer observation. |
| `case_operational_projection` | `case_id`, current route reference or withdrawal, assignment references, producer revisions, last update times | Read model for the incident monitor. Preserve last update time and reconcile missing relevant state with the owner. |

Keep confirmed case progress simple: `Open` → `ExitConfirmed`. The monitor derives `InProgress`, assistance and delivery indicators from operational reports and projections. An authorized correction can explicitly reopen an incorrectly confirmed case while preserving the original report. A route publication, unit pickup or location fix leaves the confirmed exit state unchanged.

## Situation Data database

| Table | Key and minimum fields | Rule or index |
| --- | --- | --- |
| `hazard_assessments` and `hazard_revisions` | `hazard_id`; `(hazard_id, revision_number)` with extent, effective window, source, observation time, evidence kind and review status | Append revisions. Use a GiST index on the spatial extent. Expose review findings separately from confirmed constraints. |
| `network_segments` | `segment_id`, source network ref, start and end IDs, direction, `LineString` in SRID 4326, supported modes, accessibility facts, version | GiST on geometry. Keep stable IDs across snapshots and record unknown access facts explicitly. |
| `access_restrictions` | `restriction_id`, segment ID, affected modes, state, effective window and evidence | Index segment and effective time. Preserve a confirmed restriction until its expiry or an authorized superseding update. |
| `traffic_observations` | Observation ID, segment ID, travel mode, travel time or throughput with unit, measured time, evidence kind | Read the relevant fresh observation. Keep demo estimates labeled. Missing traffic uses the fixture's baseline for ranking and remains unknown in the monitor. |
| `exit_points` | `exit_point_id`, geometry, supported modes, evidence reference | Candidate points are checked against applicable hazard extents and access at expected arrival. |
| `situation_snapshots` | `snapshot_id`, `incident_id`, `as_of`, extent, evidence manifest, routing revision and source coverage | Store an immutable manifest of the evidence used. A new view does not overwrite the prior snapshot. |

Build snapshots from consistent local reads. Advance an incident-level routing revision when new evidence affects eligibility. Use the revision to correlate snapshots and revalidation results. Record the source observation date separately from effective simulation time when replaying an archived Copernicus product. The read model may contain estimated population counts with coverage metadata. Management remains the source for individual cases.

## Routing database

| Table | Key and minimum fields | Rule or index |
| --- | --- | --- |
| `route_instructions` | `(subject_kind, subject_id)`, incident ID, current route version if any, aggregate version and unresolved reason | A subject is a civilian case or a response assignment. The root is the sole owner of the current pointer. |
| `route_versions` | `(subject_kind, subject_id, route_version)`, immutable legs, snapshot reference, validity window, approval actor and time, lifecycle state | Keep historical versions. Assign route versions monotonically per subject. Route version and aggregate version answer different questions. |
| `route_proposals` | `proposal_id`, subject, versioned evidence, candidate legs, applicability and expiry | Validate and scope proposals to the exact subject. Expired or mismatched proposals cannot be published. |
| `route_segment_uses` | Subject, route version, leg, segment or geometry reference, traversal window | Index by segment and time for impact lookup. Include geometry or a footprint when the provider returns a path without matching segment IDs. |
| `route_review_jobs` | Job ID, source event ID, subject and target route version, state, attempts and retry time | Persist work before acknowledging the originating event. Repeated or delayed jobs cannot withdraw a newer version. |

Use these transitions: a proposed candidate can be published only after approval and current validation. `Published` can become `Superseded`, `Withdrawn` or `Expired`. Publishing a replacement updates the current pointer and history in one Routing transaction. Withdrawal refers to the exact target version. An expired route is omitted from active guidance at read time even if a cleanup job has yet to update the stored status. A candidate result from the routing provider stays separate from publication.

For `SituationChanged.v1`, persist jobs keyed by source event and affected instruction version. Confirm the current route and effective traversal window when processing a job. If a confirmed closure already establishes a conflict for the indexed route, withdraw it. When the match is uncertain, record pending review and revalidate with Situation Data. If a required check cannot complete, surface that fact and hold new publication. A later replacement follows the normal approval path.

## Dispatch database

| Table | Key and minimum fields | Rule or index |
| --- | --- | --- |
| `assistance_requests` | `request_id`, incident and case references, pickup point, required capabilities, outbound transport flag, state and version | The request root owns assignment history and unresolved reasons. |
| `response_units` | `unit_id`, capabilities, readiness, last position and observation time, travel mode and version | Update readiness and evaluate booking in Dispatch. Unit availability also depends on active bookings. |
| `dispatch_assignments` | `assignment_id`, request ID, unit ID, approved actor and time, state, route reference, version, `booked_at`, `released_at` | Two partial unique indexes enforce one unreleased booking per request and per unit. Publish a task only when its route is current and authorized. |
| `assignment_progress_reports` | `(assignment_id, report_id)`, step, source, observed time, recorded time | Enforce the assignment transition and retain its history. Duplicate reports leave the state unchanged. |
| `assignment_publication_jobs` | `assignment_id`, proposal ID, command ID for publication, retry state and attempt count | Created in the same Dispatch transaction as booking. Reconcile a timeout with Routing before replacing the proposal or booking. |

Use PostgreSQL partial unique indexes on `(request_id) WHERE released_at IS NULL` and `(unit_id) WHERE released_at IS NULL`. Create bookings in one transaction. If both requests race for the same available unit, map the constraint conflict to `UnitUnavailable`. Pending route publication and review keep the booking active. Release it only on completion, confirmed cancellation or an explicit authorized disposition. [PostgreSQL partial indexes](https://www.postgresql.org/docs/17/indexes-partial.html).

Assignment progression is `PendingRoutePublication` → `Ready` → `Acknowledged` → `ArrivedAtPickup` → `PickupConfirmed` → `Completed` for a transport task. A task without transport can complete from `ArrivedAtPickup` once its work is confirmed. Route withdrawal adds a `RouteReviewRequired` flag without erasing a physical milestone. Confirmed cancellation transitions the assignment to `Cancelled`, records the reason and frees the booking. A correction records actor, report and reason. The `EvacuationCase` still requires its own exit confirmation.

## Transaction and retry recipes

1. **Alert.** Insert or find the alert under its client identity, persist the incident and outbox record, and commit. Query Situation Data after commit. A snapshot failure leaves a discoverable alert and pending situation result.
2. **Booking.** Read the request and unit, reserve a stable proposed assignment ID, and obtain a route proposal over HTTP outside the database transaction. Open a short transaction and recheck version and availability, then write the booking, publication job and receipt. Commit before publishing the route. A completion or cancellation changes the booking and releases the unit in the same local transaction.
3. **Route publish.** Revalidate with current Situation Data outside the write transaction. In Routing, compare the root version and proposal expiry, set the current version and write the outbox event. A remote constraint can still arrive after approval. The change workflow revalidates affected instructions and withdraws conflicts when the system learns about them.
4. **Outbox and consumers.** Claim eligible outbox rows with a short lease, using `FOR UPDATE SKIP LOCKED` where multiple workers compete. Publish after the claim transaction, then record the broker confirmation. Retry expired claims. A duplicate after broker confirmation is harmless because the consumer receipt is unique. A consumer writes its effect or a durable follow-up job before acknowledging. [PostgreSQL row locking](https://www.postgresql.org/docs/17/sql-select.html), [RabbitMQ confirms and acknowledgements](https://www.rabbitmq.com/docs/confirms).

See [routing and demo fixtures](mvp-routing-and-demo.md) for the version, closure and concurrency checks to exercise before the first demonstration.
