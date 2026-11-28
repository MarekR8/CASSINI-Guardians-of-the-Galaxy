# Task 2 — Road network, FlyPix AI and closures

**Owner:** __________
**Directories:** `src/Situation/Network/`, `experiments/network/`, `data/demo/routing/`
**Technologies:** local OpenStreetMap graph, FlyPix GeoJSON export and mock HTTP response, versioned Situation snapshot

## What the demo shows

The Valencia viewer shows the OSM network and archived flood extent. The local API can replay a synthetic FlyPix response or import browser-exported GeoJSON. Both enter the same review queue. The isolated test checks that approving a detection changes the Situation revision and the OSM test route.

## Technology concept

- **OSM**: a one-off export of roads in the AOI via Overpass, simplified into a graph: nodes at junctions, segments with stable `segmentId`, walking/driving times from length and speed. The repo stores the finished graph, not raw OSM.
- **FlyPix AI**. The browser workflow exports reviewed imagery annotations as GeoJSON with an image date and optional confidence. `MockFlyPixSource` returns a deterministic synthetic GeoJSON response locally. A detection only enters the queue. Closure requires operator approval.
- **Closure**. Situation records the source, `decidedAt` and `reviewFeatureId` for approved detections. An active restriction lasts until an explicit reopen command.

## Scope

### Stage A (demo)
- [x] Use the shared Sentinel‑1 extent in the Valencia task 03 dataset. The exercise bridge and exit IDs are tracked in [task 01](01-copernicus-hazard-layers.md#stage-a-demo) as part of W0.
- [x] Build the graph from a local OSM extract with `experiments/network/prepare_valencia.py` and export **W1** with `experiments/network/export_valencia_graph.py`. The [versioned archive](../../data/demo/routing/valencia-task03.zip) contains the graph and handoff schema.
- [x] Load Valencia's `city-network.json.gz` in Routing. The separate Kłodzko exercise uses the synthetic arrays in `Network.cs`.
- [x] Provide a synthetic preview via `IFlyPixSource` and import browser-exported GeoJSON through `POST /commands/import-flypix`. Imports stay pending until reviewed.
- [x] Apply `POST /commands/review-decision` with revision and idempotency checks. Approval closes matched segments and records evidence. Rejection leaves the roads open.
- [x] Fetch and import the offline FlyPix response with `python experiments/network/import_valencia_review.py --mock`. The same CLI imports a browser export with its manifest. The live API adapter is outside the Valencia demo scope.
- [x] Exercise the closure lifecycle using approval and operator reopen. The offline test checks the affected segment in both revisions. Automatic start and expiry windows are outside the demo scope.
- [x] Validate segment IDs against the current snapshot via Routing's `GET /queries/validate-route?segments=...&at=...`. It returns `ok` or `ClosedSegment`, with the checked timestamp and revision.

### Stage B (offline substitutes)
- [x] Use revisioned HTTP snapshots as the offline substitute for change delivery. [`SituationChanged.v1`](../../contracts/events/SituationChanged.v1.json) provides a synthetic event for consumer tests. Publishing the event from Situation's outbox to RabbitMQ is outside the offline scope.
- [x] Use the local OSM graph, JSON closures and validation by segment ID as the offline path. PostGIS storage and ORS polygon-intersection validation are outside this path.

## Dependencies

- W1 and segment-based W5 are available. The Valencia test exercises them with the task 03 planner disabled. The planner and its end-to-end acceptance are tracked in [task 03](03-route-distribution-algorithm.md). Dashboard presentation is tracked in task 07.

## Where to find data

- Overpass Turbo (`overpass-turbo.eu`): `way["highway"](bbox)` for the AOI; bridge: `bridge=yes`.
- FlyPix: hackathon account, docs at `docs.flypix.ai` (apply AI model, export vectors). Imagery: Sentinel-2 from CDSE or publicly available aerial images of the 2024 flood.
- Without FlyPix access: a detection drawn by hand in geojson.io, labeled `synthetic`.

## How to test

```sh
python scripts/offline_demo.py --test
python scripts/offline_demo.py --scenario valencia --test --valencia-inputs-only
```
- The test fetches the mock, imports it into the review queue and rejects it. A synthetic browser-export fixture then exercises approval, a detour, reopen and no route via `city-route`. This is the OSM test path used by tasks 01/02.
- Idempotency test: the same decision with the same `commandId` gives the same result; different content → 409.

## Where to save results

- `data/demo/routing/valencia-task03.zip` with the graph, bounds, OSM attribution and observation metadata.
- `data/demo/review/flypix-*.geojson` (+ `recorded/` for API responses).
- Section below: FlyPix model parameters, confidence, time from image to result.

## Results

The Valencia viewer shows the shared radar rectangle and the Copernicus EMS extent. PR #12 made the viewer read-only. Review, city routing, closed-road geometry and node classification are still available through the APIs or the task 03 package. The Valencia integration test uses synthetic review polygons to verify pending, rejection, approval, command replay and conflict, detour and no route on the OSM graph. [Data preparation and handoff](../../data/local/valencia.md) · [Task 03 input contract](../../data/local/valencia-task03.md)
