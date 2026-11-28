# Task 2 — Road network, FlyPix AI and closures

**Owner:** __________
**Directories:** `src/Situation/Network/`, `experiments/network/`, `data/demo/routing/`
**Technologies:** OpenStreetMap (Overpass), FlyPix AI (GeoJSON export / API), GeoJSON, `SituationChanged.v1` event

## What the demo shows

The Valencia viewer shows the real OSM network and observed flood extent. In an isolated integration test, a FlyPix detection enters the review queue and an approved decision updates the routing revision.

## Technology concept

- **OSM**: a one-off export of roads in the AOI via Overpass, simplified into a graph: nodes at junctions, segments with stable `segmentId`, walking/driving times from length and speed. The repo stores the finished graph, not raw OSM.
- **FlyPix AI**: a detection model for a flooded road/bridge on an image (drone or Sentinel-2). The result is GeoJSON with `confidence` and image date. `LiveFlyPixSource` fetches the export from the API/workspace; `MockFlyPixSource` reads a file. A detection **never** closes a road on its own.
- **Closure**: the existing `POST /commands/closure` extended with a source (`operator`, `flypix-verified`), validity window and the related `reviewFeatureId`.

## Scope

### Stage A (demo)
- [ ] Use the shared Sentinel‑1 extent in the Valencia task 03 dataset (W0).
- [ ] Build the graph from a local OSM extract with `experiments/network/prepare_valencia.py` and export **W1** with `experiments/network/export_valencia_graph.py`.
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
python scripts/offline_demo.py --scenario valencia --test
```
- Test: an unapproved detection does not change the route distribution; an approved one closes the segment and increments the situation revision.
- Idempotency test: the same decision with the same `commandId` gives the same result; different content → 409.

## Where to save results

- `data/demo/routing/valencia-task03.zip` with the graph, bounds, OSM attribution and observation metadata.
- `data/demo/review/flypix-*.geojson` (+ `recorded/` for API responses).
- Section below: FlyPix model parameters, confidence, time from image to result.

## Results

The Valencia viewer shows the shared radar rectangle and the Copernicus EMS extent. Situation can match a browser-exported FlyPix polygon to OSM edges for isolated tests. Routing validation checks current closures by segment ID. [Data preparation and handoff](../../data/local/valencia.md) · [Task 03 input contract](../../data/local/valencia-task03.md)
