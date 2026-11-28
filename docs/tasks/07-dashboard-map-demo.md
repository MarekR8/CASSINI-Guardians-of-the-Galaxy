# Task 7 — Dashboard, map and demo script

**Owner:** __________
**Directories:** `apps/web/`, `src/Gateway/`, `tests/e2e/` (new), `docs/offline-demo.md`
**Technologies:** Leaflet (local GeoJSON, optional online OSM tiles), .NET Gateway, Playwright, Keycloak (Stage B)

## What the demo shows

One coordinator screen walks through the whole script from the [README](README.md) ("Demo script" section): a map with layers (EMS, Sentinel-1, FlyPix, roads, closures, routes, units, positions), a legend with source and simulation label, and the counters and metrics from the one-pager. Presentation mode steps through the script with a "Next" button.

## Technology concept

- **Leaflet**: replaces the current custom map. Offline without tiles (background from local GeoJSON or a small tile pack in the repo); online optionally OSM tiles with attribution. Serve the library locally (the Gateway CSP blocks CDNs).
- **Gateway**: still the single entry point; may host the link simulator from task 6.
- **Playwright**: one end-to-end scenario test on Chromium against `docker compose`.
- **Keycloak** (Stage B): roles `coordinator`, `dispatcher`, `responder`, `resident`; OIDC PKCE in the dashboard, `JwtBearer` in the services.

## Scope

### Stage A (demo)
- [x] Leaflet + layers from `GET /queries/snapshot`, `/network`, `/distribution`, `/assignments`, `/incident`. Each layer appears in the legend with its `provenance`. _Valencia reads snapshot, network, closed-roads, city-route and, in presentation mode, the task 03 evacuation-plan, distribution and draw; assignments and incident exist only in `sample`._
- [x] Styles: observed (EMS/S1) ≠ to be reviewed (FlyPix) ≠ closed ≠ current route ≠ withdrawn route.
- [x] Metrics panel: time from report to situation view, time to assign assistance, time from closure to withdrawal, number of people without a confirmed exit. _Partial: the four metrics work in `sample` only; in Valencia they show "niedostępne w tym scenariuszu" and Valencia-specific metrics are shown instead. Assignment time uses a session timestamp until Dispatch exposes `requestedAt`._
- [x] Presentation mode: a list of script steps, each calling existing commands (reset, alert, draw, assistance request, FlyPix detection, approval, link drop, EWSS, exit confirmation). _Partial: reset is an instruction (no reset command); link drop and EWSS wait for task 6; in Valencia alert, transport and exit confirmation wait for the Management and Dispatch owners. Distribution and draw run on the task 03 model since develop@af3aa72._
- [x] You run the dress rehearsal and update `docs/offline-demo.md` (presentation walkthrough).

### Stage B (completion)
- [x] Playwright scenario test in CI (`.github/workflows/offline-ci.yml`). _Job `e2e` runs `tests/e2e/specs/demo.spec.js` on `sample` against `docker compose up --wait`; `valencia.spec.js` runs locally with `E2E_SCENARIO=valencia`. Passed locally; the GitHub run is pending the first push._
- [x] Keycloak in the `full` profile + dashboard sign-in; per-role views. _Verified with Keycloak 26.8.2 in Docker on `sample`: sign-in for all four roles, 401 without a token, 403 for forbidden commands, 0 CSP violations._
- [ ] Optional dashboard migration to React + TypeScript + Vite per the stack. _Skipped by decision; the dashboard stays on plain ES modules._

## Dependencies

- You wait for **W6**: Stage A of tasks 1, 2, 4, 6 (until then draw from mock files in `data/demo/`), W1 (map extent), W3 (events instead of polling, Stage B).
- You own the Gateway: agree the link simulator location with person 6.

## Where to find data

- Leaflet 1.9.x: `leafletjs.com/download.html`, download into `apps/web/vendor/`.
- Offline tiles: a small pack for the AOI (e.g. MBTiles export → PNG) only if the license allows; otherwise a background from road GeoJSON.
- Metrics and script: [project-one-pager.md](../project-one-pager.md), [offline-demo.md](../offline-demo.md).

## How to test

```sh
docker compose build && docker compose up -d --no-build --pull never   # dashboard: http://127.0.0.1:5080
python scripts/offline_demo.py --test
npx playwright test tests/e2e                                            # Stage B
```
- Manually, without internet (turn off Wi-Fi): all layers render, no CSP errors in the browser console.
- Check the three states from AGENTS.md: route available, bridge closed, no exit.
- Take screenshots of each step for the presentation.

