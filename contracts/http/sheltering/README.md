# Sheltering HTTP examples

These examples use the [Sheltering and Grouping interfaces](../../../docs/sheltering-interfaces.md) and synthetic `sample` scenario. Read the current Situation revision before submitting a command with verified evidence. The examples assume a fresh exercise with Situation revision `0`. Read availability at `2026-11-27T19:20:00Z` to evaluate the seeded exercise period.

| Example | Request or response | Scope |
| --- | --- | --- |
| [Set assembly point](set-assembly-point.request.json) | Request to `POST /api/situation/commands/set-assembly-point`. | SH-T01 |
| [Configure shelter](configure-shelter.request.json) | Request to `POST /api/sheltering/commands/configure-shelter`. | SH-T01 |
| [Shelter availability](shelter-availability.response.json) | Illustrative response to `GET /api/sheltering/queries/shelter-availability?at=2026-11-27T19:20:00Z`. | SH-T01 |
| [Routing planning input](shelters.v1.example.json) | Flat nominal-capacity projection for the `cassini.shelters.v1` planning input. It is not a live availability response. | Contract alignment with Routing PR #11 |
| [Reserve a place](reserve-shelter-places.draft.json) | Proposed request to `POST /api/sheltering/commands/reserve-shelter-places`. | Draft for SH-T04 |
| [Confirm admission](confirm-shelter-admission.request.json) and [result](confirm-shelter-admission.response.json) | Request to and response from `POST /api/sheltering/commands/confirm-shelter-admission`. | SH-T05 |
| [Confirm departure](confirm-shelter-departure.request.json) | Request to `POST /api/sheltering/commands/confirm-shelter-departure`. The response has the same shape as an admission. | SH-T05 |
| [Shelter reception](shelter-reception.response.json) | Illustrative response to `GET /api/sheltering/queries/shelter-reception?shelterId=shelter-a&at=2026-11-27T19:51:00Z`. | SH-W5 (SH-T05) |

The command examples use separate IDs from the seeded catalog so they can create entries with `expectedVersion: 0`. Repeating either command with the same payload returns its original result. For a subsequent change, use a new command ID and the site's returned version.

### Admissions and departures (SH-T05)

These examples need the [hold fixture](../../../data/demo/sheltering/sample-holds.json), enabled with `Sheltering:HoldsFile`. The fixture stands in for SH-W4 until SH-T04 issues reservations. A hold is per case and leg. SH-T04 writes holds into the same shelter record (`reception.holds`).

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
