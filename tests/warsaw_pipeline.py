"""Test the Warsaw GeoTIFF and FlyPix sync without cloud credentials or credits."""

import json
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch

import numpy as np
import rasterio
from rasterio.transform import from_bounds
from shapely.geometry import box as extent, mapping

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "experiments/network"))

import copernicus
import import_warsaw_evidence as evidence
import sync_warsaw as sync


with tempfile.TemporaryDirectory() as temporary:
    folder = Path(temporary)
    area = evidence.AOI
    w, s, e, n = area
    image = folder / "image.tif"
    transform = from_bounds(w - .05, s - .05, e + .05, n + .05, 64, 64)
    with rasterio.open(image, "w", driver="GTiff", width=64, height=64,
                       count=1, dtype="uint8", crs="EPSG:4326", transform=transform, nodata=0) as target:
        band = np.zeros((64, 64), dtype="uint8")
        band[10:14, 10:14] = 1
        target.write(band, 1)
    tiles = copernicus.prepare_file_tiles(image, folder, 32, size=24)
    assert len(tiles) > 1
    for tile in tiles:
        with rasterio.open(tile) as source:
            assert source.width <= 24 and source.height <= 24 and source.crs.to_epsg() == 4326
    assert copernicus.hazard_geometry({"geoTiff": image.name, "floodValues": [1]}, folder)["features"]

    box = [[20.91, 52.11], [20.92, 52.11], [20.92, 52.12], [20.91, 52.12], [20.91, 52.11]]
    polygon = {"type": "Polygon", "coordinates": [box]}
    manifest = folder / "manifest.json"
    manifest.write_text(json.dumps({
        "imagery": {"images": [{"geoTiff": image.name, "imageProductId": "test-scene",
                                "observedAt": "2026-11-27T18:00:00Z"}]},
        "flypix": {"tenantId": "test-tenant", "projectId": "test-project", "modelId": "test-model", "classIds": [3]},
    }), encoding="utf-8")
    responses = {
        "/v2/users/tenants/for-user/": [],
        "/v2/rasters/test-raster": {"status": "PROCESSED"},
        "/v2/vectors/for-raster/test-raster": [],
        "/v2/models/test-model/apply": {"inference_id": "test-inference"},
        "/v2/models/inference/test-inference": {"status": "completed", "data": {"vector_id": "test-vector"}},
        "/v2/vectors/test-vector/features": {
            "data": {"polygons": [json.dumps(polygon), json.dumps(polygon)], "ids": [1, 2],
                     "confidences": [.91, .98], "class_ids": [3, 4]}
        },
    }
    paths = []

    def flypix(path, payload=None, headers=None, **kwargs):
        paths.append(path)
        if path.startswith("/v2/files/upload?"):
            assert not payload.closed
            return {"raster_id": "test-raster", "status": "UPLOADED"}
        return responses[path]

    def check_import(base, operation, body):
        assert operation == "import-flypix"
        assert len(body["geoJson"]["features"]) == 1 and body["geoJson"]["features"][0]["id"] == 1

    with (patch.object(evidence, "flypix", side_effect=flypix), patch.object(sync, "flypix", side_effect=flypix),
          patch.object(sync, "request", return_value={"city": {"bounds": area}}),
          patch.object(sync, "import_layer", side_effect=check_import)):
        sync.synchronize(manifest, "http://127.0.0.1:5080", 10, False)
        sync.synchronize(manifest, "http://127.0.0.1:5080", 10, False)
    assert sum(path.startswith("/v2/files/upload?") for path in paths) == 1
    assert paths.count("/v2/models/test-model/apply") == 1
    assert (folder / "flypix" / "test-vector.geojson").is_file()

    group = []
    for name, bounds in (("left", (w, s, (w + e) / 2, n)), ("right", ((w + e) / 2, s, e, n))):
        group.append({"id": name, "geometry": mapping(extent(*bounds)),
                      "properties": {"datetime": "2026-11-27T18:00:00Z", "eo:cloud_cover": 4,
                                     "platform": "sentinel-2a", "sat:relative_orbit": 36}})
    assert len(copernicus.covering_overpass(group, 25)) == 2

print("Warsaw pipeline passed: clipped/tiled GeoTIFF, classified flood mask, CDSE mosaic and FlyPix checkpoint replay.")