## Where to save results

- Screenshots and the rehearsal recording: outside Git (team folder). In the repo only the updated `docs/offline-demo.md`.
- Section below: metrics measured during the rehearsal, list of issues and the owner of each.

## Results

Dress rehearsal on 2026-11-28, Valencia only. Warsaw and Kłodzko are no longer presented; `sample` (Kłodzko) stays as the CI scenario.

**Update after rebasing onto develop@af3aa72.** The dashboard modules now also carry develop's changes: the integration preview (`#integration-preview`, `js/panels/integrations.js`), the optional shelter and assembly point catalog (`#site-catalog`, `site-catalog.js` wired by `js/panels/sites.js`, shown when `GET /features` reports `sheltering`), the Dispatch unit ranking (`unit-candidates`, in `js/panels/operations.js`), removal of Warsaw and the `/ui/companion` scenario switch, and the Valencia map limited to the Sentinel-1 scene with only the Copernicus EMS hazard layer. Presentation step 4 in Valencia now runs the task 03 model (`evacuation-plan`, `distribution`, `draw` with seed 1 at OSM node 10242238645, synthetic shelters) and draws the route; step 8 reports the draw status after closures. The link step (10) follows the task 6 contract below and stays blocked until task 6 merges. Re-verified: `offline_demo.py --test`, `--test --scenario valencia`, `--sheltering --test`, `tests/grouping_integration.py`, Routing and Android tests, both e2e specs (Docker `sample`, `offline_demo.py` Valencia) and a headless Edge check of both scenarios (0 console errors, 0 CSP violations, 0 external requests).

**Update after merging develop@be3f36a (task 6).** The presentation link step now runs against the Gateway link simulator: in `sample` it switches the profile to `down`, sends a field `management/alert` with `X-Cassini-Link: field` (answered `202 Queued`, a retry with the same `commandId` returns the same queue entry), switches to `iris2`, waits until `link-status` reports the entry `Delivered` over `iris2` and shows the Management alert count 1 → 2. In Valencia it switches profiles only, because Management and Dispatch have no Valencia cases. The EWSS step sends `management/ewss-alert` (`severe`, a bounding-box polygon of the exercise flood layer in `sample` and of the EMSR773 layer in Valencia) and shows `ewss-alerts` labelled SYMULACJA (`delivery: simulated`, `provenance.mode: mock`, no reception confirmed). Metric 4 uses Management `counters` (`unconfirmed` of `confirmedOutside + unconfirmed`, SYMULACJA badge for `confirmedOutsideSimulated`, GHSL estimate described as an estimate). The cases panel shows the exit fix mode, the map keeps node-level counts (INV-14) and counts only cases with `confirmedOutside`. `GatewayAuth` needs no new rule: `link-profile` and `ewss-alert` are coordinator-only by the default branch, `link-status` is a staff read, and field-relayed commands keep the per-role rules of `/api/{service}/commands/{op}` because the middleware runs before the relay.

### What was verified

