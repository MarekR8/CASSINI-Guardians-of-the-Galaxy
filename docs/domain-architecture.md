# MVP domain architecture and service contracts

This proposal uses four service boundaries for the [MVP workflows](business-processes.md). An aggregate root owns a local consistency boundary. An entity has an identity and lifecycle within that boundary. A value object is immutable and compared by value. Services own their writes and exchange IDs, snapshots and committed facts.

## Service boundaries and aggregate model

| Owner | Aggregate root and identity | Child entities | Value objects | What the root controls |
| --- | --- | --- | --- | --- |
| Evacuation Management | `EvacuationIncident(incidentId)` | `HazardAlert` | `ReportedArea`, `Evidence` | Opening and updating the incident, attributing reports and retaining alert references |
| Evacuation Management | `EvacuationCase(caseId)` | `ProgressConfirmation` | `SubjectRef`, `MobilityProfile`, `ProgressState` | One case per known subject and incident. Explicit progress confirmations and corrections. |
| Situation Data | `HazardAssessment(hazardId)` | `HazardRevision` | `HazardExtent`, `TimeWindow`, `Evidence` | Versioned hazard extents and their evidence, including reports, confirmed observations and forecasts |
| Situation Data | `NetworkSegment(segmentId)` | `AccessRestriction` | `TravelModeSet`, `TimeWindow`, `TrafficEstimate`, `Evidence` | Effective access restrictions, supported modes and time-sensitive traffic information |
| Evacuation Routing | `RouteInstruction(routeSubjectId)` | `RouteVersion` | `RouteSubject`, `RouteLeg`, `SegmentTraversal`, `SafePoint`, `Approval`, `SnapshotRef` | One current authorized route or bundle per civilian case or dispatch assignment. Versioning, validity and withdrawal. |
| Assistance Dispatch | `AssistanceRequest(requestId)` | `DispatchAssignment` | `CapabilityRequirements`, `PickupPoint`, `AssignmentState` | Open assistance, unit assignment history, acknowledgement, pickup, cancellation and completion |
| Assistance Dispatch | `ResponseUnit(unitId)` | — | `UnitCapabilities`, `AvailabilityState`, `TravelMode` | Unit capability and readiness for dispatch |

A `SituationSnapshot` is a versioned read model, assembled from infrastructure, hazards and population observations or estimates. It exposes source coverage and freshness. A `SafePoint` is a location eligible as an exit destination for a specific proposal, with evidence from the situation snapshot. It has no capacity, placement or stay lifecycle in the MVP.

`RouteSubject` is a tagged reference: `CivilianCase(caseId)` or `ResponseAssignment(assignmentId)`. A civilian route has an exit leg. A response route has an approach leg and, when required by the task, an outbound leg. Responders use their own authorized access profile for approach. Civilian and assisted outbound legs must account for leaving an affected area safely under applicable restrictions. The routing domain service calculates candidates and live validation checks them before publication. A route binds estimated traversal windows to its evidence revision.

## Service contracts

Names below describe version-one contracts independently of the transport. Queries return a result or an explicit pending, stale or unknown state. Commands carry an idempotency key. State-changing commands carry an expected version where concurrent edits can conflict.

