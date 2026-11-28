"""Copernicus input for the Warsaw pipeline, from local GeoTIFFs or CDSE."""

from collections import defaultdict
from datetime import datetime, timedelta, timezone
import hashlib
import math
from pathlib import Path

from import_warsaw_evidence import AOI, load_json, setting, timestamp


AUTH = "https://identity.dataspace.copernicus.eu/auth/realms/CDSE/protocol/openid-connect/token"
PROCESS = "https://sh.dataspace.copernicus.eu/process/v1"
CATALOG = "https://stac.dataspace.copernicus.eu/v1/search"
CDSE_BROWSER = "https://browser.dataspace.copernicus.eu/"
EVALSCRIPT = """//VERSION=3
function setup() {
  return {input: ["B04", "B03", "B02", "dataMask"], output: {bands: 4, sampleType: "UINT8"}};
}
function evaluatePixel(sample) {
  return [Math.min(255, sample.B04 * 637.5), Math.min(255, sample.B03 * 637.5),
          Math.min(255, sample.B02 * 637.5), 255 * sample.dataMask];
}
"""


def cdse_request(url, **kwargs):
    import requests

    try:
        with requests.post(url, **kwargs, timeout=180, allow_redirects=False) as result:
            if result.status_code != 200:
                raise ValueError(f"Copernicus returned HTTP {result.status_code} at {url}.")
            return result.content
    except requests.RequestException:
        raise ValueError(f"Could not connect to Copernicus at {url}.") from None


def imagery_tiles(config, directory):
    source = config.get("source", "file")
    if source == "file":
        images = config["images"]
        from shapely import union_all
        from shapely.geometry import box

        bounds = []
        for img in images:
            product = img["imageProductId"].strip()
            if not product or len(product) > 120:
                raise ValueError("Each TIFF requires an imageProductId of up to 120 characters.")
            timestamp(img["observedAt"])
            bounds.append(validate_geotiff(directory / img["geoTiff"]))
        extent = box(*AOI)
        footprint = union_all([box(*item) for item in bounds]).intersection(extent)
        coverage = footprint.area / extent.area
        if config.get("requireFullCoverage", True) and coverage < .999:
            raise ValueError(f"GeoTIFF footprints cover only {coverage:.1%} of the Warsaw AOI. "
                             "Provide the missing area or explicitly select partial coverage.")
        print(f"GeoTIFF footprints cover {coverage:.1%} of the Warsaw AOI.", flush=True)
        max_tiles = config.get("maxTiles", 64)
        tiles = 0
        for img in images:
            path = directory / img["geoTiff"]
            prepared = prepare_file_tiles(path, directory, max_tiles - tiles)
            tiles += len(prepared)
            for tile in prepared:
                yield {**img, "geoTiff": str(tile), "observedAt": timestamp(img["observedAt"])}
        return
    if source != "sentinel-2":
        raise ValueError("imagery.source must be file or sentinel-2.")
    yield from sentinel2_tiles(config, directory)


def prepare_file_tiles(path, directory, remaining, size=2048):
    import rasterio
    from rasterio.windows import Window, from_bounds
    from rasterio.warp import transform_bounds

    with rasterio.open(path) as image:
        west, south, east, north = transform_bounds("EPSG:4326", image.crs, *AOI)
        box = from_bounds(west, south, east, north, transform=image.transform)
        left = max(0, math.floor(box.col_off))
        top = max(0, math.floor(box.row_off))
        right = min(image.width, math.ceil(box.col_off + box.width))
        bottom = min(image.height, math.ceil(box.row_off + box.height))
        columns = math.ceil((right - left) / size)
        rows = math.ceil((bottom - top) / size)
        if not columns or not rows:
            raise ValueError(f"{path.name}: empty raster window inside Warsaw AOI.")
        if rows * columns > remaining:
            raise ValueError("The GeoTIFF package exceeds imagery.maxTiles. Supply selected tiles or set a tile limit "
                             "appropriate for the FlyPix project and credits.")
        if (left, top, right, bottom) == (0, 0, image.width, image.height) and columns == rows == 1:
            return [path]
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        folder = directory / "prepared" / f"{digest}-{left}-{top}-{right}-{bottom}-{size}"
        folder.mkdir(parents=True, exist_ok=True)
        output = []
        for y in range(top, bottom, size):
            for x in range(left, right, size):
                tile = folder / f"{x}-{y}.tif"
                if not tile.exists():
                    window = Window(x, y, min(size, right - x), min(size, bottom - y))
                    profile = image.profile.copy()
                    profile.update(width=int(window.width), height=int(window.height),
                                   transform=image.window_transform(window))
                    partial = tile.with_suffix(".part.tif")
                    with rasterio.open(partial, "w", **profile) as target:
                        target.write(image.read(window=window))
                        target.colorinterp = image.colorinterp
                    partial.replace(tile)
                output.append(tile)
        return output


def validate_geotiff(path):
    import rasterio
    from rasterio.warp import transform_bounds

    with rasterio.open(path) as image:
        if image.driver != "GTiff" or not image.crs:
            raise ValueError(f"{path.name}: a GeoTIFF with a CRS is required.")
        west, south, east, north = transform_bounds(image.crs, "EPSG:4326", *image.bounds)
        a, b, c, d = AOI
        if max(west, a) >= min(east, c) or max(south, b) >= min(north, d):
            raise ValueError(f"{path.name}: the raster has no overlap with the Warsaw AOI.")
        return west, south, east, north


