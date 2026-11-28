"""Download and build an offline OpenStreetMap street network for the Warsaw AOI."""

import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import gzip
import json
import math
from pathlib import Path
import shutil
import tempfile
from urllib.request import Request, urlopen

from osm_to_graph import ROOT, SCENARIO, WALKING, DRIVING, distance, read_data


AREA = SCENARIO["evidenceAoi"]
OUTPUT = ROOT / "artifacts/warsaw-city"
SOURCE = "https://download.geofabrik.de/europe/poland/mazowieckie-latest.osm.pbf"
CAR = DRIVING | {"motorway", "motorway_link", "trunk", "trunk_link"}
FOOT = WALKING
MAJOR = {"motorway", "motorway_link", "trunk", "trunk_link", "primary", "primary_link", "secondary", "secondary_link"}


def touches(points, area=AREA):
    west, south, east, north = area
    return (min(p[0] for p in points) <= east and max(p[0] for p in points) >= west
            and min(p[1] for p in points) <= north and max(p[1] for p in points) >= south)


def fetch(path, source=SOURCE):
    partial = path.with_suffix(path.suffix + ".part")
    req = Request(source, headers={"User-Agent": "CASSINI-offline-map/1.0"})
    with urlopen(req, timeout=120) as response, partial.open("wb") as target:
        shutil.copyfileobj(response, target)
    partial.replace(path)


def read_pbf(path, area=AREA):
    import osmium

    nodes, ways = {}, {}

    class Collector(osmium.SimpleHandler):
        def way(self, way):
            road = way.tags.get("highway")
            if road not in CAR | FOOT and way.tags.get("waterway") != "river":
                return
            if not all(node.location.valid() for node in way.nodes):
                return
            points = [(node.lon, node.lat) for node in way.nodes]
            if len(points) >= 2 and touches(points, area):
                ids = [node.ref for node in way.nodes]
                nodes.update(zip(ids, points))
                ways[way.id] = (ids, dict(way.tags))

    Collector().apply_file(str(path), locations=True)
    with osmium.io.Reader(str(path)) as reader:
        source_timestamp = reader.header().get("osmosis_replication_timestamp")
    return nodes, ways, source_timestamp


def direction_cost(tags, seconds, car):
    mode = tags.get("motorcar", tags.get("motor_vehicle", tags.get("vehicle"))) if car else tags.get("foot")
    allowed = {"yes", "designated", "permissive", "official"}
    restricted = {"no", "private", "destination", "customers", "agricultural", "forestry"}
    road = tags.get("highway")
    if (road not in (CAR if car else FOOT) or mode in restricted
            or (mode not in allowed and tags.get("access") in restricted)
            or (not car and road == "cycleway" and mode not in allowed and tags.get("segregated") != "yes")):
        return [None, None]
    if car:
        one_way = tags.get("oneway:motor_vehicle", tags.get("oneway"))
        if one_way is None and (tags.get("junction") in {"roundabout", "circular"} or road == "motorway"):
            one_way = "yes"
    else:
        one_way = tags.get("oneway:foot")
    return [seconds if one_way != "-1" else None, seconds if one_way not in {"yes", "true", "1"} else None]


