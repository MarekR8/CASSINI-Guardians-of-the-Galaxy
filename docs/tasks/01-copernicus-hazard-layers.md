# Task 1 — Copernicus: hazard layers and population

**Owner:** __________
**Directories:** `src/Situation/Hazards/`, `data/demo/hazards/`, `data/demo/population/`
**Technologies:** archived Copernicus EMS, Sentinel-1 radar imagery, GHSL estimates, local mock HTTP responses

## What the demo shows

Valencia shows the archived EMSR773 flood extent and two Sentinel-1 radar images. The **Integracje · symulacja offline** panel previews separate synthetic EMS and Sentinel-1 polygons, a synthetic population example and a mock DEM low-elevation area.

## Technology concept

- **Copernicus EMS**: a ready-made flood delineation product (candidate `EMSR756`, Kłodzko Valley 2024). Download the vector, clip it to the AOI, convert to WGS 84 GeoJSON with `ogr2ogr`.
- **Sentinel-1 SAR**. The radar pair is archived imagery. The water-mask endpoint supplies a deterministic synthetic polygon for the offline exercise. A prepared real polygon can be imported locally.
- **GHSL** (`GHS-POP`). The zonal script sums the pixels inside the flood polygon. Kłodzko has a precomputed estimate. The Valencia mask preview carries a synthetic example of 42 people. The archived Valencia EMS layer carries no population estimate in Situation.

## Scope

### Stage A (demo)
- [x] Share the Valencia AOI with task 02. The [task 03 package](../../data/local/valencia-task03.md) uses the shared Sentinel-1 extent and the clipped EMSR773 observation.
- [x] Exercise W0 with a synthetic footprint on the Kłodzko graph. The sample test checks that the bridge, pickup and eastern exit fall in the test polygon and the northern exit is dry. Valencia's W0 handoff contains the historical extent and classified OSM graph for task 03.
- [x] Serve an offline EMS preview via `ICopernicusSource` and an archived EMS layer on the map. The preview generates a synthetic polygon labeled `mock`. The Valencia snapshot uses a local seed prepared from EMSR773.
- [x] Accept locally prepared EMS and Sentinel-1 polygons through `POST /commands/import-copernicus`. Imports retain the product ID and observation time. The sample integration test covers both sources and a per-layer population estimate.
- [x] Provide a Sentinel-1 water-mask mock through `GET /queries/integration-sentinel-1` with product ID, observation time, GeoJSON and a synthetic population estimate. The dashboard renders it in the integration panel beside the EMS preview.
- [x] `IPopulationEstimator`: `MockGhslEstimator` returns the precomputed value from `data/demo/population/estimate.json`; the script `experiments/eo/ghsl_zonal.py` computes it from the raster.
- [x] Extend `GET /queries/snapshot` with `hazards[].provenance { source, mode, observedAt, productId }` and `populationEstimate { value, method, source, year }`. Kłodzko uses both in its sources panel. An imported layer without an estimate leaves the top-level estimate `null`.
- [x] Verify the snapshot contract through Gateway, the Kłodzko sources panel and the Valencia mock integration panel. Acceptance of the full presentation remains part of the task 07 rehearsal.

### Stage B (offline substitute)
- [x] Preview potential low elevation via `GET /queries/integration-copernicus-dem`. The synthetic polygon is read-only and has no effect on closures.

For the Valencia demo, Situation uses local GeoJSON and JSON persistence. Live EMS/openEO clients, DEM processing and PostGIS are outside the offline scope.

## Dependencies

- **W0** and the layer fields are available through the [Valencia handoff](../../data/local/valencia-task03.md). The choice of a Valencia planning node and verification of task 03 output are tracked in [task 03](03-route-distribution-algorithm.md).

## Where to find data

- EMS: `mapping.emergency.copernicus.eu` → activation → AOI → *Delineation* product → download the `.zip` (shapefile/GeoJSON).
- Sentinel-1: `dataspace.copernicus.eu` (free hackathon account), openEO Python client (`openeo`), collection `SENTINEL1_GRD`, dates around 15–17 Sept 2024.
- GHSL: `human-settlement.emergency.copernicus.eu` → GHS-POP, epoch 2020/2025, 100 m resolution, the tile covering the AOI.

## How to test

```sh
python scripts/offline_demo.py --test               # must stay green (mock mode)
python scripts/offline_demo.py --scenario valencia --test --valencia-inputs-only
curl http://127.0.0.1:5080/api/situation/queries/snapshot   # layers + provenance + populationEstimate
```
- The Valencia test checks that the mock polygons fall inside the shared imagery rectangle, the mock reads leave the snapshot intact, and the exported graph and imagery use the same bounds.

## Where to save results

- Clipped GeoJSON: `data/demo/hazards/*.geojson`, metadata in `*.meta.json` (productId, observedAt, URL, download date).
- Scripts: `experiments/eo/`. Raw files outside Git.
- Results note: the section below.

## Results

The synthetic Kłodzko exercise polygon has a precomputed estimate of 4,872 residents. The input was JRC's [GHS-POP R2023A, 2025 projection, 100 m, Mollweide](https://jrc-ghsl.s3.eu-central-1.amazonaws.com/ghs-pop/r2023a/54009/100m/2025/GHS_POP_E2025_GLOBE_R2023A_54009_100_V1_0.tif),
accessed on 2026-11-28. The calculation sums valid cells whose centres are inside the polygon.

```sh
python experiments/eo/ghsl_zonal.py population.tif flood.geojson --year 2025 --output estimate.json
```

To import an estimate with a flood layer, put the resulting JSON object in `populationEstimate`
in the payload of Situation's `import-copernicus` command. The top-level snapshot estimate refers to the last imported layer, or to the
exercise polygon before any imports. A layer without an estimate returns `null`.

The exercise polygons are synthetic. The Kłodzko map includes a reference flood extent clipped from
the public [EMSR756 AOI03 Sentinel-1 delineation](https://mapping.emergency.copernicus.eu/activations/EMSR756/),
observed on 2024-09-15 at 05:01 UTC.

Valencia uses the official EMSR773 AOI01 delineation, clipped to the shared Sentinel‑1 rectangle in the viewer and in the task 03 export. [Sources and preparation](../../data/local/valencia.md).
