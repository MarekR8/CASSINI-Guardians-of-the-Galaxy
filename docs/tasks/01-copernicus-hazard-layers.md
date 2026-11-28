# Task 1 — Copernicus: hazard layers and population

**Owner:** __________
**Directories:** `src/Situation/Hazards/`, `data/demo/hazards/`, `data/demo/population/`
**Technologies:** Copernicus EMS Rapid Mapping, GHSL, Sentinel-1 via CDSE/openEO (or ESA APEx), PostGIS (Stage B)

## What the demo shows

After a flood report the map shows the observed flood extent (EMS), a Sentinel-1 water mask and the number of people in the zone (GHSL). Each layer shows its source, product ID, observation time and a `mock`/`recorded`/`live` label.

## Technology concept

- **Copernicus EMS**: a ready-made flood delineation product (candidate `EMSR756`, Kłodzko Valley 2024). Download the vector, clip it to the AOI, convert to WGS 84 GeoJSON with `ogr2ogr`.
- **Sentinel-1 SAR**: our own water mask when there is no EMS activation. An openEO process on CDSE (threshold on `VV` in dB, or a ready APEx algorithm) returns water polygons. `live` mode submits an openEO job; `mock` reads the saved result.
- **GHSL** (`GHS-POP`): population raster. Sum the pixels inside the flood polygon. The result is an **estimate**, not confirmed people (INV-01). Publish only counts.

## Scope

### Stage A (demo)
- [ ] Agree the AOI with person 2 (W0). The EMS extent must intersect the bridge and one exit; the other exit must stay dry.
- [ ] `IHazardLayerSource` interface with `MockEmsSource`, `MockSentinel1Source` (files from `data/demo/hazards/`) and `LiveOpenEoSentinel1Source` (openEO call, URL and token from configuration).
- [x] `IPopulationEstimator`: `MockGhslEstimator` returns the precomputed value from `data/demo/population/estimate.json`; the script `experiments/eo/ghsl_zonal.py` computes it from the raster.
- [x] Extend `GET /queries/snapshot` with layer `provenance` and `populationEstimate { value, method, source, year }`. The dashboard reads both.
- [ ] Confirm the additive snapshot contract with the task 7 owner.

### Stage B (completion)
- [ ] Move layers to PostGIS (`situation_db`, SRID 4326, spatial index), EF Core + NetTopologySuite.
- [ ] Optional Copernicus DEM: low-lying segments as a *potential* hazard (does not close roads).

## Dependencies

- You provide **W0** together with person 2. Person 7 waits for the layer DTO. Person 3 can use the extent to exclude exits.

## Where to find data

- EMS: `mapping.emergency.copernicus.eu` → activation → AOI → *Delineation* product → download the `.zip` (shapefile/GeoJSON).
- Sentinel-1: `dataspace.copernicus.eu` (free hackathon account), openEO Python client (`openeo`), collection `SENTINEL1_GRD`, dates around 15–17 Sept 2024.
- GHSL: `human-settlement.emergency.copernicus.eu` → GHS-POP, epoch 2020/2025, 100 m resolution, the tile covering the AOI.

## How to test

```sh
python scripts/offline_demo.py --test               # must stay green (mock mode)
curl http://127.0.0.1:5080/api/situation/queries/snapshot   # layers + provenance + populationEstimate
```
- Unit test: the mock returns a layer with `mode = mock`; the geometry is a `Polygon`/`MultiPolygon` within the AOI.
- Live mode (manually, with internet): `Integrations__Sentinel1__Mode=live`, save the response to `data/demo/hazards/recorded/` and switch back to mock.
- Check in QGIS or geojson.io that the layers line up with person 2's graph.

## Where to save results

- Clipped GeoJSON: `data/demo/hazards/*.geojson`, metadata in `*.meta.json` (productId, observedAt, URL, download date).
- Scripts: `experiments/eo/`. Raw files outside Git.
- Results note: the section below.

## Results

The two synthetic exercise polygons have precomputed estimates of 65 (Warsaw) and 4,872 (sample)
residents. The input was JRC's [GHS-POP R2023A, 2025 projection, 100 m, Mollweide](https://jrc-ghsl.s3.eu-central-1.amazonaws.com/ghs-pop/r2023a/54009/100m/2025/GHS_POP_E2025_GLOBE_R2023A_54009_100_V1_0.tif),
accessed on 2026-11-28. The calculation sums valid cells whose centres are inside the polygon.

```sh
python experiments/eo/ghsl_zonal.py population.tif flood.geojson --year 2025 --output estimate.json
```

To import an estimate with a flood layer, put the resulting JSON object in `hazards[].populationEstimate`
in the Warsaw manifest. The top-level snapshot estimate refers to the last imported layer, or to the
exercise polygon before any imports. A layer without an estimate returns `null`.

The exercise polygons are synthetic. The EMS product or Sentinel-1 analysis and the overlap check with
the Warsaw corridors remain pending.
