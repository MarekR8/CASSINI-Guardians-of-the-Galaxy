"""Prepare the offline Valencia map, OSM graph and EMSR773 observation."""

import argparse
from collections import Counter
from datetime import datetime, timezone
import gzip
import json
import math
from pathlib import Path
import tempfile

import rasterio as rio
from rasterio.warp import Resampling, calculate_default_transform, reproject
from shapely import covers, make_valid, points, prepare, union_all
from shapely.geometry import LineString, box, mapping, shape
from shapely.strtree import STRtree

from osm_to_graph import ROOT, read_data
from prepare_warsaw_osm import build, fetch, read_pbf, svg_map


AREA = [-0.62, 39.24, -0.25, 39.61]
OUTPUT = ROOT / "artifacts/valencia-city"
EVIDENCE = ROOT / "artifacts/valencia-evidence"
SOURCE = "https://download.geofabrik.de/europe/spain/valencia-latest.osm.pbf"
PRODUCT = "EMSR773_AOI01_DEL_PRODUCT_v1"
EMS_URL = "https://mapping.emergency.copernicus.eu/activations/EMSR773/"
OBSERVATIONS = ["2024-10-30T10:37:00Z", "2024-10-31T10:51:00Z", "2024-10-31T18:02:00Z"]
SCENES = {"before": "2024-10-19", "event": "2024-10-31"}
ACCESS = {
    "motorway": 1800, "trunk": 1400, "primary": 1050, "secondary": 850,
    "tertiary": 650, "residential": 450, "unclassified": 450, "service": 250,
}


def read_polygons(path):
    layer = json.loads(path.read_text(encoding="utf-8-sig"))
    crs = layer.get("crs", {}).get("properties", {}).get("name")
    if crs not in (None, "urn:ogc:def:crs:OGC:1.3:CRS84", "EPSG:4326"):
        raise ValueError(f"{path.name}: expected longitude/latitude GeoJSON (CRS84 or WGS 84)")
    return [make_valid(shape(feature["geometry"])) for feature in layer["features"]
            if feature["geometry"] is not None]


def polygon_parts(geometry):
    if geometry.is_empty:
        return
    if geometry.geom_type == "Polygon":
        yield geometry
    elif hasattr(geometry, "geoms"):
        for part in geometry.geoms:
            yield from polygon_parts(part)


def flood_layers(directory):
    def read(layer):
        return union_all(read_polygons(directory / f"EMSR773_AOI01_DEL_PRODUCT_{layer}_v1.json"))

    extent = box(*AREA)
    flood = list(polygon_parts(read("observedEventA").intersection(extent)))
    coverage = read("areaOfInterestA").intersection(read("imageFootprintA")).intersection(extent)
    if not flood or coverage.is_empty:
        raise ValueError("EMSR773 flood and surveyed footprint must overlap Valencia.")
    return flood, coverage


def describe_edge(edge, tags, metres):
    road = edge["class"]
    directions = sum(cost is not None for cost in edge["drive"])
    raw_lanes = tags.get("lanes", "")
    lanes = int(raw_lanes) if raw_lanes.isdecimal() and 0 < int(raw_lanes) <= 16 else None
    assumed_lanes = lanes or max(1, directions)
    per_lane = ACCESS.get(road.removesuffix("_link"), 350)
    foot_capacity = 300 if road in {"steps", "track"} else 600 if road in {"path", "footway"} else 900
    edge.update(lengthMetres=round(metres, 1), lanes=lanes,
                vehiclesPerHour=round(per_lane * assumed_lanes) if directions else None,
                pedestriansPerHour=foot_capacity if any(edge["walk"]) else None)


def classify(network, flood, coverage):
    tree = STRtree(flood)
    # Shapes and node coordinates use [longitude, latitude] throughout.
    coordinates = {}
    for edge in network["roads"]:
        coordinates[edge["from"]] = edge["geometry"][0]
        coordinates[edge["to"]] = edge["geometry"][-1]
        edge["intersectsObservedFlood"] = bool(tree.query(LineString(edge["geometry"]), predicate="intersects").size)
    ids = sorted(coordinates)
    locations = points([coordinates[node] for node in ids])
    wet = set(tree.query(locations, predicate="intersects")[0])
    prepare(coverage)
    surveyed = covers(coverage, locations)
    return [{"id": node, "longitude": coordinates[node][0], "latitude": coordinates[node][1],
             "zone": "hazard" if index in wet else "safe" if surveyed[index] else "unknown"}
            for index, node in enumerate(ids)]


