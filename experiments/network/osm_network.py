"""Read OSM extracts and build the offline street network."""

from collections import Counter, defaultdict
import json
import math
from pathlib import Path
import shutil
from urllib.request import Request, urlopen
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
DRIVING = {"primary", "secondary", "tertiary", "residential", "living_street", "service",
           "unclassified", "primary_link", "secondary_link", "tertiary_link"}
WALKING = DRIVING | {"footway", "pedestrian", "path", "steps", "cycleway", "track"}
CAR = DRIVING | {"motorway", "motorway_link", "trunk", "trunk_link"}
FOOT = WALKING
MAJOR = {"motorway", "motorway_link", "trunk", "trunk_link", "primary", "primary_link", "secondary", "secondary_link"}


def distance(a, b):
    return math.hypot((a[0] - b[0]) * 111_320 * math.cos(math.radians((a[1] + b[1]) / 2)),
                      (a[1] - b[1]) * 111_320)


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


def touches(points, area):
    west, south, east, north = area
    return (min(p[0] for p in points) <= east and max(p[0] for p in points) >= west
            and min(p[1] for p in points) <= north and max(p[1] for p in points) >= south)


def fetch(path, source):
    partial = path.with_suffix(path.suffix + ".part")
    req = Request(source, headers={"User-Agent": "CASSINI-offline-map/1.0"})
    with urlopen(req, timeout=120) as response, partial.open("wb") as target:
        shutil.copyfileobj(response, target)
    partial.replace(path)


def read_pbf(path, area):
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


def build(nodes, ways, timestamp, area, describe_edge):
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
            if not all(area[0] <= x <= area[2] and area[1] <= y <= area[3] for x, y in points):
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
            describe_edge(edge, tags, metres)
            roads.append(edge)
    distinct = {road["id"]: road for road in roads}
    return {"bounds": area, "osmTimestamp": timestamp, "roads": list(distinct.values()), "waterways": waterways}


def svg_map(network):
    west, south, east, north = network["bounds"]
    longitude_scale = math.cos(math.radians((south + north) / 2))
    ratio = 10000
    width = (east - west) * longitude_scale * ratio
    height = (north - south) * ratio

    def path(points):
        return "M" + " ".join(f"{(x - west) * longitude_scale * ratio:.1f},{(north - y) * ratio:.1f}" for x, y in points)

    styles = {"walking": ("#52746a", 0.9), False: ("#6f8993", 1.5), True: ("#c1c9c3", 3.3)}
    roads = defaultdict(list)
    for link in network["roads"]:
        group = link["major"] if any(link["drive"]) else "walking"
        roads[group].append(path(link["geometry"]))
    water = "".join(path(line) for line in network["waterways"])
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" preserveAspectRatio="none">'
            f'<path fill="none" stroke="#4b89aa" stroke-width="12" d="{water}"/>'
            + "".join(f'<path fill="none" stroke="{colour}" stroke-width="{weight}" d="{"".join(roads[major])}"/>'
                      for major, (colour, weight) in styles.items()) + "</svg>\n")
