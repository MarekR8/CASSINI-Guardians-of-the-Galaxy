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
- [ ] `IRouteProvider` + both providers; configuration `Integrations:Ors:Mode`, `Integrations:Ors:BaseUrl`, `Integrations:Ors:ApiKey` (user-secrets).
- [ ] Record ORS responses for the scenario: approach and outbound before and after the bridge closure → `data/demo/ors/recorded/`.
- [ ] Dispatch uses the provider for approach and outbound legs; keep the existing double-booking guard and `commandId` idempotency.
- [ ] Unit position with `positionEvidence { source, egnosApplied, hpl, mode }` in `GET /queries/assignments`.

### Stage B (completion)
- [ ] Rank units by approach time using the ranking from task 3 (W4).
- [ ] React to `RouteWithdrawn.v1` / `SituationChanged.v1` from RabbitMQ (W3) instead of manual `replan`.
- [ ] Dispatch in PostgreSQL: active unit booking uniqueness as a database constraint.

## Dependencies

- You wait for: W1 (point coordinates from person 2's graph), W5 (validation), W4 and W3 in Stage B.
- Waiting on you: person 7 (unit route geometry on the map).

## Where to find data

- ORS key: free account at `openrouteservice.org` (daily limit is enough for recordings).
- Docs: `giscience.github.io/openrouteservice` → Directions, Routing options (`avoid_polygons`).
- EGNOS: HPL/integrity descriptions on EUSPA pages; choose realistic mock values (HPL of a few to a dozen metres).

## How to test

```sh
python scripts/offline_demo.py --test        # mock mode, no key
dotnet user-secrets set Integrations:Ors:ApiKey <key> --project src/Routing/Cassini.Routing.csproj
Integrations__Ors__Mode=live dotnet run --project src/Routing/Cassini.Routing.csproj   # manual recording
```
- Test: missing recording → `FixtureMissing`; same input → same response; a candidate through the closed bridge → rejected by validation.
- Integration test: two parallel bookings of the same unit → only one wins (already in `tests/offline_integration.py`, must not break).

## Where to save results

- Recordings: `data/demo/ors/recorded/<hash>.json` + `index.json` (request → file, recording date).
- Section below: API limits, response times, ORS vs demo graph differences.

## Results

_(fill in)_