| Check | Command or method | Outcome |
| --- | --- | --- |
| Compose build, Valencia | `docker compose -p <project> -f compose.yaml -f compose.valencia.yaml build` | All five images built; Gateway restored the JwtBearer package. |
| Compose run, Valencia | `up -d --no-build --pull never`, `CASSINI_PORT` set to a free port | Five containers up; snapshot revision 0 with EMSR773; network `scenario: valencia`, 247,875 OSM edges; `/auth-config.json` = `enabled:false`. |
| Compose config | `docker compose -f compose.yaml -f compose.valencia.yaml config --quiet`; `docker compose --profile full config --quiet` with dummy passwords | Both valid. |
| Offline browser | Headless Edge (Playwright) with every non-localhost request aborted | 0 external requests, 0 CSP violations, 0 console or page errors. Before the task 6 merge the only expected error was the 404 of `GET /api/link/queries/link-status`; after the merge there are none. |
| Presentation mode | All 13 Valencia steps (0–12) with **Dalej**, one screenshot per step | 7 steps done, 6 blocked with the owner shown, 0 errors (table below). After the develop update: 8 done, 5 blocked. |
| Three states | Steps 3, 7–9 | Route available (18 min 56 s); closure → detour (19 min 02 s, the old route is replaced); no route ("Brak trasy w rewizji 4") as a separate state. |
| Stale data | Situation container stopped for 9 s, then restarted | Legend and metrics show "Dane nieaktualne" with the last reading; the view recovers after restart. |
| Integration, sample | `python scripts/offline_demo.py --test` | `Offline integration passed: gateway, alert retry, weighted routes, withdrawal, dispatch booking and pickup, blocked exits, local import.` |
| Integration, Valencia | `python scripts/offline_demo.py --test --scenario valencia` | `Valencia integration passed: 247,875 edges, classifications, evidence review, detour and no route.` |
| After the task 6 merge: integration | `offline_demo.py --test` (with `connectivity_integration.py`), `--test --scenario valencia`, `--sheltering --test`, `tests/grouping_integration.py`, `tests/dispatch_ors_integration.py` | All passed. Measured link transit (total / simulated ms): terrestrial 33/10, iris2 80/40, govsatcom 619/600. |
| After the task 6 merge: .NET tests | `dotnet run` for Gateway, Management, Messaging (in-memory), Routing and Android tests | All passed. |
| After the task 6 merge: Compose | `config --quiet` for the default stack, `+ compose.sheltering.yaml`, `--profile full` and `-f compose.management-full.yaml --profile full` with dummy passwords; `up --dry-run ... gateway` | All valid; the gateway-only start of the management overlay does not start Keycloak. The overlay was not started on Docker. |
| After the task 6 merge: e2e | `demo.spec.js` against `docker compose -p cassini-m6` (port 5790); `valencia.spec.js` against `offline_demo.py --scenario valencia --port 5880` | Both passed; link and EWSS `done` in both scenarios, replay of `link` and `ewss` changes nothing. |
| After the task 6 merge: browser | Headless Edge through all presentation steps of both scenarios | `sample`: 11/11 done; Valencia: 10 done, 3 blocked; 0 console errors, 0 CSP violations, 0 external requests. |
| After the task 6 merge: Keycloak | Four roles on the `full` profile (port 5680/5689) | `link-profile` and `ewss-alert`: 403 for dispatcher, responder and resident; `link-status`: 200 for staff, 403 for resident; field-relayed `management/progress`: 403 for dispatcher and resident, allowed for responder and coordinator. |

Presentation run on a fresh stack:

| # | Step | Result |
| --- | --- | --- |
| 0 | Fresh state | Revision 0, no closures. |
| 1 | Situation picture | EMSR773_AOI01_DEL_PRODUCT_v1 · copernicus-ems-archive · recorded · 2024-10-31T18:02Z; nodes: 21,833 in the hazard zone, 155,763 outside. |
| 2 | Alert | Blocked: Management has no Valencia support. |
| 3 | City route | 18 min 56 s, 50 segments, revision 0. |
| 4 | Distribution and draw | Rehearsal: blocked. Since develop@af3aa72: task 03 model, node 10242238645 (profile `independent`, wave 3, deadline 43 min), shelters C 37 % / F 12 % / G 51 % (synthetic), draw with seed 1, route drawn from OSM nodes. |
| 5 | Transport | Blocked: Dispatch has no Valencia support (task 4). |
| 6 | FlyPix detection (SYMULACJA) | Pending review, revision 1; 6 OSM segments, 4 on the route. |
| 7 | Approval | 6 closed roads, revision 2. |
| 8 | Recalculation | Detour 19 min 02 s (was 18 min 56 s), 0 closed segments on the new route. |
| 9 | Start cut off (SYMULACJA) | No route; 3,461 more segments closed, revision 4. |
| 10 | Link drop → IRIS² | Rehearsal: blocked. After the task 6 merge: profile `down` → `iris2`, status from `link-status` (RTT 40 ms, queue 0); no field command in Valencia. |
| 11 | EWSS | Rehearsal: blocked. After the task 6 merge: `ewss-1` (severe), zone `-0.62, 39.24, -0.2622, 39.5929` from the EMSR773 bounds, SYMULACJA (mock), no reception confirmed. |
| 12 | Exit confirmation | Blocked: no Valencia cases in Management; Galileo phone is task 5. |

Each executed step took 0.15–1.2 s. The page was ready 0.5 s after load.

### Metrics measured in the rehearsal

