"""Prepare the offline Warsaw exercise graph from a public OpenStreetMap extract."""

import argparse
from collections import defaultdict
from datetime import datetime, timezone
from heapq import heappop, heappush
import json
import math
from pathlib import Path
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "data/demo/network/graph.json"
SCENARIO = json.loads((ROOT / "data/demo/scenario.json").read_text(encoding="utf-8"))
BOUNDS = SCENARIO["mapBounds"]
EXITS = SCENARIO["exits"]
ANCHORS = {
    "walk-start": ("Punkt zbiórki · Praga", 21.0411, 52.2419),
    "pickup": ("Punkt przesiadkowy", 21.0391, 52.2432),
    "east-junction": ("Dojazd do mostu", 21.0376, 52.2432),
    "exit-east": ("Powiśle · wyjście przez most", 21.0307, 52.2406),
    "north-junction": ("Dojście północne", 21.0396574, 52.2478063),
    "exit-north": ("Praga · wyjście w głębi lądu", 21.0440, 52.2481),
}
CORRIDORS = [
    ("walk-to-pickup", "walk-start", "pickup", True, False, False),
    ("east-link", "pickup", "east-junction", True, True, True),
    ("bridge-2", "east-junction", "exit-east", True, True, True),
    ("north-link", "pickup", "north-junction", True, True, True),
    ("north-exit-link", "north-junction", "exit-north", True, True, True),
    ("outside-connector", "exit-east", "exit-north", False, True, True),
]
BOXES = [
    (21.012, 52.237, 21.041, 52.246), (21.040, 52.235, 21.053, 52.249),
    (21.022, 52.245, 21.044, 52.255), (21.025, 52.227, 21.050, 52.238),
]
OSM_API = "https://api.openstreetmap.org/api/0.6/map"
OVERPASS = "https://maps.mail.ru/osm/tools/overpass/api/interpreter"
DRIVING = {"primary", "secondary", "tertiary", "residential", "living_street", "service",
           "unclassified", "primary_link", "secondary_link", "tertiary_link"}
WALKING = DRIVING | {"footway", "pedestrian", "path", "steps", "cycleway", "track"}
CONTEXT = {"primary", "secondary", "tertiary", "primary_link", "secondary_link", "tertiary_link", "residential"}


def distance(a, b):
    return math.hypot((a[0] - b[0]) * 111_320 * math.cos(math.radians((a[1] + b[1]) / 2)),
                      (a[1] - b[1]) * 111_320)


def download(url):
    with urlopen(Request(url, headers={"User-Agent": "CASSINI-Warsaw-demo/1.0"}), timeout=90) as response:
        return response.read()


def read_data(files):
    nodes, ways = {}, {}
    for path in files:
        raw = path.read_bytes()
        if raw.lstrip().startswith(b"<"):
            root = ET.fromstring(raw)
            for node in root.iterfind("node"):
                nodes[int(node.get("id"))] = (float(node.get("lon")), float(node.get("lat")))
            for way in root.iterfind("way"):
                tags = {tag.get("k"): tag.get("v") for tag in way.iterfind("tag")}
                if "highway" in tags or tags.get("waterway") == "river":
                    ways[int(way.get("id"))] = ([int(nd.get("ref")) for nd in way.iterfind("nd")], tags)
            continue

        for way in json.loads(raw)["elements"]:
            if way["type"] != "way" or "geometry" not in way:
                continue
            ids = way["nodes"]
            for node, point in zip(ids, way["geometry"], strict=True):
                nodes[node] = (point["lon"], point["lat"])
            ways[way["id"]] = (ids, way["tags"])

    if not ways:
        raise ValueError("The OSM extract contains no roads.")
    return nodes, ways


