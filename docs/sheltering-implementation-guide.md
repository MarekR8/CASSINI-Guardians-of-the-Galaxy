# Sheltering and Grouping implementation guide

Extend the existing evacuation demo with assembly points, relationship-based grouping and shelter allocation. Follow [SH-01 through SH-06](domain/business-processes.md#detailed-sheltering-and-grouping-workflows) and the [domain invariants](domain/domain-invariants.md). The [architecture](domain-architecture.md#sheltering-and-grouping-extension) defines who owns each write. The [command and event catalog](mvp-commands-events.md#sheltering-and-grouping-extension) defines the proposed contracts and state transitions. This guide follows the structure of the [first MVP guide](mvp-implementation-guide.md), using the repository's current offline launcher and Gateway for Stage A. The [team tasks](tasks/sheltering/README.md) divide the work and list the integration handoffs.

## Deliverables and scope

- **Stage A: offline exercise.** Extend Situation Data with assembly points and host Grouping in Management as a separate module. Add a Sheltering service, a coordinator and shelter operator walkthrough, deterministic fixture data and integration checks.
- **Stage B: deployment and recovery.** Add durable database and messaging support, versioned external contracts, role-based sign-in and recovery tests. Consider deploying Grouping independently only if it needs an independent lifecycle or operational boundary.

The exercise uses two synthetic shelters and one assembly point on the `sample` routing graph. Keep the resident's first leg independent of an unconfirmed shelter reservation. Sheltering ranks feasible destinations and reserves verified resources. Routing still owns the travel instruction and publishes it only after checking current eligibility. Grouping provides travel parties and care constraints. No satellite link, provider key or connection to a live data source is required to replay the exercise.

## Bootstrap the repository

| Path | Work to implement |
| --- | --- |
| `src/Management/Grouping/` | Give the module its own aggregate, command/query handlers, persistence interface and DTOs. Read case identifiers and relevant needs through a Management contract. |
| `src/Situation/AssemblyPoints/` | Add the AssemblyPoint aggregate and publish versioned access and status evidence. Keep the existing hazard and network handlers. |
| `src/Routing/` | Add destination eligibility for an assembly point or shelter through the application layer. Reuse existing routing evidence, route proposal and publication contracts. Preserve exit-only routing for the current demo. |
| `src/Sheltering/`, `Cassini.Offline.slnx` | Add the .NET service and register it in the current solution. Keep aggregate logic in the domain, transactions in application handlers and transport in adapters. |
| `src/Gateway/`, `apps/web/` | Expose Sheltering through the existing Gateway. Start with grouping reads and commands behind the Management entry point, while keeping an independent module contract. Extend the current dashboard. |
| `contracts/http/`, `contracts/events/` | Save proposed version-one payloads, error cases and stable sample IDs. Reuse the shared envelope rather than introducing a second event format. |
| `data/demo/sheltering/`, `tests/sheltering_integration.py` | Store the shared scenario, minimal cross-service fixtures and expected outcomes. Run the integration test against an isolated instance. |
| `compose.sheltering.yaml`, `scripts/offline_demo.py` | Add opt-in startup and verification of the sheltering slice. Keep the existing sample and Valencia startup paths available. |

Propose and review the first DTOs before implementing compatibility changes. Carry `incidentId`, `caseId`, `legId` and `shelterId` as references. Use expected versions for changed records, memoize completed command receipts under `commandId`, and use an injected clock for time windows. Candidate queries do not mutate state.

## Local configuration and startup

| Setting | Stage A | Stage B |
| --- | --- | --- |
| Runtime | Use the current ASP.NET Gateway on loopback port 5080. Run the added service on a private endpoint and opt in with a Compose overlay or an explicit launcher option. | Add authenticated service endpoints and retain the same owning-service boundaries. |
| State | Use a separate, restart-safe local store for Sheltering. Persist affected shelters, assignments, receipts and outbox entries in one atomic commit. One writer instance is enough for the exercise. | Use an owned PostgreSQL database with migrations, transaction and version checks, and uniqueness for one assignment per case and leg. |
| Routing | Use a deterministic graph and recorded eligibility evidence. A route to a shelter remains a candidate until the reservation and route checks succeed. | Exercise optional provider adapters separately. Preserve identical validation at publication for graph and provider candidates. |
| Events | Use persisted outbox entries and replay fixtures or local reconciliation. Verify handlers without a live broker. | Connect the outbox to RabbitMQ in the optional `full` profile. Add consumer deduplication and retry handling. |
| Readiness and secrets | Expose local readiness for the new service. Store the exercise under its own state directory or Compose project. | Keep DB, broker and identity credentials in environment or ignored configuration. Use the same role contract in the application and UI. |

The existing baseline uses `python scripts/offline_demo.py --scenario sample --test` and `docker compose config --quiet`. The Stage A tasks add a dedicated sheltering run and test entry point. Document their exact commands when available. Build or load images before the offline rehearsal, and use a fresh scenario state for each run.

## Identity and operational views

| Role | View and actions |
| --- | --- |
| Coordinator | Activate sites, review group decisions, approve reservations and transfers, and publish the first leg or destination change through Routing. See blocked decisions and the evidence behind them. |
| Field operator | Maintain incident-scoped group membership and record approved care handovers with evidence. See cases that require assisted evacuation. |
| Shelter operator | Update permitted resources and report actual admissions or departures for assigned sites. See confirmed occupancy, reserved quantities and expected intake separately. |
| Resident | See their current approved route and whether shelter allocation is pending. A reservation, a published instruction and an actual arrival remain distinct. |

Use synthetic roles in Stage A and mark the exercise accordingly. Stage B enforces authenticated incident and site scope at the owning service. Keep household and care details out of public views and shared logs.

## Implementation order and handoffs

| Step | Implement | Ready for the next step when |
| --- | --- | --- |
| 0. Shared contract and fixture | Agree on stable case, leg, group, point and shelter IDs, a case graph and a synthetic clock. | The owners can resolve the same IDs across the fixture and example payloads. |
| 1. Sites and groups | Implement point/shelter availability and versioned group plans as independent work. | Candidate planning can read valid sites and care constraints, including explicit unknown or review states. |
| 2. Eligible destinations | Bind candidate routes to current evidence, support a safe first leg and rank feasible shelters. | A missing shelter does not suppress an eligible first leg. An infeasible option returns a reason. |
| 3. Reservation and intake | Reserve all required quantities, implement transfer and withdraw invalid assignments. Admit and release people on explicit reports. | Concurrent requests cannot claim the same final place. A partial arrival changes only the arriving person's resources. |
| 4. Publication and reconciliation | Connect committed destination decisions to existing Routing publication and withdrawal. | Retry, stale messages and a route-publication failure cannot make an outdated instruction current. |
| 5. Dashboard and rehearsal | Connect map and counters, draft the runbook and verify the full exercise. | A clean offline run shows grouping, allocation, partial arrival and a closure with a replacement or explicit unresolved state. |

A destination change updates all affected assignments, the new reservation and any releasable old hold in one Sheltering transaction. An occupied place remains charged until confirmed departure. Route publication crosses a service boundary after the booking and may remain pending. If it fails, recheck current route and reservation state before retrying or withdrawing. A shared-destination preference never overrides access, clearance time or required care.

## Acceptance checks

| ID | Exercise | Expected outcome |
| --- | --- | --- |
| SH-A01 | Fill both shelters; evaluate a feasible first leg and then an infeasible one. | The first can be issued to a verified point with shelter pending. The second is escalated. |
| SH-A02 | Give a household separate origins; then make a shared destination infeasible. | Members can travel independently to a shared shelter when feasible. Otherwise the system records the split and reason. |
| SH-A03 | Propose separating a person from a required caregiver. | Rejected without a valid handover. An authorized handover with effective evidence permits re-evaluation. |
| SH-A04 | Run simultaneous requests for one remaining place; separately exhaust a required resource and an intake window. | Only one request takes the final place. Other limits reject the affected batch without leaving partial holds. |
| SH-A05 | Admit one member of a reserved group, replay the report and then confirm departure. | Convert only their hold to occupancy, count the admission once and free the occupied resources once. |
| SH-A06 | Close the assigned shelter or its approach while people are en route; exercise a replacement and no replacement. | Transfer unused holds on success. Otherwise show pending reassignment and withdraw invalid guidance. Existing occupancy remains until actual departure. |
| SH-A07 | Fail route publication after booking, then replay an older or duplicate event. | The current reservation and instruction reconcile without creating a duplicate or restoring withdrawn guidance. |
| SH-A08 | Run the sample regression and the sheltering exercise on prebuilt images without internet. | Both runs use local fixtures and produce consistent results from fresh state. |

CI should run the existing baseline before the new sheltering integration checks. Capture service logs with correlation, command or event IDs and reason codes. Confirm new checks against the documented aggregate transitions. Stage B adds database restart and broker-redelivery tests and verifies role boundaries with distinct accounts. The team records actual outcomes and limits in each task's Results section.
