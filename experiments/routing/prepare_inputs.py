"""Prepare the task 03 model inputs for Valencia: GHSL population, EMS flood depth and exercise shelters.

The population step reads only the needed window of the public GHS-POP raster over HTTP.
The depth step needs the EMSR773 AOI01 delineation package unpacked under artifacts/ (see data/local/valencia.md).
"""

import argparse
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import re

import numpy as np
import rasterio
from rasterio.warp import transform, transform_bounds
from rasterio.windows import from_bounds
from scipy.cluster.vq import kmeans2
from scipy.spatial import cKDTree
from shapely import contains_xy, distance as shapely_distance, make_valid, points as shapely_points, prepare, union_all
from shapely.affinity import scale
from shapely.geometry import LineString, box, mapping, shape

from graph import DEFAULT_GRAPH, EARTH_METRES, ROOT, EvacuationGraph
import shelters as shelter_file

OUTPUT = ROOT / "data/demo/routing/valencia"
GHSL_URL = ("https://jrc-ghsl.s3.eu-central-1.amazonaws.com/ghs-pop/r2023a/54009/100m/2025/"
            "GHS_POP_E2025_GLOBE_R2023A_54009_100_V1_0.tif")
EMS_DEPTH = ROOT / "artifacts/valencia-evidence/emsr773-aoi01/EMSR773_AOI01_DEL_PRODUCT_floodDepthA_v1.json"
OSM_EXTRACT = ROOT / "artifacts/valencia-city/valencia.osm.pbf"
FLOOD_SOURCE_WAY = 41726575  # "Barranc de Xiva", the lower Poyo ravine through Paiporta to the Albufera
EMS_PRODUCT = "EMSR773_AOI01_DEL_PRODUCT_v1"
EMS_URL = "https://mapping.emergency.copernicus.eu/activations/EMSR773/"
SHELTER_COUNT = 8
WITHOUT_STEP_FREE_ACCESS = 2  # the smallest exercise shelters, so wheelchair routing has a case to avoid
SURROUNDING_METRES = 1000
MAX_HAZARD_SHARE = 0.15
MIN_SOURCE_METRES = 1000


def write_json(path, document):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")


def hazard_area(graph):
    observation = graph.document["hazardObservation"]
    polygons = [make_valid(shape(feature["geometry"])) for feature in observation["geoJson"]["features"]]
    return union_all(polygons), observation


def population(graph, source):
    """Assign GHS-POP cells whose centres lie in the observed flood to the nearest reachable hazard node."""
    flood, observation = hazard_area(graph)
    prepare(flood)
    if source.startswith("http"):
        os.environ.setdefault("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR")
        location = "/vsicurl/" + source
    else:
        location = source
    with rasterio.open(location) as raster:
        projected = transform_bounds("EPSG:4326", raster.crs, *graph.bounds, densify_pts=21)
        exact = from_bounds(*projected, transform=raster.transform)
        col, row = math.floor(exact.col_off), math.floor(exact.row_off)
        window = rasterio.windows.Window(col, row, math.ceil(exact.col_off + exact.width) - col,
                                         math.ceil(exact.row_off + exact.height) - row)
        values = raster.read(1, window=window, masked=True).astype("float64").filled(0)
        cell = raster.window_transform(window)
        crs = raster.crs
    rows, cols = np.nonzero(values > 0)
    xs, ys = rasterio.transform.xy(cell, rows, cols, offset="center")
    longitude, latitude = transform(crs, "EPSG:4326", xs, ys)
    longitude, latitude = np.array(longitude), np.array(latitude)
    people = values[rows, cols]
    west, south, east, north = graph.bounds
    in_window = (longitude >= west) & (longitude <= east) & (latitude >= south) & (latitude <= north)
    wet = in_window & contains_xy(flood, longitude, latitude)
    candidates = (graph.zone == "hazard") & graph.main_component
    nodes, distances = graph.nearest(longitude[wet], latitude[wet], candidates)
    per_node = np.bincount(nodes, weights=people[wet], minlength=len(graph.nodes))
    assigned = [{"nodeId": graph.nodes[position]["id"], "people": round(float(per_node[position]), 2)}
                for position in np.flatnonzero(per_node > 0)]
    return {
        "populationVersion": "cassini.population.v1",
        "scenario": graph.document["scenario"],
        "kind": "estimate",
        "source": {
            "product": "Copernicus GHSL GHS-POP R2023A", "epoch": 2025, "resolutionMetres": 100,
            "crs": "ESRI:54009", "url": source if source.startswith("http") else None,
            "licence": "CC BY 4.0, European Commission JRC",
            "accessedAt": datetime.now(timezone.utc).date().isoformat(),
        },
        "method": ("GHS-POP cells whose centres lie inside the observed flood extent of "
                   f"{observation['productId']}, each assigned to the nearest hazard node on the main pedestrian network."),
        "bounds": graph.bounds,
        "totals": {
            "inWindow": round(float(people[in_window].sum())),
            "inObservedFlood": round(float(people[wet].sum())),
            "cells": int(wet.sum()),
            "nodes": len(assigned),
            "maxSnapMetres": round(float(distances.max()), 1) if distances.size else 0.0,
            "p95SnapMetres": round(float(np.percentile(distances, 95)), 1) if distances.size else 0.0,
        },
        "nodes": assigned,
    }


