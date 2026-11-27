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
- [ ] Leaflet + layers from `GET /queries/snapshot`, `/network`, `/distribution`, `/assignments`, `/incident`. Each layer appears in the legend with its `provenance`.
- [ ] Styles: observed (EMS/S1) ≠ to be reviewed (FlyPix) ≠ closed ≠ current route ≠ withdrawn route.
- [ ] Metrics panel: time from report to situation view, time to assign assistance, time from closure to withdrawal, number of people without a confirmed exit.
- [ ] Presentation mode: a list of script steps, each calling existing commands (reset, alert, draw, assistance request, FlyPix detection, approval, link drop, EWSS, exit confirmation).
- [ ] You run the dress rehearsal and update `docs/offline-demo.md` (presentation walkthrough).

### Stage B (completion)
- [ ] Playwright scenario test in CI (`.github/workflows/offline-ci.yml`).
- [ ] Keycloak in the `full` profile + dashboard sign-in; per-role views.
- [ ] Optional dashboard migration to React + TypeScript + Vite per the stack.

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

_(fill in)_