| Metric | Value | Source |
| --- | --- | --- |
| Report → situation view | not available in Valencia | Management has no Valencia alerts |
| Assistance assignment time | not available in Valencia | Dispatch has no Valencia tasks |
| Closure → route withdrawal | not available in Valencia | no unit routes; the evacuation draw status is shown in presentation step 8 |
| People without a confirmed exit | not available in Valencia | Management has no Valencia cases |
| EMS observation → decision | 757 days | service; archive replayed, the gap includes the time since the 2024 observation |
| Imagery → decision | 757 days | service; synthetic detection `observedAt` 2024-10-31T18:03Z |
| Decision → closure shown in the panel | 0.6 s | service `decidedAt` → session |
| Closure → route recalculation | 0.2 s | session |
| Nodes in the hazard zone | 21,833 of 177,596 | service (Routing `zoneCounts`) |
| Population estimate in the zone | not available | EMSR773 has no population estimate |

### Data used

- Copernicus EMS EMSR773, AOI 01, delineation v1 (`EMSR773_AOI01_DEL_PRODUCT_v1`), observations 2024-10-30T10:37Z, 2024-10-31T10:51Z and 2024-10-31T18:02Z; prepared locally in `artifacts/valencia-city/` (not in Git).
- Sentinel-1 VV pair: 2024-10-19 (before) and 2024-10-31 (event), from `data/demo/valencia/imagery/`.
- OSM extract of Valencia (Geofabrik), graph timestamp 2026-11-27T21:21:21Z, 247,875 edges.
- Synthetic FlyPix fixture `apps/web/data/presentation/valencia-review.synthetic.json`, labelled SYMULACJA (`mode: synthetic`, `imageProductId: presentation-synthetic-fixture`, `observedAt: 2024-10-31T18:03:00Z`): detection `crossing` (6 segments) and `isolation` (3,461 segments), plus the test route points. It is not a FlyPix observation.
- No resident data, credentials or private operational layers. Screenshots are kept outside Git.

### Issues and owners

| Issue | Owner |
| --- | --- |
| No Valencia support in Management (alerts, cases); alerts have only `observedAt`, no `receivedAt`. Exit time is now available as `exit.observedAt` of the position fix. | Management |
| Valencia distribution and draw are available (task 03). The draw returns only OSM node IDs, so the dashboard reads `city-node/{id}` per node to draw the route; a geometry field would save ~70 requests. `DrawStatus` has no `withdrawnAt`. | Routing (task 3) |
| No Valencia support in Dispatch; `AssistanceRequest` has no `requestedAt`; `RouteWithdrawn.at` is the closure time, not the detection time (proposed `withdrawnAt` / `detectedAt`). | Dispatch (task 4) |
| Snapshot has no revision timestamp; `/queries/closed-roads` has no `closedAt`; EMSR773 has no `populationEstimate`; hazard `provenance.mode` to be confirmed for every layer. | Situation (task 1) |
| The link simulator keeps the last profile (`iris2` after the presentation) and has no reset; a new run starts from a fresh stack. The Valencia link step sends no field command because no Valencia owner accepts one. | Task 6 / task 7 |
| No reset command; reset is `docker compose down -v` or a new `--data` directory. | Service owners (proposal) |
| Gateway build now needs the NuGet package `Microsoft.AspNetCore.Authentication.JwtBearer` on the first restore; prepare images or the NuGet cache before going offline. | Gateway (task 7) |
| A CI retry of the e2e test cannot pass without a reset command, so retries are off; metric 2 in `sample` shows the baseline explanation because Dispatch seeds requests before the page opens. | Task 7 / Dispatch |
| `tests/connectivity_integration.py` prints `²` in its final line, which fails on a Windows console with cp1250 after all assertions passed; run with `PYTHONIOENCODING=utf-8` (CI on Linux is not affected). | Task 6 |

### Not verified

- Presentation with Wi-Fi physically off: offline behaviour was checked by blocking non-localhost requests in the browser on a machine with internet.
- Keycloak `full` profile on Valencia (verified on `sample` only).
- `compose.management-full.yaml` started on Docker together with Keycloak (only `config` and `up --dry-run` checked here; task 6 verified PostgreSQL and RabbitMQ without Docker and in CI).
- The e2e CI job on GitHub (passed locally: `demo.spec.js` against Docker Compose, `valencia.spec.js` against `offline_demo.py --scenario valencia`).
- Image transport with `docker image save` / `load` on another machine.
- Galileo, EGNOS and real FlyPix detections (hardware and external services).

