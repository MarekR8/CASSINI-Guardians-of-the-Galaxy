# SH-W2 · Grouping HTTP v1

Management hosts these operations under `/api/management` through the Gateway. Use the incident, group and leg identifiers as opaque references. The examples use `incident-42`, `group-household` and `leg-shelter`. The synthetic evidence uses the same exercise clock and `shelter-a`/`shelter-b` identifiers as the SH-T01 sample fixture.

## Reads

| Method and path | Parameters and result |
| --- | --- |
| `GET /queries/evacuation-groups/{groupId}` | `incidentId`. Returns `groupRevision`, case references with current node, mobility and case version, typed links and confirmed handover references. Routing can use this before it computes travel candidates. |
| `GET /queries/travel-party-proposals/{groupId}` | `incidentId`, `legId`, `evidenceId`. Returns a `Proposed` or `ReviewRequired` result with a reason. The query has no write effects. |
| `GET /queries/approved-travel-plans/{groupId}` | `incidentId`. Returns the latest approved revision with its current review state. Before the first approval it returns `404 PlanNotFound`. |

A proposal includes `groupRevision`, `legId`, case versions, clearance deadlines, `requiredCare`, `sharedDestinationPreferences`, and the evidence ID, source, validity window and Situation revision. Each party includes its members, route reference, destination kind and ID, departure, hazard clearance and arrival times. `required-care` requires each dependent and their current caregiver in the same party. `household` and `institution` express a preference to share a shelter.

Grouping checks recorded eligibility, mobility and clearance deadlines, including time spent waiting to depart. It prefers travelling together when feasible. For split departures it maximizes shared-shelter preferences kept, then minimizes the party count and the sum of arrival times relative to the exercise clock. Route IDs break ties. `SharedShelterNotFeasible` means the evaluated route options leave at least one preference unmet. Sheltering checks current site availability, resources and intake and owns reservations. Routing validates live evidence before publishing any instruction.

## Commands

| POST path | Additional inputs | Result |
| --- | --- | --- |
| `/commands/register-evacuation-group` | `caseIds`, `relationships`, `evidenceRef` | Group revision 1. For Stage A a case belongs to at most one group in the incident. |
| `/commands/update-group-composition` | `expectedGroupRevision`, complete `caseIds` and `relationships`, `evidenceRef` | Next group revision. An existing required-care link remains assigned through composition updates. |
| `/commands/record-care-handover` | `expectedGroupRevision`, `handover` with dependent, outgoing/incoming caregiver, effective time, authorizing operator, role and confirmation evidence | Next group revision with the confirmed replacement caregiver. |
| `/commands/approve-travel-plan` | `expectedGroupRevision`, `expectedPlanRevision` (0 before the first approval), `legId`, `evidenceId` | Re-evaluates the same evidence with current cases and returns the next `planRevision` with the approved proposal. |
| `/commands/flag-travel-plan-for-review` | `planRevision`, `reason`, `evidenceRef` | Latest plan becomes `ReviewRequired` if the requested revision still matches. Further evaluations for this group and leg require new route evidence. |

All commands take `commandId`, `incidentId` and `groupId`. Retry a successful command with the same ID and body to get the saved result. Reusing its ID with different content or writing against a stale revision returns `409`. An infeasible proposal cannot be approved (`422`). Composition, handover and referenced case-version changes persist a review flag on the affected current revision. Route invalidation uses the `flag-travel-plan-for-review` command with the affected `planRevision`. A delayed invalidation for a superseded revision returns `409`. Consumers should check the current plan revision, status and `proposal.legId` before allocating resources or publishing instructions.

## Incident scope and events

Set `Grouping__OperatorsFile` on Management to enable scoped bearer authentication. Store the file outside Git. Its top-level `operators` array contains `operatorId`, `incidentId`, `role` and `tokenSha256` (the hex SHA-256 of a randomly generated bearer token). The Gateway forwards the bearer on Grouping requests. Missing or invalid credentials return `401`. A valid caller outside the requested incident or role returns `403`. An incident that Management does not hold returns `404` before a command receipt is consulted.

| Role | Access in the assigned incident |
| --- | --- |
| `coordinator` | Grouping reads and commands |
| `field-operator` | Grouping reads and confirmed care handovers |
| `reader` | Grouping reads for planner and service accounts |

Management records the authenticated operator and role for handovers. Scoped reads expose the linked case IDs and evidence references. Routing and Sheltering can supply a reader credential via `Grouping__ReadToken` in their own service configuration.

Management commits the group, plan, command receipt and [Grouping events](../../events/README.md) in the same file replacement or PostgreSQL transaction. The existing outbox publishes to the in-memory bus offline or RabbitMQ in the full profile. Events include opaque group, plan and evidence references, without operator identities or care relationships.

## Replay

```powershell
python tests/grouping_integration.py
```

To make manual requests against the local sample through the Gateway, opt in with the evidence file and a fresh state directory:

```powershell
$env:Grouping__EvidenceFile = (Resolve-Path data/demo/sheltering/sample-grouping.json).Path
python scripts/offline_demo.py --scenario sample --port 5180 --data artifacts/grouping-demo
```

POST the `register` and `approve` objects from [household-split.json](household-split.json) to see independent departures to the same shelter. Query again with `evidenceId=household-separated` to see the effect of the recorded closure. The integration test exercises the second approval, then applies [care-handover.json](care-handover.json) and checks both rejection before the transfer and approval afterward.

The evidence file supplies a fixed synthetic clock, recorded route alternatives and the candidate-party combinations to compare. With only `Grouping__EvidenceFile` configured, the local exercise uses synthetic roles and the in-memory bus. The integration test generates temporary operator credentials and exercises incident and role enforcement separately. Integration with live Routing evidence and downstream publication remains part of the shared handoff.
