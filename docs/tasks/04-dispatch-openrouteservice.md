# Task 4 — Dispatch, response units and OpenRouteService

**Owner:** __________
**Directories:** `src/Dispatch/`, `src/Routing/Providers/` (new), `data/demo/ors/`
**Technologies:** OpenRouteService Directions API, typed `HttpClient`, EGNOS (unit position integrity, simulated)

## What the demo shows

A person without a car asks for transport. The dispatcher sees a unit, its position with a quality indicator (EGNOS), and approach and outbound routes drawn on real roads from ORS. A second request cannot book the same unit. After the bridge closes, the unit's route is recalculated.

## Technology concept

- **OpenRouteService**: `POST /v2/directions/{profile}/geojson` with `start`, destination and `options.avoid_polygons` (the closed bridge polygon). Profile `driving-car` for units, `foot-walking` for pedestrians. `IRouteProvider` has `OrsRouteProvider` (live, key from configuration) and `RecordedRouteProvider` (response from a file, key = hash of the normalized request). A missing recording is an explicit `FixtureMissing` error, never a silent substitution.
- **An ORS candidate is not the truth**: before booking, the geometry is validated in Situation (`ValidateRoute`, W5).
- **EGNOS**: the unit reports its position with an `egnosApplied` flag and `hpl` (protection level). The mock returns values from a file. No confirmation or `hpl` too large → `PositionUnverified`, and the dispatcher confirms the start point manually.

## Scope

### Stage A (demo)
- [x] `IRouteProvider` + both providers; configuration `Integrations:Ors:Mode`, `Integrations:Ors:BaseUrl`, `Integrations:Ors:ApiKey` (user-secrets).
- [ ] Record ORS responses for the scenario: approach and outbound before and after the bridge closure → `data/demo/ors/recorded/`.
- [x] Dispatch uses the provider for approach and outbound legs; keep the existing double-booking guard and `commandId` idempotency.
- [x] Unit position with `positionEvidence { source, egnosApplied, hpl, mode }` in `GET /queries/assignments`.

### Stage B (completion)
- [ ] Rank units by approach time using the ranking from task 3 (W4).
- [ ] React to `RouteWithdrawn.v1` / `SituationChanged.v1` from RabbitMQ (W3) instead of manual `replan`.
- [ ] Dispatch in PostgreSQL: active unit booking uniqueness as a database constraint.

## Dependencies

- You wait for: W1 (point coordinates from person 2's graph), W5 (validation), W4 and W3 in Stage B.
- Waiting on you: person 7 (unit route geometry on the map).

## Where to find data

- ORS key: sign in to [HeiGIT Account](https://account.heigit.org/) and copy the basic key in the API Key tab.
- Docs: `giscience.github.io/openrouteservice` → Directions, Routing options (`avoid_polygons`).
- EGNOS: HPL/integrity descriptions on EUSPA pages; choose realistic mock values (HPL of a few to a dozen metres).

## How to test

```sh
python scripts/offline_demo.py --test        # default offline graph, no key
python tests/dispatch_ors_integration.py     # after building the solution
dotnet user-secrets set Integrations:Ors:ApiKey <key> --project src/Routing/Cassini.Routing.csproj
Integrations__Ors__Mode=live dotnet run --project src/Routing/Cassini.Routing.csproj   # manual recording
```
- Test: missing recording → `FixtureMissing`; same input → same response; a candidate through the closed bridge → rejected by validation.
- Integration test: two parallel bookings of the same unit → only one wins (already in `tests/offline_integration.py`, must not break).

## Where to save results

- Recordings: `data/demo/ors/recorded/<hash>.json` + `index.json` (request → file, recording date).
- Section below: API limits, response times, ORS vs demo graph differences.

## Results

Routing owns the provider request and candidate validation. It uses the current
Situation revision and known closure geometry before returning either leg.
Dispatch checks that revision before saving a booking or replan and retains the
existing command receipt and active booking checks.

| `Integrations:Ors:Mode` | Route source |
| --- | --- |
| `graph` (default) | Local exercise graph |
| `mock` or `recorded` | Strict replay from the published `ors/recorded` directory or `Integrations:Ors:FixtureDirectory` |
| `live` | ORS Directions GeoJSON at `https://api.heigit.org/openrouteservice`. Configure `Integrations:Ors:ApiKey` and optionally `Integrations:Ors:BaseUrl` |

Use `Integrations__Ors__Mode=mock` with `scripts/offline_demo.py` to replay the
bundled examples. The [request index](../../data/demo/ors/index.json) covers both
exercise graphs and their before/after bridge scenarios. The files are labeled
`synthetic`. Their geometry and duration come from the bundled exercise graphs.
Warsaw approaches the pickup across the bridge before its closure, even though
the local outbound ranking already prefers the north exit.

To capture actual responses, configure `Integrations__Ors__ApiKey` in your local
environment or use the Routing project's .NET user-secrets. Run
`python scripts/record_ors_routes.py artifacts/ors-recorded`, then run
`python tests/dispatch_ors_integration.py --recordings artifacts/ors-recorded`
after building the solution. To use those recordings in the dashboard, point
`Integrations__Ors__FixtureDirectory` at that directory and use replay mode.
The hash includes the profile and complete normalized request.
Missing entries return `FixtureMissing`. The launcher sets the environment to
Production. Use the environment variable with the launcher and use .NET
user-secrets with a standalone Routing process in Development.

The current closure check is an exercise approximation based on the active
closed segments in Situation. For the Warsaw bridge it covers the river
crossing, since the logical segment also contains approach roads used by the
detour. An active closed segment with unknown geometry blocks ORS routing.
PostGIS intersection with operational closure polygons remains part of task 02.

The API includes `approachGeometry`, `outboundGeometry` and `provenance` in
provider routes. The dashboard draws currently valid vehicle legs and labels
the route source. The unit position fixture labels EGNOS as synthetic. If the
reported quality is missing or the HPL exceeds `Integrations:Egnos:MaxHpl`
(25 metres by default), the dispatcher can pass `confirmedStartNode` with the
known node in an assign or replan command.

Local verification covers capturing responses from an ORS-compatible HTTP
test server and replaying those files for both cities, missing recordings,
manual start position, and rejection of a route crossing the closed bridge.
The browser check covered the position
label, both vehicle legs, withdrawal after bridge closure, the detour and the
state with no available exit. Actual ORS responses and hosted service limits
still need validation with an API key.