def depth_range(label):
    numbers = [float(value) for value in re.findall(r"\d+(?:\.\d+)?", label)]
    lowered = label.lower()
    if lowered.startswith("below") and len(numbers) == 1:
        return 0.0, numbers[0]
    if (lowered.startswith("above") or lowered.startswith("over")) and len(numbers) == 1:
        return numbers[0], None
    if len(numbers) == 2:
        return numbers[0], numbers[1]
    raise ValueError(f"Unrecognised flood depth class: {label!r}")


def flood_depth(graph, path):
    layer = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    crs = layer.get("crs", {}).get("properties", {}).get("name")
    if crs not in (None, "urn:ogc:def:crs:OGC:1.3:CRS84", "EPSG:4326"):
        raise ValueError(f"{Path(path).name}: expected longitude/latitude GeoJSON.")
    window = box(*graph.bounds)
    classes = {}
    for feature in layer["features"]:
        if feature.get("geometry") is None:
            continue
        label = feature["properties"]["value"]
        geometry = make_valid(shape(feature["geometry"])).intersection(window)
        if not geometry.is_empty:
            classes.setdefault(label, []).append(geometry)
    features = []
    for label, parts in sorted(classes.items(), key=lambda item: depth_range(item[0])[0]):
        merged = union_all(parts)
        merged = merged.buffer(0) if merged.geom_type not in ("Polygon", "MultiPolygon") else merged
        polygons = [part for part in getattr(merged, "geoms", [merged]) if part.geom_type == "Polygon"]
        if not polygons:
            continue
        geometry = polygons[0] if len(polygons) == 1 else union_all(polygons)
        low, high = depth_range(label)
        features.append({"type": "Feature",
                         "properties": {"depthClass": label, "minMetres": low, "maxMetres": high},
                         "geometry": round_geometry(mapping(geometry))})
    observation = graph.document["hazardObservation"]
    return {
        "type": "FeatureCollection",
        "productId": EMS_PRODUCT, "layer": "floodDepthA", "sourceUrl": EMS_URL,
        "kind": "observation", "acquisitionsUtc": observation["acquisitionsUtc"],
        "attribution": "Copernicus Emergency Management Service, © European Union 2024",
        "bounds": graph.bounds, "features": features,
    }