| Provider or publisher | Contract | Main fields and outcomes |
| --- | --- | --- |
| Evacuation Management | `SubmitHazardAlert` command or client endpoint | `clientAlertId`, source, time, reported hazard and area. Returns `alertId`, `incidentId` and a situation snapshot or an explicit pending status with a query reference. Repeating the same client alert returns the same identity. |
| Situation Data | `GetSituation` query | `incidentId`, optional area of interest, authorized view. Returns `snapshotId`, source versions and times, infrastructure, hazard layers, population coverage and known gaps. |
| Situation Data | `GetRoutingSnapshot`, `ValidateRoute` queries and `SituationChanged.v1` event | Validation takes segment traversal windows, destination, travel and access profiles, and a snapshot reference. Returns validity, conflicts, and evidence revisions checked. A change declares affected segments or area, effective interval, source and revision. |
| Evacuation Routing | `ProposeRoute`, `PublishRoute`, `WithdrawRoute` commands | Carry `RouteSubject`, origin or pickup, travel mode and needs. A proposal returns typed legs and current evidence or a reason such as `NoEligibleExit`. Publication checks approval, the expected route version, and latest relevant evidence. |
| Evacuation Routing | `RoutePublished.v1`, `RouteWithdrawn.v1` events | Carry subject reference, route version, validity and restricted route details or a withdrawal reason. Evacuation Management and Dispatch receive facts for their respective users. |
| Assistance Dispatch | `RequestAssistance`, `AssignUnit`, `ReportAssignmentProgress` commands | Carry case or request ID, pickup, capabilities, and the relevant route proposal or assignment version. An assignment rejects conflicts such as `UnitUnavailable`, `RequestAlreadyAssigned` or `RouteIneligible`. |
| Assistance Dispatch | `AssignmentChanged.v1` event | Carries request and assignment IDs, unit reference, task state, subject version and time of report. Acknowledgement, pickup and completion are distinct values. |
| Evacuation Management | `ReportEvacuationProgress` command and `CaseProgressChanged.v1` event | Carry case ID, report type, source, observed time and instruction version if relevant. Allows an attributable exit confirmation or explicit correction. Location reports can update the monitor without confirming exit. |

For example, a closure message can describe a restriction without disclosing sensitive operational details.

```json
{
  "eventId": "evt-983",
  "type": "SituationChanged.v1",
  "producer": "situation-data",
  "subjectId": "segment-bridge-2",
  "subjectVersion": 7,
  "occurredAt": "2026-11-27T19:32:00Z",
  "correlationId": "incident-42",
  "causationId": "closure-report-12",
  "payload": {
    "affectedSegmentIds": ["segment-bridge-2"],
    "restriction": "closed",
    "evidenceStatus": "confirmed",
    "effectiveFrom": "2026-11-27T19:30:00Z"
  }
}
```

Events carry stable IDs, subject revisions, occurrence times, and correlation and causation IDs. Use UTC and half-open time ranges. Publish through a transactional outbox. Consumers deduplicate by event ID and use subject revisions or reconcile with the authoritative owner when updates arrive out of order. A command timeout leaves its outcome unknown until the caller retries with the same key or reconciles via the owner. Projections and delivery retry logic preserve withdrawals and suppress stale route versions. For the design conventions, see [tactical DDD](https://learn.microsoft.com/en-us/azure/architecture/microservices/model/tactical-ddd) and [transactional outbox guidance](https://learn.microsoft.com/en-us/dotnet/architecture/microservices/multi-container-microservice-net-applications/subscribe-events).

## Consistency in the MVP workflows

- **Alert and situation.** Persist the alert and its incident reference before requesting the situation view. If an input is unavailable or the view is still building, return the alert identity together with the pending or partial status. An unconfirmed alert remains visible as a report.
- **Dispatch.** Reserve a proposed assignment ID for route evaluation. Before assigning the unit, check its capabilities, availability and route eligibility. Assistance Dispatch commits the request assignment and unit booking under local concurrency checks. Enforce uniqueness of active assignments per request and per unit, for example with database constraints in Dispatch. After booking, publish the unit route under the dispatcher's authority and send the task. If publication fails or the route becomes invalid, keep the booked task visibly pending review. The dispatcher can obtain a replacement route or cancel by an explicit transition.
- **Monitoring and rerouting.** Maintain an index of valid instructions by segment and affected area. When Situation Data reports a change, revalidate affected instructions and withdraw those now in conflict. Publish replacements using the expected route version. Revalidate current constraints again before publishing as event propagation can lag. If a required check cannot be completed, keep the replacement pending and show the operator what needs verification. Withdrawal reaches offline users when contact is restored or via field communication.
- **Progress.** A dashboard combines case progress, route state and dispatch status through projections. It shows the latest known data and the time it was observed. Explicit operator or verified self-reports confirm physical progress. The same immutable report ID cannot advance the case twice.

See the [MVP domain invariants](domain-invariants.md).
