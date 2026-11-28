# SH-T05 Admissions and departures

- **Owner:** __________
- **Directories:** `src/Sheltering/`, `contracts/http/sheltering/`, `contracts/events/`
- **Technologies:** C#, owning-store transaction and repeatable arrival/departure fixtures
- **Baseline:** [05 Android progress evidence](../05-android-galileo.md) and [06 Management progress](../06-management-events-connectivity.md).

## What the demo shows

One person from a reserved household arrives first. Only their hold becomes occupied. Replaying the arrival leaves the figures unchanged. A later confirmed departure frees that place.

## Scope

### Stage A (demo)

- [ ] Implement `ConfirmShelterAdmission` and `ConfirmShelterDeparture` in Sheltering. Record the shelter, case, report ID and observation time.
- [ ] Support partial arrivals, eligible walk-ins and explicit departure. Reconcile any existing active hold before counting a walk-in. Keep reserved and occupied quantities distinct.
- [ ] Provide SH-W5 payloads and owner queries with separate counters for awaiting admission, confirmed admissions and departures. A route receipt, positioning update or pickup cannot count as shelter admission.

### Stage B (completion)

- [ ] Persist admission and departure events together with their state. Verify duplicate report handling across restarts and authorization for the operator's assigned shelter.
- [ ] Reconcile a person moving between shelters as two legs, preserving original occupancy until the actual departure.

## Dependencies and verification

Receive SH-W4 from SH-T04 and agree with that owner on shared command transaction helpers. Deliver the admission and departure part of SH-W5 to SH-T07. Publish example event payloads. Cover SH-A05 and the admitted-person branch of SH-A06.

## Results

_(Fill in the partial arrival, duplicate report, walk-in and departure checks.)_
