"""Package the Valencia radar-area graph for the task 03 routing model."""

import argparse
from collections import Counter
import gzip
import json
from pathlib import Path
from uuid import uuid4
from zipfile import ZIP_DEFLATED, ZipFile

from shapely import prepare
from shapely.geometry import LineString, box, mapping, shape

from osm_to_graph import distance
from prepare_valencia import EVIDENCE, OUTPUT, ROOT, flood_layers, polygon_parts


INPUT_VERSION = "cassini.evacuation-input.v1"


def build_graph(city, evidence, state_path):
    with gzip.open(city / "city-network.json.gz", "rt", encoding="utf-8") as stream:
        network = json.load(stream)
    state = json.loads(state_path.read_bytes())
    if network["scenario"] != "valencia" or state["scenario"] != "valencia":
        raise ValueError("The network and Situation state must both belong to Valencia.")
    snapshot = state["current"]
    imagery = json.loads((city / "city-imagery.json").read_bytes())
    scenes = imagery["scenes"]
    bounds = scenes["event"]["bounds"]
    if bounds != scenes["before"]["bounds"]:
        raise ValueError("The radar scenes must share the same extent.")
    window = box(*bounds)

    def inside(point):
        lon, lat = point
        return bounds[0] <= lon <= bounds[2] and bounds[1] <= lat <= bounds[3]

    _, coverage = flood_layers(evidence / "emsr773-aoi01")
    fully_surveyed = coverage.covers(window)
    prepare(coverage)
    nodes = {node["id"]: node for node in network["nodes"] if inside((node["longitude"], node["latitude"]))}
    closed = set(snapshot["closedSegments"])
    roads, incident, boundary = [], set(), set()
    for road in network["roads"]:
        endpoints = {road["from"], road["to"]}
        inside_endpoints = endpoints & nodes.keys()
        if not inside_endpoints:
            continue
        if inside_endpoints != endpoints or not all(map(inside, road["geometry"])):
            boundary.update(inside_endpoints)
            continue
        if road["intersectsObservedFlood"]:
            zone = "hazard"
        else:
            surveyed = fully_surveyed or coverage.covers(LineString(road["geometry"]))
            zone = "safe" if surveyed else "unknown"
        metres = road["lengthMetres"]
        if metres == 0:
            # Older graphs rounded links shorter than 5 cm to zero.
            metres = sum(distance(a, b) for a, b in zip(road["geometry"], road["geometry"][1:]))
        roads.append({**road, "lengthMetres": metres, "zone": zone, "closed": road["id"] in closed})
        incident.update(endpoints)
    if not roads:
        raise ValueError("No OSM roads were found inside the radar rectangle.")
    hazard_seed = json.loads((city / "hazard-seed.json").read_bytes())
    seed_geometries = [feature["geometry"] for feature in hazard_seed["geoJson"]["features"]]
    layers = snapshot["hazards"]
    if (len(layers) != 1 or layers[0]["productId"] != hazard_seed["productId"]
            or layers[0]["geometry"] != {"type": "GeometryCollection", "geometries": seed_geometries}):
        raise ValueError("Situation uses different hazard geometry. Rebuild the Valencia graph for the selected observation.")
    clipped = [mapping(polygon) for geometry in seed_geometries
               for polygon in polygon_parts(shape(geometry).intersection(window))]
    evidence_by_road = snapshot.get("closureEvidence") or {}
    return {
        "inputVersion": INPUT_VERSION, "scenario": "valencia",
        "bounds": bounds, "osmTimestamp": network["osmTimestamp"],
        "situationRevision": snapshot["revision"],
        "hazardObservation": {
            "productId": hazard_seed["productId"], "sourceUrl": hazard_seed["sourceUrl"],
            "source": layers[0]["source"], "acquisitionsUtc": imagery["hazardAcquisitions"],
            "coverage": mapping(coverage.intersection(window)),
            "geoJson": {"type": "FeatureCollection", "features": [
                {"type": "Feature", "properties": {}, "geometry": geometry} for geometry in clipped
            ]},
        },
        "closureEvidence": [{"roadId": road["id"], **evidence_by_road[road["id"]]}
                            for road in roads if road["closed"] and road["id"] in evidence_by_road],
        "boundaryNodeIds": sorted(boundary & incident),
        "nodes": [node for node_id, node in nodes.items() if node_id in incident],
        "roads": roads,
    }


def node_features(nodes):
    for node in nodes:
        yield {
            "type": "Feature", "properties": {"id": str(node["id"]), "zone": node["zone"]},
            "geometry": {"type": "Point", "coordinates": [node["longitude"], node["latitude"]]},
        }


def road_features(roads):
    for road in roads:
        yield {
            "type": "Feature",
            "properties": {key: road[key] for key in ("id", "class", "zone", "closed")},
            "geometry": {"type": "LineString", "coordinates": road["geometry"]},
        }


def write_package(output, graph):
    output.parent.mkdir(parents=True, exist_ok=True)
    staging = output.with_name(f".{output.name}.{uuid4().hex}.tmp")
    try:
        with ZipFile(staging, "w", compression=ZIP_DEFLATED) as archive:
            archive.write(ROOT / "data/local/valencia-task03.md", "README.md")
            for name in ("evacuation-input.v1.schema.json", "evacuation-input.v1.example.json"):
                archive.write(ROOT / "data/demo/routing" / name, name)
            archive.writestr("valencia-graph.json", json.dumps(graph, ensure_ascii=False, separators=(",", ":")))
            for name, features in (("nodes", node_features(graph["nodes"])), ("roads", road_features(graph["roads"]))):
                geojson = {"type": "FeatureCollection", "features": list(features)}
                archive.writestr(f"{name}.geojson", json.dumps(geojson, separators=(",", ":")))
        staging.replace(output)
    finally:
        staging.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--city", type=Path, default=OUTPUT)
    parser.add_argument("--evidence", type=Path, default=EVIDENCE)
    parser.add_argument("--state", type=Path, default=ROOT / "artifacts/valencia-demo/situation.json")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/valencia-task03.zip")
    args = parser.parse_args()
    try:
        graph = build_graph(args.city, args.evidence, args.state)
        write_package(args.output, graph)
    except (OSError, KeyError, ValueError) as error:
        parser.exit(1, f"{error}\n")
    nodes, roads = graph["nodes"], graph["roads"]
    print(f"{args.output}: {len(nodes):,} nodes {dict(Counter(n['zone'] for n in nodes))}; "
          f"{len(roads):,} roads {dict(Counter(r['zone'] for r in roads))}; "
          f"{sum(r['closed'] for r in roads):,} closures at Situation revision {graph['situationRevision']}.")


if __name__ == "__main__":
    main()
