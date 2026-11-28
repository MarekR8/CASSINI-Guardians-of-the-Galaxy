# Task 2 — Road network, FlyPix AI and closures

**Owner:** __________
**Directories:** `src/Situation/Network/`, `data/demo/network/`, `data/demo/review/`
**Technologies:** OpenStreetMap (Overpass), FlyPix AI (GeoJSON export / API), GeoJSON, `SituationChanged.v1` event

## What the demo shows

A real piece of the road network with a bridge and a detour. FlyPix marks the bridge as "detected, unverified" (different map style, routes unchanged). The operator approves → the segment is closed → Routing withdraws affected routes.

## Technology concept

- **OSM**: a one-off export of roads in the AOI via Overpass, simplified into a graph: nodes at junctions, segments with stable `segmentId`, walking/driving times from length and speed. The repo stores the finished graph, not raw OSM.
- **FlyPix AI**: a detection model for a flooded road/bridge on an image (drone or Sentinel-2). The result is GeoJSON with `confidence` and image date. `LiveFlyPixSource` fetches the export from the API/workspace; `MockFlyPixSource` reads a file. A detection **never** closes a road on its own.
- **Closure**: the existing `POST /commands/closure` extended with a source (`operator`, `flypix-verified`), validity window and the related `reviewFeatureId`.

## Scope

### Stage A (demo)
- [ ] Agree the AOI with person 1 (W0) and write `data/demo/scenario.json` (bridge, 2 exits, pickup point, pedestrian start, unit).
- [ ] Script `experiments/network/osm_to_graph.py` (Overpass → `data/demo/network/graph.json`, matching the shape of `GET /queries/network`). You provide **W1**.
- [ ] Load the graph from the file in Routing instead of the arrays in `Network.cs` (agree with person 3, who owns the algorithm).
- [ ] `IReviewLayerSource` (mock/live FlyPix) + command `POST /commands/review-decision` (approve/reject a detection) → on approval it closes the segment.
- [ ] `GET /queries/validate-route?segments=...&at=...` returns `ok` or the reason `ClosedSegment` (**W5** for person 4).

### Stage B (completion)
- [ ] Publish `SituationChanged.v1` to RabbitMQ (after **W3** from person 6).
- [ ] Graph and closures in PostGIS; validate the ORS candidate geometry by intersecting it with the closure polygon.

## Dependencies

- You wait for: W0 (together with 1), W3 (RabbitMQ from 6) in Stage B.
- Waiting on you: 3 and 4 (graph, W1), 4 (validation, W5), 7 (review layer style).

## Where to find data

- Overpass Turbo (`overpass-turbo.eu`): `way["highway"](bbox)` for the AOI; bridge: `bridge=yes`.
- FlyPix: hackathon account, docs at `docs.flypix.ai` (apply AI model, export vectors). Imagery: Sentinel-2 from CDSE or publicly available aerial images of the 2024 flood.
- Without FlyPix access: a detection drawn by hand in geojson.io, labeled `synthetic`.

## How to test

```sh
python scripts/offline_demo.py --test
python experiments/network/osm_to_graph.py --check data/demo/network/graph.json   # consistency: no dangling segments, both exits reachable
```
- Test: an unapproved detection does not change the route distribution; an approved one closes the segment and increments the situation revision.
- Idempotency test: the same decision with the same `commandId` gives the same result; different content → 409.

## Where to save results

- `data/demo/network/graph.json` + `graph.meta.json` (bbox, export date, OSM attribution).
- `data/demo/review/flypix-*.geojson` (+ `recorded/` for API responses).
- Section below: FlyPix model parameters, confidence, time from image to result.

## Results

Warsaw is available as a separate offline scenario. The graph uses OpenStreetMap road geometry and contains Most Świętokrzyski, a pedestrian exit inland in Praga, and a separate approach for the response unit. Situation matches FlyPix polygons to corridor geometry and applies an approved decision atomically. The Routing validation query checks segment IDs against the current situation revision. See the [Warsaw area and data handoff](../../data/local/README.md) and [graph provenance](../../data/demo/network/README.md).

Verification commands are `python scripts/offline_demo.py --test` and `python scripts/offline_demo.py --scenario warsaw --test`. The exercise detection is synthetic. The model, a real FlyPix run and satellite observations for this area need to be verified with the team’s data. The demo keeps closures active until authorized reopening. RabbitMQ, PostGIS and ORS geometry validation remain Stage B work.

The Warsaw-wide AOI is [20.830, 52.080, 21.300, 52.390]. The PBF preparation script builds a local city graph and basemap. The city route query excludes approved OSM closures. The Copernicus task connects through the [Warsaw sync manifest](../../data/local/warsaw-sync.example.json).


## Valencia handoff

The branch now opens the Valencia metropolitan exercise. [Preparation, browser FlyPix export and the task 03 graph contract](../../data/local/valencia.md) describe the EMSR773 observation, safe/hazard/unknown node labels, estimated edge capacities and operator-approved closures.
