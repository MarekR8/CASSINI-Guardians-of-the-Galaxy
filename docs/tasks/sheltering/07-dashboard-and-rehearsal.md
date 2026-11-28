# SH-T07 Dashboard and offline rehearsal

- **Owner:** __________
- **Directories:** `apps/web/`, `src/Gateway/`, `scripts/offline_demo.py`, `tests/sheltering_integration.py`, `docs/offline-demo.md`
- **Technologies:** existing Gateway and dashboard, Docker Compose overlay, Python integration runner, browser tests in Stage B
- **Baseline:** [07 dashboard, map and demo script](../07-dashboard-map-demo.md).

## What the demo shows

The coordinator opens sites, sees separate household departures, reserves a common shelter and observes partial arrival. Closing the site shows a transfer or an unresolved case while people already admitted stay counted until departure.

## Scope

### Stage A (demo)

- [x] Add Gateway routes and dashboard views for site status, travel-party decisions, availability, admission and withdrawal. Distinguish verified exit, point check-in, reservation, published route and confirmed reception.
- [x] Provide an opt-in sheltering Compose overlay and launcher support. Keep the current sample and Valencia startup paths. Document the exact run and test commands after implementing them.
- [ ] Own the shared integration script, confirm fresh state for each run, and walk through the full matrix in the [guide](../../sheltering-implementation-guide.md#acceptance-checks). Run the rehearsal on prebuilt images without internet.

### Stage B (completion)

- [x] Automate the main browser journey with Playwright and add it to CI. Verify coordinator and shelter operator scopes with the Stage B identity configuration.
- [x] Verify that a disconnected or stale projection labels the observed revision and cannot show a revoked instruction as current.

## Dependencies and verification

Receive SH-W1, SH-W2, SH-W4 and SH-W5. Use synthetic API examples while integrating the owners' endpoints. Run `python scripts/offline_demo.py --scenario sample --test` for the existing baseline, then add the documented sheltering run. Own SH-A08 and record the result of every acceptance check.

## Results

Run commands and the panel description are in [docs/offline-demo.md](../../offline-demo.md#panel-schronienia-i-przydziały-sh-t07).

**Dashboard.** The card **Schronienia i przydziały** ([apps/web/js/panels/sheltering.js](../../../apps/web/js/panels/sheltering.js)) reads the owners through the existing Gateway proxy (`/api/{service}/…`). It needs no new Gateway route. It shows:

- site status and availability;
- reception counters per shelter;
- approved travel-party plans;
- one row per assignment with five separate facts:

| Fact | Owner | Shown as |
| --- | --- | --- |
| Verified exit | Management | from the case's progress |
| Point check-in | none | "brak właściciela danych" |
| Reservation | Sheltering allocation | the assignment's status |
| Published route | SH-T06 | "nieopublikowana · SH-T06" |
| Confirmed reception | Sheltering reception | the admission and departure state |

A withdrawn assignment reads "wycofana · czeka na zastępstwo". The coordinator can close a shelter. A shelter operator confirms an admission or a departure from the awaiting and present lists.

**Launcher and overlay.** `compose.sheltering.yaml` and `offline_demo.py --sheltering` (SH-T01) are unchanged and keep the `sample` and Valencia paths. `start_services` takes `shelter_operators=True` to enable the Stage B reception accounts. Their tokens are generated per state directory.

**Rehearsal** (`python tests/sheltering_rehearsal.py --browser --images`, Windows 11, local processes, 28 Nov 2026). Each suite ran on a new state directory.

| Check | Suites | Result |
| --- | --- | --- |
| SH-A01 | `destination_routing_integration.py` (now also a CI step) | Pass |
| SH-A02, SH-A03 | `grouping_integration.py` | Pass |
| SH-A04 | `sheltering_reservations_integration.py` | Pass |
| SH-A05 | `offline_demo.py --sheltering --test` (catalog, reception, restart) | Pass |
| SH-A06 | reservations and reception | Pass |
| SH-A07 | none | **Blocked**: route publication and event replay belong to SH-T06, which is not implemented |
| SH-A08 | `offline_demo.py --test` and the sheltering suites on local processes | Pass on processes. The image path (`--images`) was **not run**: no container engine here. |

The sample regression failed once in the rehearsal and passed on two reruns. `connectivity_integration.py` (task 6) compares measured wall-clock milliseconds, and the first terrestrial request took 93 ms, longer than the simulated IRIS² delay. That timing assertion is flaky on a loaded machine. It is left to the task 6 owner.

**Stage B.** `tests/sheltering_browser.py` (Playwright 1.49.1, headless Chromium) is the CI job `verify-sheltering-browser`. It replays the SH-T04 exercise and restarts on the same state with the reception accounts. Then it checks in the dashboard:

1. The five facts stay separate for every assignment, and the withdrawn one never reads as reserved.
2. Without a token, reception is refused, not guessed.
3. The coordinator sees both shelters and gets `RoleNotPermitted` when reporting an admission.
4. With the Sheltering API cut off, the panel labels the last consistent read and its revision. The reservation reads "ostatnio … (niepotwierdzone)" and no action is offered.
5. The operator of shelter-a sees only shelter-a.
6. The operator of shelter-b admits case-15 and sees it counted once. The row then shows `Admitted`.

Screenshots are written to `artifacts/sheltering-browser/` and kept by CI.

**Limits.**

- SH-A07 waits for SH-T06.
- No owner records a point check-in yet.
- The Stage B accounts are Sheltering's exercise tokens, not Keycloak users, and the two are not combined.
- The prebuilt-image rehearsal must still be run on a machine with Docker.