def links(nodes, ways, driving):
    graph = defaultdict(list)
    for way, (ids, tags) in sorted(ways.items()):
        road = tags.get("highway")
        mode = tags.get("motor_vehicle", tags.get("vehicle")) if driving else tags.get("foot")
        permitted = mode in {"yes", "designated", "permissive", "official"}
        if (road not in (DRIVING if driving else WALKING) or mode in {"private", "no"}
                or not permitted and tags.get("access") in {"private", "no", "customers", "destination"}
                or not driving and road == "cycleway" and not (permitted or tags.get("segregated") == "yes")):
            continue
        direction = tags.get("oneway", "yes" if tags.get("junction") == "roundabout" else "no") if driving else tags.get("oneway:foot")
        speed = 1.25
        if driving:
            raw_speed = tags.get("maxspeed", "")
            speed = min(int(raw_speed), 35) / 3.6 if raw_speed.isdecimal() and int(raw_speed) > 0 else 25 / 3.6
        is_bridge = tags.get("bridge", "no") != "no" and any(
            21.032 <= nodes[node][0] <= 21.0375 and 52.240 <= nodes[node][1] <= 52.244
            for node in ids if node in nodes
        )
        for a, b in zip(ids, ids[1:]):
            if a not in nodes or b not in nodes:
                continue
            seconds = distance(nodes[a], nodes[b]) / speed
            if direction != "-1":
                graph[a].append((b, seconds, way, is_bridge))
            if direction not in {"yes", "true", "1"}:
                graph[b].append((a, seconds, way, is_bridge))
    return graph


def corridor(graph, start, end, bridge):
    first = (start, False)
    queue = [(0, *first)]
    costs, parents = {first: 0}, {}
    while queue:
        seconds, node, crossed = heappop(queue)
        state = (node, crossed)
        if seconds != costs[state]:
            continue
        if node == end and crossed == bridge:
            points, ways = [node], []
            while state != first:
                state, way = parents[state]
                points.append(state[0])
                ways.append(way)
            return round(seconds), list(reversed(points)), sorted(set(ways))
        for target, cost, way, on_bridge in graph[node]:
            if on_bridge and not bridge:
                continue
            next_state = (target, crossed or on_bridge)
            duration = seconds + cost
            if duration < costs.get(next_state, math.inf):
                costs[next_state] = duration
                parents[next_state] = (state, way)
                heappush(queue, (duration, *next_state))
    raise ValueError(f"No {'bridge' if bridge else 'non-bridge'} corridor between OSM nodes {start} and {end}.")


def build(nodes, ways, timestamp):
    modes = {driving: links(nodes, ways, driving) for driving in (False, True)}
    snapped = {driving: {key: min(graph, key=lambda node: distance(nodes[node], anchor[1:]))
                         for key, anchor in ANCHORS.items()} for driving, graph in modes.items()}
    snapped[True]["east-junction"] = 32528888  # OSM node on the two-way approach near the bridge.
    points = [{"id": key, "label": anchor[0], "longitude": nodes[snapped[False][key]][0],
               "latitude": nodes[snapped[False][key]][1]} for key, anchor in ANCHORS.items()]
    segments = []
    for name, start, end, walking, driving, both_ways in CORRIDORS:
        segment = {"id": name, "from": start, "to": end, "bothWays": both_ways}
        for enabled, vehicle, reverse, time_field, geometry_field in (
            (walking, False, False, "walkingSeconds", "geometry"),
            (driving, True, False, "drivingSeconds", "drivingGeometry"),
            (driving and both_ways, True, True, "reverseDrivingSeconds", "reverseDrivingGeometry"),
        ):
            if not enabled:
                continue
            a, b = (end, start) if reverse else (start, end)
            seconds, path, used_ways = corridor(modes[vehicle], snapped[vehicle][a], snapped[vehicle][b],
                                                name == "bridge-2")
            segment[time_field] = seconds
            segment[geometry_field] = [nodes[node] for node in path]
            segment[geometry_field + "OsmWays"] = used_ways
        segments.append(segment)
    bounds = BOUNDS
    context = []
    water = []
    for _, (ids, tags) in sorted(ways.items()):
        coordinates = [nodes[node] for node in ids if node in nodes]
        if len(coordinates) < 2:
            continue
        if not any(bounds[0] <= x <= bounds[2] and bounds[1] <= y <= bounds[3] for x, y in coordinates):
            continue
        if tags.get("highway") in CONTEXT:
            context.append(coordinates)
        if tags.get("waterway") == "river" and tags.get("name") == "Wisła":
            water.append(coordinates)
    return {"nodes": points, "segments": segments, "exits": EXITS, "bounds": bounds,
            "contextRoads": context, "waterways": water, "attribution": "© OpenStreetMap contributors · ODbL",
            "osmTimestamp": timestamp}


