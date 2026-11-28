# Team tasks (7 people)

Goal: take the flood evacuation demo to the presentation, then to the full architecture in [mvp-technology-stack.md](../mvp-technology-stack.md). **The demo comes first.** Each task has a Stage A (demo) and a Stage B (project completion). Start Stage B only after everyone's Stage A is green.

The follow-on [Sheltering and Grouping tasks](sheltering/README.md) build on these packages and have their own Stage A acceptance checks.

| # | File | Part of the architecture | Technologies |
| --- | --- | --- | --- |
| 1 | [01-copernicus-hazard-layers.md](01-copernicus-hazard-layers.md) | Situation: hazard layers and population | Copernicus EMS, GHSL, Sentinel-1 (CDSE/openEO), PostGIS |
| 2 | [02-road-network-flypix-closures.md](02-road-network-flypix-closures.md) | Situation: road network and closures | OpenStreetMap, FlyPix AI, GeoJSON |
| 3 | [03-route-distribution-algorithm.md](03-route-distribution-algorithm.md) | **Algorithm/mathematics** in Routing | Python, NetworkX, C#, GloFAS forecast |
| 4 | [04-dispatch-openrouteservice.md](04-dispatch-openrouteservice.md) | Dispatch and route provider | OpenRouteService, EGNOS (unit position) |
| 5 | [05-android-galileo.md](05-android-galileo.md) | Mobile app | Galileo, OSNMA, EGNOS, EWSS |
| 6 | [06-management-events-connectivity.md](06-management-events-connectivity.md) | Management, events, connectivity | RabbitMQ, IRIS², GOVSATCOM, EWSS (broadcast) |
| 7 | [07-dashboard-map-demo.md](07-dashboard-map-demo.md) | Gateway, dashboard, demo script | Leaflet, Gateway, Playwright, Keycloak |

## Shared rules (apply to every task)

1. **The code connects to the real service; the demo data is mocked.** Each external integration is an interface with two implementations: `Mock…` reads a file from `data/demo/<area>/`, and `Live…` calls the real API. The mode is selected by the `Integrations:<Name>:Mode` setting, `mock` (default) or `live`, e.g. the variable `Integrations__Ors__Mode=live`.
2. **Prefer mocks built from recordings.** If you can call the real API once, save the response in `data/demo/<area>/recorded/` and have the mock replay it. No recording = a synthetic file labeled `synthetic`.
3. **Data provenance.** Every record coming from an integration carries `provenance: { source, mode: "mock" | "recorded" | "live", observedAt, productId? }`. The dashboard shows a simulation label (`SYMULACJA` in the current Polish UI) for `mock`.
4. **Offline stays unchanged.** `docker compose up` and `python scripts/offline_demo.py --test` must work without internet and without keys. Add new containers (PostgreSQL, RabbitMQ, Keycloak) under the Compose profile `full`, not the default one.
5. **Keys and sensitive data** go only in `dotnet user-secrets`, environment variables or an ignored `.env`. Keep large raw downloads (GeoTIFF, full EMS products) outside Git; commit only clipped GeoJSON. Check the license (OSM: ODbL and attribution).
6. **Directory ownership and CQRS** as in [AGENTS.md](../../AGENTS.md). Agree any change to someone else's contract with its owner.
7. **Record results in the `## Results` section at the end of your file**: what was verified, where the data came from (link, product ID, download date), where the file is, and what could not be verified.

## Waits between tasks

These parts cannot be separated. Until a wait is resolved, work against a mock or the current synthetic graph.

| ID | Provider → waiting | What | Workaround until then |
| --- | --- | --- | --- |
| **W0** | 1 + 2 → everyone | Shared area of interest (AOI), bridge, 2 exits, pickup point, stable IDs in `data/demo/scenario.json` | Current IDs from `src/Routing/Network.cs` |
| **W1** | 2 → 3, 4, 7 | Road graph in `data/demo/network/` in a shape compatible with `GET /queries/network` | Synthetic graph from `Network.cs` |
| **W2** | 6 → 5 | `POST /commands/progress` contract with position evidence fields (Galileo, OSNMA, EGNOS) | Android stores reports locally |
| **W3** | 6 → 2, 4, 7 | RabbitMQ and the events `SituationChanged.v1` / `RoutePublished.v1` / `AssignmentChanged.v1` | HTTP polling every 7 s (current behavior) |
| **W4** | 3 → 4 | Expected-results file `data/demo/routing/expected.json` and the C# port of the ranking | Current "inverse time" formula |
| **W5** | 2 → 4 | `ValidateRoute` in Situation (checks an ORS candidate against closures) | Validation by segment IDs as today |
| **W6** | 1, 2, 4, 6 → 7 | Stage A of each task ready for the dress rehearsal | Dashboard draws mock layers |

Order: on **day 0** (1–2 h, everyone) agree on W0 and draft DTOs in `contracts/`. Then work in parallel. At the end, task 7 runs the dress rehearsal.

## Demo script (the target of every Stage A)

1. Flood report → map with the Copernicus EMS layer, a Sentinel-1 water mask and a GHSL population estimate.
2. A pedestrian resident gets the route distribution at the node and a draw (algorithm from task 3).
3. A person without a car asks for transport → a unit with approach and outbound legs from OpenRouteService.
4. FlyPix detects a flooded bridge → operator approves → closure → routes withdrawn and recalculated.
5. Terrestrial link goes down → switch to IRIS² (simulated) → queued commands arrive after retry without duplicates. EWSS broadcasts a short alert.
6. A phone with Galileo (OSNMA as a labeled simulation) confirms leaving the zone → "confirmed outside the zone" counter.
