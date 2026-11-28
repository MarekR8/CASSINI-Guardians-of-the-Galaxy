# SH-T01 Assembly points and shelter resources

- **Owner:** __________
- **Directories:** `src/Situation/AssemblyPoints/`, `src/Sheltering/Catalog/`, `data/demo/sheltering/`
- **Technologies:** C#, ASP.NET Core, local fixtures, existing Situation snapshots
- **Baseline:** [01 Copernicus hazard layers](../01-copernicus-hazard-layers.md) and [02 road network and closures](../02-road-network-flypix-closures.md).

## What the demo shows

The coordinator opens one assembly point and two shelters. A shelter operator records the usable places, accessible places and intake windows. A site that loses eligibility remains visible with evidence and cannot receive a new assignment.

## Scope

### Stage A (demo)

- [ ] Implement `SetAssemblyPoint` in Situation and `ConfigureShelter` in Sheltering, with ownership and expected versions. Record place, access, time windows and source evidence.
- [ ] Provide `GetAssemblyPoints` and `GetShelterAvailability` for SH-W1. Include previews of usable capacity, active holds, actual occupancy and the revision checked. SH-T04 and SH-T05 own the operations that change holds and occupancy.
- [ ] Seed two synthetic shelters and one assembly point tied to the shared `sample` graph. A point and shelter may share a location, with separate functions and counters.

### Stage B (completion)

- [ ] Persist site configuration and its audit history in the owning stores. Store `AssemblyPointChanged.v1` and `ShelterAvailabilityChanged.v1` through the owner outboxes and verify recovery with SH-T06.
- [ ] Enforce coordinator activation and site-scoped resource updates with authenticated roles.

## Dependencies and verification

Receive SH-W0 first. Deliver SH-W1 to SH-T03, SH-T04 and SH-T07. Capture a site configuration and availability example under `contracts/http/` and synthetic source references alongside the scenario. Verify that closed or unverified sites cannot become destinations, that assembly check-in cannot reserve a bed, and that an operating-window change preserves existing occupancy. Contributes to SH-A01, SH-A04 and SH-A06.

## Results

_(Fill in the checks, fixture IDs, evidence sources and any unverified limits.)_