def check(graph):
    node_ids = {node["id"] for node in graph["nodes"]}
    assert len(node_ids) == len(graph["nodes"]) and set(EXITS) <= node_ids, "invalid nodes"
    segments = {segment["id"]: segment for segment in graph["segments"]}
    assert len(segments) == len(graph["segments"]), "duplicate segment ID"
    assert all({s["from"], s["to"]} <= node_ids for s in segments.values()), "dangling segment"
    for segment in segments.values():
        for time, geometry in (("walkingSeconds", "geometry"), ("drivingSeconds", "drivingGeometry"),
                               ("reverseDrivingSeconds", "reverseDrivingGeometry")):
            if time in segment:
                assert segment[time] > 0 and len(segment[geometry]) > 1, f"empty {time} on {segment['id']}"

    def reachable(closed):
        nodes = {"walk-start"}
        for _ in node_ids:
            for item in segments.values():
                if item["id"] in closed or not item.get("walkingSeconds"):
                    continue
                if item["from"] in nodes:
                    nodes.add(item["to"])
                if item["bothWays"] and item["to"] in nodes:
                    nodes.add(item["from"])
        return nodes.intersection(EXITS)

    assert reachable(set()) == set(EXITS), "both exits must be reachable"
    assert reachable({"bridge-2"}) == {"exit-north"}, "closing the bridge must leave the inland exit"
    assert not reachable({"bridge-2", "north-link"}), "both closures must block all exercise exits"


def write_graph(path, graph):
    fields = []
    for name, value in graph.items():
        if name in {"nodes", "segments", "contextRoads", "waterways"} and value:
            rows = ",\n    ".join(json.dumps(item, ensure_ascii=False) for item in value)
            encoded = "[\n    " + rows + "\n  ]"
        else:
            encoded = json.dumps(value, ensure_ascii=False)
        fields.append(f'  "{name}": {encoded}')
    path.write_text("{\n" + ",\n".join(fields) + "\n}\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--input", nargs="+", type=Path, help="Local Overpass JSON or OSM XML extracts")
    source.add_argument("--download", action="store_true", help="Download the Warsaw OSM extracts")
    source.add_argument("--overpass", action="store_true", help="Download an equivalent Overpass extract")
    source.add_argument("--check", type=Path)
    parser.add_argument("--output", type=Path, default=OUTPUT)
    parser.add_argument("--osm-timestamp", help="UTC timestamp of the input snapshot or download")
    args = parser.parse_args()
    if args.check:
        check(json.loads(args.check.read_text(encoding="utf-8")))
        print("Warsaw graph OK")
        return
    inputs = args.input
    if args.download or args.overpass:
        from urllib.parse import urlencode
        cache = ROOT / "artifacts" / "osm"
        cache.mkdir(parents=True, exist_ok=True)
        urls = [f"{OSM_API}?bbox={','.join(map(str, box))}" for box in BOXES]
        if args.overpass:
            query = "[out:json][timeout:60];(way[highway](52.227,21.012,52.255,21.053);way[waterway=river](52.227,21.012,52.255,21.053););out body geom;"
            urls = [OVERPASS + "?" + urlencode({"data": query})]
        inputs = []
        for index, url in enumerate(urls):
            path = cache / f"{index}.{'json' if args.overpass else 'osm'}"
            path.write_bytes(download(url))
            inputs.append(path)
    if args.input and not args.osm_timestamp:
        parser.error("--input requires --osm-timestamp in UTC")
    timestamp = args.osm_timestamp or datetime.now(timezone.utc).isoformat(timespec="seconds")
    graph = build(*read_data(inputs), timestamp)
    check(graph)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    write_graph(args.output, graph)
    print(args.output)


if __name__ == "__main__":
    main()