def build(nodes, ways, timestamp, area=AREA, describe_edge=None):
    histogram = Counter(node for ids, tags in ways.values()
                        if tags.get("highway") in CAR | FOOT for node in ids)
    roads, waterways = [], []
    for way, (ids, tags) in sorted(ways.items()):
        road = tags.get("highway")
        if len(ids) < 2 or any(node not in nodes for node in ids):
            continue
        if tags.get("waterway") == "river":
            points = [nodes[node] for node in ids]
            if touches(points, area):
                waterways.append(points)
        if road not in CAR | FOOT:
            continue
        start = 0
        for end in range(1, len(ids)):
            if histogram[ids[end]] < 2 and end < len(ids) - 1 and ids[end] != ids[start]:
                continue
            section = ids[start:end + 1]
            start = end
            points = [nodes[node] for node in section]
            if not touches(points, area):
                continue
            if describe_edge and not all(
                area[0] <= x <= area[2] and area[1] <= y <= area[3] for x, y in points
            ):
                continue
            metres = sum(distance(a, b) for a, b in zip(points, points[1:]))
            if metres <= 0:
                continue
            raw_speed = tags.get("maxspeed", "")
            speed = min(int(raw_speed), 70) if raw_speed.isdecimal() and int(raw_speed) > 0 else (45 if road in MAJOR else 25)
            walk = direction_cost(tags, max(1, round(metres / 1.25)), car=False)
            drive = direction_cost(tags, max(1, round(metres / (speed / 3.6))), car=True)
            if not any(walk + drive):
                continue
            # The way and its two endpoint nodes give clients an auditable OSM identifier.
            identifier = f"osm-{way}-{section[0]}-{section[-1]}"
            edge = {"id": identifier, "from": section[0], "to": section[-1],
                          "name": tags.get("name"), "major": road in MAJOR, "class": road,
                          "walk": walk, "drive": drive, "geometry": points}
            if describe_edge:
                describe_edge(edge, tags, metres)
            roads.append(edge)
    distinct = {road["id"]: road for road in roads}
    return {"bounds": area, "osmTimestamp": timestamp, "roads": list(distinct.values()), "waterways": waterways}


def svg_map(network, show_paths=False):
    west, south, east, north = network["bounds"]
    longitude_scale = math.cos(math.radians((south + north) / 2))
    ratio = 10000
    width = (east - west) * longitude_scale * ratio
    height = (north - south) * ratio

    def path(points):
        return "M" + " ".join(f"{(x - west) * longitude_scale * ratio:.1f},{(north - y) * ratio:.1f}" for x, y in points)

    styles = {False: ("#507079", 2.15), True: ("#8da7ac", 4.6)}
    roads = defaultdict(list)
    for link in network["roads"]:
        if show_paths and not any(link["drive"]):
            roads["walking"].append(path(link["geometry"]))
        elif show_paths or (link["class"] != "service" and any(link["drive"])):
            roads[link["major"]].append(path(link["geometry"]))
    if show_paths:
        styles = {"walking": ("#52746a", 0.9), False: ("#6f8993", 1.5), True: ("#c1c9c3", 3.3)}
    water = "".join(path(line) for line in network["waterways"])
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" preserveAspectRatio="none">'
            f'<path fill="none" stroke="#4b89aa" stroke-width="12" d="{water}"/>'
            + "".join(f'<path fill="none" stroke="{colour}" stroke-width="{weight}" d="{"".join(roads[major])}"/>'
                      for major, (colour, weight) in styles.items()) + "</svg>\n")


def prepare(output, inputs=None, refresh=False):
    output.mkdir(parents=True, exist_ok=True)
    timestamp = None
    if inputs:
        nodes, ways = read_data(inputs)
    else:
        pbf = output / "mazowieckie.osm.pbf"
        if refresh or not pbf.exists():
            fetch(pbf)
        nodes, ways, timestamp = read_pbf(pbf)
    timestamp = timestamp or datetime.now(timezone.utc).isoformat(timespec="seconds")
    network = build(nodes, ways, timestamp)
    if not network["roads"]:
        raise ValueError("No OSM roads intersect the Warsaw AOI.")
    with tempfile.TemporaryDirectory(dir=output) as temp:
        temp = Path(temp)
        with gzip.open(temp / "city-network.json.gz", "wt", encoding="utf-8") as stream:
            json.dump(network, stream, ensure_ascii=False, separators=(",", ":"))
        (temp / "city-map.svg").write_text(svg_map(network), encoding="utf-8")
        (temp / "city-map.svg").replace(output / "city-map.svg")
        (temp / "city-network.json.gz").replace(output / "city-network.json.gz")
    print(f"OSM: {len(network['roads']):,} road sections, {network['bounds']}, snapshot {timestamp}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=OUTPUT)
    parser.add_argument("--input", nargs="+", type=Path, help="Use local OSM XML / Overpass exports for testing")
    parser.add_argument("--refresh", action="store_true", help="Refresh the Geofabrik extract before preparation")
    args = parser.parse_args()
    prepare(args.output, args.input, args.refresh)


if __name__ == "__main__":
    main()