def flood_source(graph, extract, way_id):
    """Read the overflowing channel from the local OSM extract and clip it to the graph window with a margin."""
    import osmium

    found = {}

    class Channel(osmium.SimpleHandler):
        def way(self, way):
            if way.id == way_id:
                found["tags"] = dict(way.tags)
                found["points"] = [(node.lon, node.lat) for node in way.nodes]

    Channel().apply_file(str(extract), locations=True)
    if "points" not in found:
        raise ValueError(f"OSM way {way_id} is not in {extract}.")
    west, south, east, north = graph.bounds
    margin = 0.02
    line = LineString(found["points"]).intersection(box(west - margin, south - margin, east + margin, north + margin))
    if line.is_empty:
        raise ValueError(f"OSM way {way_id} does not reach the graph window.")
    return {
        "type": "FeatureCollection",
        "kind": "reference geometry",
        "attribution": "© OpenStreetMap contributors, ODbL",
        "note": ("Channel that overflowed on 29 October 2024. Distance from it sets the flood-trace deadline; "
                 "this is a model assumption, not a hydraulic simulation."),
        "features": [{"type": "Feature",
                      "properties": {"osmWayId": way_id, "name": found["tags"].get("name"),
                                     "waterway": found["tags"].get("waterway")},
                      "geometry": round_geometry(mapping(line))}],
    }


def round_geometry(geometry):
    def walk(value):
        if isinstance(value, (list, tuple)):
            if value and isinstance(value[0], (int, float)):
                return [round(value[0], 6), round(value[1], 6)]
            return [walk(item) for item in value]
        return value
    return {"type": geometry["type"], "coordinates": walk(geometry["coordinates"])}