def hazard_geometry(config, directory):
    if config.get("geoJson"):
        return load_json(directory / config["geoJson"])

    import numpy as np
    import rasterio
    from rasterio.features import shapes
    from rasterio.windows import Window, from_bounds
    from rasterio.warp import transform_bounds, transform_geom
    from shapely.geometry import box, mapping, shape

    values = config["floodValues"]
    if not isinstance(values, list) or not values or not all(isinstance(value, int) for value in values):
        raise ValueError("A classified flood TIFF requires an explicit list of integer floodValues.")
    path = directory / config["geoTiff"]
    validate_geotiff(path)
    with rasterio.open(path) as image:
        bounds = transform_bounds("EPSG:4326", image.crs, *AOI)
        window = from_bounds(*bounds, transform=image.transform).round_offsets().round_lengths()
        window = window.intersection(Window(0, 0, image.width, image.height))
        if window.width * window.height > 50_000_000:
            raise ValueError("A classified TIFF is too large for local polygonization. Supply a clipped GeoJSON or tiles.")
        band = image.read(config.get("band", 1), window=window, masked=True)
        flooded = np.isin(band.data, values) & ~np.ma.getmaskarray(band)
        display = box(*AOI)
        features = []
        for polygon, _ in shapes(flooded.astype("uint8"), mask=flooded, transform=image.window_transform(window)):
            converted = transform_geom(image.crs, "EPSG:4326", polygon)
            geometry = shape(converted).intersection(display).simplify(.00001, preserve_topology=True)
            if not geometry.is_empty and geometry.geom_type in {"Polygon", "MultiPolygon"}:
                features.append({"type": "Feature", "geometry": mapping(geometry), "properties": {}})
    return {"type": "FeatureCollection", "features": features}


def sentinel2_tiles(config, directory):
    import json

    client = setting("CDSE_CLIENT_ID")
    secret = setting("CDSE_CLIENT_SECRET")
    if not client or not secret:
        raise ValueError("Set CDSE_CLIENT_ID and CDSE_CLIENT_SECRET for Sentinel Hub automatic download.")
    products = json.loads(cdse_request(CATALOG, json={
        "collections": ["sentinel-2-l2a"], "bbox": AOI, "datetime": config["datetime"], "limit": 100,
    }))["features"]
    products = covering_overpass(products, config.get("maxCloudCover", 25))
    observed = min(item[1] for item in products)
    start = (observed - timedelta(seconds=30)).isoformat()
    end = (max(item[1] for item in products) + timedelta(seconds=30)).isoformat()
    ids = sorted(item[0] for item in products)
    digest = hashlib.sha256("\n".join(ids).encode()).hexdigest()[:16]
    product_id = ids[0] if len(ids) == 1 else f"CDSE-S2-{observed:%Y%m%dT%H%M%S}-{digest}"
    token = json.loads(cdse_request(AUTH, data={
        "grant_type": "client_credentials", "client_id": client, "client_secret": secret
    }))["access_token"]
    folder = directory / "cdse" / Path(product_id).name
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "provenance.json").write_text(json.dumps({
        "imageProductId": product_id, "sourceProductIds": ids, "observedAt": observed.isoformat(),
        "timeRange": {"from": start, "to": end}, "bbox": AOI, "sourceUrl": CDSE_BROWSER,
    }, indent=2) + "\n", encoding="utf-8")
    west, south, east, north = AOI
    middle_lon, middle_lat = (east + west) / 2, (north + south) / 2
    for index, extent in enumerate(((west, south, middle_lon, middle_lat),
                                   (middle_lon, south, east, middle_lat),
                                   (west, middle_lat, middle_lon, north),
                                   (middle_lon, middle_lat, east, north)), 1):
        path = folder / f"warsaw-{index}.tif"
        if not path.is_file():
            payload = {
                "input": {"bounds": {"bbox": extent, "properties": {"crs": "http://www.opengis.net/def/crs/OGC/1.3/CRS84"}},
                          "data": [{"type": "sentinel-2-l2a", "dataFilter": {"timeRange": {"from": start, "to": end}}}]},
                "output": {"width": 1650, "height": 1750,
                           "responses": [{"identifier": "default", "format": {"type": "image/tiff"}}]},
                "evalscript": EVALSCRIPT,
            }
            contents = cdse_request(PROCESS, json=payload, headers={"Authorization": f"Bearer {token}"})
            partial = path.with_suffix(".part.tif")
            partial.write_bytes(contents)
            validate_geotiff(partial)
            partial.replace(path)
        validate_geotiff(path)
        yield {"imageProductId": product_id, "observedAt": observed.isoformat(),
               "sourceUrl": CDSE_BROWSER, "geoTiff": str(path)}


def covering_overpass(products, max_cloud):
    from shapely import union_all
    from shapely.geometry import box, shape

    extent = box(*AOI)
    groups = defaultdict(list)
    for item in products:
        props = item["properties"]
        cloud = props.get("eo:cloud_cover")
        if cloud is None or cloud > max_cloud or not item.get("geometry"):
            continue
        observed = datetime.fromisoformat(props["datetime"]).astimezone(timezone.utc)
        geometry = shape(item["geometry"]).intersection(extent)
        if geometry.is_empty:
            continue
        group = (props.get("platform"), props.get("sat:relative_orbit"), observed.strftime("%Y-%m-%dT%H"))
        groups[group].append((item["id"], observed, cloud, geometry))
    choices = [items for items in groups.values() if union_all([item[3] for item in items]).covers(extent)]
    if not choices:
        raise ValueError("No Sentinel-2 overpass covers the whole Warsaw AOI within the selected window and cloud limit. "
                         "Adjust the dates or use the team's GeoTIFFs.")
    return min(choices, key=lambda items: (max(item[2] for item in items), min(item[1] for item in items)))
