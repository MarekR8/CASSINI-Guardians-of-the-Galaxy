# SH-T07 Dashboard and offline rehearsal

- **Owner:** __________
- **Directories:** `apps/web/`, `src/Gateway/`, `scripts/offline_demo.py`, `tests/sheltering_integration.py`, `docs/offline-demo.md`
- **Technologies:** existing Gateway and dashboard, Docker Compose overlay, Python integration runner, browser tests in Stage B
- **Baseline:** [07 dashboard, map and demo script](../07-dashboard-map-demo.md).

## What the demo shows

The coordinator opens sites, sees separate household departures, reserves a common shelter and observes partial arrival. Closing the site shows a transfer or an unresolved case while people already admitted stay counted until departure.

## Scope

### Stage A (demo)

- [ ] Add Gateway routes and dashboard views for site status, travel-party decisions, availability, admission and withdrawal. Distinguish verified exit, point check-in, reservation, published route and confirmed reception.
- [ ] Provide an opt-in sheltering Compose overlay and launcher support. Keep the current sample and Valencia startup paths. Document the exact run and test commands after implementing them.
- [ ] Own the shared integration script, confirm fresh state for each run, and walk through the full matrix in the [guide](../../sheltering-implementation-guide.md#acceptance-checks). Run the rehearsal on prebuilt images without internet.

### Stage B (completion)

- [ ] Automate the main browser journey with Playwright and add it to CI. Verify coordinator and shelter operator scopes with the Stage B identity configuration.
- [ ] Verify that a disconnected or stale projection labels the observed revision and cannot show a revoked instruction as current.

## Dependencies and verification

Receive SH-W1, SH-W2, SH-W4 and SH-W5. Use synthetic API examples while integrating the owners' endpoints. Run `python scripts/offline_demo.py --scenario sample --test` for the existing baseline, then add the documented sheltering run. Own SH-A08 and record the result of every acceptance check.

## Results

_(Fill in run commands, environment, acceptance outcomes and rehearsal blockers.)_