def exercise_shelters(graph, people, source=None):
    """Place synthetic shelters on dry nodes behind the flood edge, sized to the nearby estimate."""
    hazard_roads = [road for road in graph.roads if road["zone"] == "hazard"]
    frontier = sorted({graph.index[node] for road in hazard_roads for node in (road["from"], road["to"])
                       if graph.zone[graph.index[node]] == "safe" and graph.main_component[graph.index[node]]})
    if len(frontier) < SHELTER_COUNT:
        raise ValueError("Too few dry nodes on the flood edge to place exercise shelters.")
    frontier = np.array(frontier)
    points = graph.project(graph.longitude[frontier], graph.latitude[frontier])
    centroids, _ = kmeans2(points, SHELTER_COUNT, minit="++", seed=np.random.default_rng(7))

    hazard = np.flatnonzero(graph.zone == "hazard")
    hazard_tree = cKDTree(graph.project(graph.longitude[hazard], graph.latitude[hazard]))
    dry = np.flatnonzero((graph.zone == "safe") & graph.main_component)
    dry_points = graph.project(graph.longitude[dry], graph.latitude[dry])
    inland, _ = hazard_tree.query(dry_points)
    # Avoid dry pockets enclosed by the flood: few hazard nodes may lie within a kilometre.
    every = cKDTree(graph.project(graph.longitude, graph.latitude))
    hazard_share = np.array([np.mean(graph.zone[around] == "hazard")
                             for around in every.query_ball_point(dry_points, SURROUNDING_METRES)])
    eligible = (inland >= 300) & (inland <= 1500) & (hazard_share <= MAX_HAZARD_SHARE)
    if source is not None:
        channel = scale(shape(source["features"][0]["geometry"]), xfact=graph.scale, yfact=EARTH_METRES, origin=(0, 0))
        eligible &= shapely_distance(shapely_points(dry_points), channel) >= MIN_SOURCE_METRES
    dry, dry_points = dry[eligible], dry_points[eligible]
    dry_tree = cKDTree(dry_points)

    chosen = []
    for centroid in centroids:
        _, nearest = dry_tree.query(centroid, k=40)
        for candidate in np.atleast_1d(nearest):
            position = int(dry[candidate])
            if all(np.hypot(*(dry_points[candidate] - graph.project([graph.longitude[other]],
                                                                    [graph.latitude[other]])[0])) > 1200
                   for other in chosen):
                chosen.append(position)
                break
    if len(chosen) < SHELTER_COUNT - 2:
        raise ValueError("Could not spread the exercise shelters around the flood edge.")

    demand = {item["nodeId"]: item["people"] for item in people["nodes"]}
    shelter_points = graph.project(graph.longitude[chosen], graph.latitude[chosen])
    share = np.zeros(len(chosen))
    if demand:
        positions = np.array([graph.index[node] for node in demand])
        _, closest = cKDTree(shelter_points).query(graph.project(graph.longitude[positions], graph.latitude[positions]))
        np.add.at(share, closest, list(demand.values()))
    capacities = [max(200, int(math.ceil(value * 1.25 / 50) * 50)) for value in share]
    without_ramp = set(np.argsort(capacities, kind="stable")[:WITHOUT_STEP_FREE_ACCESS].tolist())
    items = []
    for number, position in enumerate(chosen):
        capacity = capacities[number]
        items.append({
            "id": f"shelter-{chr(ord('a') + number)}",
            "name": f"Punkt ćwiczeniowy {chr(ord('A') + number)}",
            "longitude": round(float(graph.longitude[position]), 6),
            "latitude": round(float(graph.latitude[position]), 6),
            "nodeId": graph.nodes[position]["id"],
            "capacity": capacity,
            "intakePerHour": max(100, int(math.ceil(capacity / 2 / 50) * 50)),
            "stepFree": number not in without_ramp,
        })
    return {
        "sheltersVersion": shelter_file.SHELTERS_VERSION,
        "scenario": graph.document["scenario"],
        "provenance": {
            "source": "experiments/routing/prepare_inputs.py", "mode": "synthetic",
            "preparedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            "note": ("Exercise shelters on dry nodes 300-1500 m behind the observed flood edge, with at most "
                     f"{MAX_HAZARD_SHARE:.0%} hazard nodes within {SURROUNDING_METRES} m and at least "
                     f"{MIN_SOURCE_METRES} m from the flood source. Capacity is 1.25 times "
                     "the GHSL estimate nearest each point; intake fills it in about two hours. Not real facilities."),
        },
        "shelters": items,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--graph", type=Path, default=DEFAULT_GRAPH)
    parser.add_argument("--output", type=Path, default=OUTPUT)
    parser.add_argument("--ghsl", default=GHSL_URL, help="GHS-POP GeoTIFF path or HTTPS URL")
    parser.add_argument("--ems-depth", type=Path, default=EMS_DEPTH)
    parser.add_argument("--osm", type=Path, default=OSM_EXTRACT, help="Local OSM extract with the flood source")
    parser.add_argument("--source-way", type=int, default=FLOOD_SOURCE_WAY)
    parser.add_argument("--steps", nargs="+", choices=("population", "depth", "source", "shelters"),
                        default=["population", "depth", "source", "shelters"])
    args = parser.parse_args()
    try:
        graph = EvacuationGraph.load(args.graph)
        if "population" in args.steps:
            result = population(graph, args.ghsl)
            write_json(args.output / "population.json", result)
            totals = result["totals"]
            print(f"population: {totals['inObservedFlood']:,} people in the observed flood "
                  f"({totals['inWindow']:,} in the window) on {totals['nodes']:,} nodes; "
                  f"snap p95 {totals['p95SnapMetres']} m, max {totals['maxSnapMetres']} m")
        if "depth" in args.steps:
            result = flood_depth(graph, args.ems_depth)
            write_json(args.output / "flood-depth.geojson", result)
            print("depth: " + ", ".join(feature["properties"]["depthClass"] for feature in result["features"]))
        if "source" in args.steps:
            result = flood_source(graph, args.osm, args.source_way)
            write_json(args.output / "flood-source.geojson", result)
            print(f"source: OSM way {args.source_way} ({result['features'][0]['properties']['name']})")
        if "shelters" in args.steps:
            people = json.loads((args.output / "population.json").read_text(encoding="utf-8"))
            source_path = args.output / "flood-source.geojson"
            source = json.loads(source_path.read_text(encoding="utf-8")) if source_path.exists() else None
            document = exercise_shelters(graph, people, source)
            accepted, rejected = shelter_file.validate(document, graph)
            if rejected:
                raise ValueError(f"Generated shelters failed validation: {rejected}")
            write_json(args.output / "shelters.synthetic.json", document)
            print(f"shelters: {len(accepted)} synthetic, total capacity "
                  f"{sum(item['capacity'] for item in accepted):,}")
    except (OSError, KeyError, ValueError, rasterio.errors.RasterioError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