def node_map(nodes):
    west, south, east, north = AREA
    xscale = math.cos(math.radians((south + north) / 2)) * 10000
    paths = {zone: [] for zone in ("safe", "hazard", "unknown")}
    for node in nodes:
        x = (node["longitude"] - west) * xscale
        y = (north - node["latitude"]) * 10000
        paths[node["zone"]].append(f"M{x:.1f},{y:.1f}h.1")
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {(east-west)*xscale} {(north-south)*10000}" '
            'preserveAspectRatio="none">'
            f'<path fill="none" stroke="#9aa9b0" stroke-width="2" d="{"".join(paths["unknown"])}"/>'
            f'<path fill="none" stroke="#72dfaa" stroke-width="2.4" d="{"".join(paths["safe"])}"/>'
            f'<path fill="none" stroke="#ffc270" stroke-width="4" d="{"".join(paths["hazard"])}"/></svg>\n')


def write_images(directory, output):
    imagery = {}
    grid = None
    for name, day in SCENES.items():
        matches = sorted(path for path in directory.glob(day + "*") if path.suffix.lower() in {".tif", ".tiff"})
        if len(matches) != 1:
            raise ValueError(f"Place exactly one {day}*.tif or {day}*.tiff in {directory}")
        path = matches[0]
        with rio.open(path) as raster:
            if raster.crs is None or raster.count < 1 or raster.dtypes[0] != "uint8":
                raise ValueError(f"{path.name}: expected a georeferenced 8-bit visualisation")
            source_grid = (raster.crs, raster.transform, raster.shape)
            if grid is not None and source_grid != grid:
                raise ValueError("The two GeoTIFFs must share a projection, extent and pixel grid.")
            grid = source_grid
            transform, width, height = calculate_default_transform(
                raster.crs, "EPSG:4326", raster.width, raster.height, *raster.bounds)
            profile = dict(driver="GTiff", width=width, height=height, count=3, dtype="uint8",
                           crs="EPSG:4326", transform=transform)
            with rio.MemoryFile() as memory:
                with memory.open(**profile) as warped:
                    for band in range(1, 4):
                        reproject(rio.band(raster, min(band, raster.count)), rio.band(warped, band),
                                  resampling=Resampling.bilinear)
                    pixels = warped.read()
                    bounds = list(warped.bounds)
                with rio.open(output / f"sentinel-{name}.jpg", "w", driver="JPEG",
                              width=width, height=height, count=3, dtype="uint8") as image:
                    image.write(pixels)
        imagery[name] = {"bounds": bounds, "date": day, "file": path.name}
    return imagery


def write_json(path, obj):
    path.write_text(json.dumps(obj, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")


def run(output, evidence, inputs, refresh):
    output.mkdir(parents=True, exist_ok=True)
    flood, coverage = flood_layers(evidence / "emsr773-aoi01")
    pbf = output / "valencia.osm.pbf"
    if inputs:
        nodes, ways = read_data(inputs)
        timestamp = None
    else:
        if refresh or not pbf.exists():
            fetch(pbf, SOURCE)
        nodes, ways, timestamp = read_pbf(pbf, AREA)
    timestamp = timestamp or datetime.now(timezone.utc).isoformat(timespec="seconds")
    network = build(nodes, ways, timestamp, AREA, describe_edge)
    if not network["roads"]:
        raise ValueError("No routable OSM road sections in the Valencia map.")
    network["nodes"] = classify(network, flood, coverage)
    network["scenario"] = "valencia"
    counts = Counter(node["zone"] for node in network["nodes"])
    feature_collection = {"type": "FeatureCollection", "features": [
        {"type": "Feature", "properties": {}, "geometry": mapping(polygon)} for polygon in flood]}
    hazard = {"productId": PRODUCT, "observedAt": OBSERVATIONS[-1], "sourceUrl": EMS_URL,
              "source": "ems", "geoJson": feature_collection}
    with tempfile.TemporaryDirectory(dir=output) as temp:
        stage = Path(temp)
        scenes = write_images(evidence, stage)
        with gzip.open(stage / "city-network.json.gz", "wt", encoding="utf-8") as stream:
            json.dump(network, stream, ensure_ascii=False, separators=(",", ":"))
        (stage / "city-map.svg").write_text(svg_map(network, show_paths=True), encoding="utf-8")
        (stage / "city-nodes.svg").write_text(node_map(network["nodes"]), encoding="utf-8")
        write_json(stage / "flood.geojson", feature_collection)
        write_json(stage / "hazard-seed.json", hazard)
        write_json(stage / "city-imagery.json", {"scenes": scenes, "hazardAcquisitions": OBSERVATIONS})
        for file in stage.iterdir():
            if not file.name.endswith(".aux.xml"):
                file.replace(output / file.name)
    print(f"Valencia: {len(network['roads']):,} OSM edges; {len(network['nodes']):,} nodes {dict(counts)}; "
          f"{len(flood):,} EMS polygons; OSM snapshot {timestamp}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=OUTPUT)
    parser.add_argument("--evidence", type=Path, default=EVIDENCE)
    parser.add_argument("--input", nargs="+", type=Path, help="Use local OSM XML/Overpass exports")
    parser.add_argument("--refresh", action="store_true", help="Download a fresh Geofabrik extract")
    args = parser.parse_args()
    run(args.output, args.evidence, args.input, args.refresh)
