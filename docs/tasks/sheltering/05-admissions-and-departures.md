# SH-T05 Admissions and departures

- **Owner:** __________
- **Directories:** `src/Sheltering/`, `contracts/http/sheltering/`, `contracts/events/`
- **Technologies:** C#, owning-store transaction and repeatable arrival/departure fixtures
- **Baseline:** [05 Android progress evidence](../05-android-galileo.md) and [06 Management progress](../06-management-events-connectivity.md).

## What the demo shows

One person from a reserved household arrives first. Only their hold becomes occupied. Replaying the arrival leaves the figures unchanged. A later confirmed departure frees that place.

## Scope

### Stage A (demo)

- [x] Implement `ConfirmShelterAdmission` and `ConfirmShelterDeparture` in Sheltering. Record the shelter, case, report ID and observation time.
- [x] Support partial arrivals, eligible walk-ins and explicit departure. Reconcile any existing active hold before counting a walk-in. Keep reserved and occupied quantities distinct.
- [x] Provide SH-W5 payloads and owner queries with separate counters for awaiting admission, confirmed admissions and departures. A route receipt, positioning update or pickup cannot count as shelter admission.

### Stage B (completion)

- [x] Persist admission and departure events together with their state. Verify duplicate report handling across restarts and authorization for the operator's assigned shelter.
- [x] Reconcile a person moving between shelters as two legs, preserving original occupancy until the actual departure.

## Dependencies and verification

Receive SH-W4 from SH-T04 and agree with that owner on shared command transaction helpers. Deliver the admission and departure part of SH-W5 to SH-T07. Publish example event payloads. Cover SH-A05 and the admitted-person branch of SH-A06.

## Results

Code is in [src/Sheltering/Reception](../../../src/Sheltering/Reception/). The contract and error codes are in [contracts/http/sheltering](../../../contracts/http/sheltering/README.md#admissions-and-departures-sh-t05). Admissions, holds, report receipts and outbox events live in each shelter's record. One store transaction changes them together with the commitments. A walk-in that releases a hold at another shelter changes both shelters in that same transaction.

Holds are created by SH-T04 reservations. `tests/sheltering_reservations_integration.py` admits an SH-T04 hold. This test reads the holds from the synthetic SH-W4 fixture [sample-holds.json](../../../data/demo/sheltering/sample-holds.json), which is enabled with `Sheltering:HoldsFile`. The default `sample` catalog is unchanged.

```sh
python scripts/offline_demo.py --sheltering --test   # catalog tests, then tests/shelter_reception_integration.py with the holds, then after a restart
```

| Check (SH-A05, admitted branch of SH-A06) | Result |
| --- | --- |
| Partial arrival: case-14 of the reserved household arrives | Only that hold becomes occupied (held 3→2, occupied 2→3, free places unchanged). case-15 stays awaiting. |
| Same report retried with the same or a new `commandId`, or with a stale version | Original result. Counters unchanged. Changed content under the same `reportId` gives `409 ReportConflict`. |
| New report for someone already present | `AlreadyAdmitted`, no change. At another shelter: `409 AdmittedElsewhere`. |
| Walk-in with a hold at shelter-b | Admitted at shelter-a. The shelter-b hold is released in the same transaction. |
| Walk-in that does not fit (accessible place), without resources, or mixed with a valid arrival | `409`/`422`. Nothing changes. |
| Missed arrival window | case-15 is flagged as overdue and the hold is kept |
| Two operators report case-15 at once | One `200`, one `409 VersionConflict`. Admitted once. |
| Departure | Frees one place once. A retry returns the same result, a new report returns `AlreadyDeparted`, and a departure before admission gives `422`. |
| Restart | Admissions, departures, report receipts and the 5 outbox events persist. A replay after restart changes nothing and is still subject to the token check. |

**Stage B.** With `Sheltering:OperatorsFile`, the reception endpoints take authority from a bearer token, never from the request body. The file holds only SHA-256 hashes of the tokens. `scripts/offline_demo.py` generates random exercise tokens in the temporary state directory, so no credential is in Git. A transfer is the second leg of an SH-T04 assignment, opened while the person is still admitted at the first shelter.

| Stage B check | Result |
| --- | --- |
| No token, unknown token | `401 AuthenticationRequired` |
| Operator of shelter-b reports at shelter-a | `403 ShelterNotAssigned`, for admissions and departures |
| Coordinator reports an admission | `403 RoleNotPermitted`. The coordinator can read the whole incident. |
| Operator of another incident | `403 IncidentNotAssigned` |
| Operator reads reception | Sees only the assigned shelter. Another shelter gives `403`. |
| Rejected calls | Change no counter or version |
| case-14, present at shelter-a, reaches shelter-b on `leg-transfer` | Admitted at shelter-b with `transferFrom` (shelter-a admission). Shelter-a figures stay unchanged and count it under `transferredAwaitingDeparture`. |
| Re-admission at shelter-a on the transfer leg | `409 AdmittedElsewhere` |
| Departure from shelter-a | Frees that place once. The person stays present at shelter-b. The event at shelter-b carries `transferFromShelterId`. |

**Limitations.** Events and state are committed together in the shared JSON state file with an atomic replace. The SH-T04 Stage B PostgreSQL store is not available yet. The outbox is written but not published to the bus (SH-T06). Tokens are exercise accounts from a file, not an identity provider. Without `Sheltering:OperatorsFile`, the endpoints run open, as in Stage A, with synthetic roles. Allocation and catalog commands are not covered by these roles. A walk-in that does not fit is rejected, and recording the unmet need is left to the coordinator.
