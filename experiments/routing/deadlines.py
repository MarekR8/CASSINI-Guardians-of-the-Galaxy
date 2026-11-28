"""Assign each hazard node the time by which it must be left (rule C: flood depth or distance from the flood source).

A node inside an EMS depth class gets that class's deadline. Every hazard node also gets a deadline that grows with
the straight-line distance from the channel that overflowed. The earlier of the two applies. Both are model
assumptions recorded in data/demo/routing/params.json; neither is a forecast.
"""

import argparse
from collections import Counter
import json
from pathlib import Path

import numpy as np
from shapely import distance as shapely_distance, points, prepare
from shapely.affinity import scale
from shapely.geometry import shape

from graph import DEFAULT_GRAPH, EARTH_METRES, ROOT, EvacuationGraph

INPUTS = ROOT / "data/demo/routing/valencia"
PARAMS = ROOT / "data/demo/routing/params.json"


def load_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def compute(graph, depth_layer, source_layer, params):
    """Return {node position: (minutes, basis)} for every hazard node, basis 'depth <class>' or 'distance <km>'."""
    rules = params["deadline"]
    tiers = sorted(rules["depthMinutes"], key=lambda tier: -tier["minMetres"])
    hazard = np.flatnonzero(graph.zone == "hazard")
    # Work in local metres so that the tolerance and distances are isotropic.
    xy = graph.project(graph.longitude[hazard], graph.latitude[hazard])
    nodes = points(xy)

    def metric(geometry):
        return scale(geometry, xfact=graph.scale, yfact=EARTH_METRES, origin=(0, 0))

    depth_minutes = np.full(hazard.size, np.inf)
    depth_label = np.full(hazard.size, None, dtype=object)
    for feature in depth_layer["features"]:
        minimum = feature["properties"]["minMetres"]
        minutes = next(tier["minutes"] for tier in tiers if minimum >= tier["minMetres"])
        area = metric(shape(feature["geometry"])).buffer(rules["depthToleranceMetres"])
        prepare(area)
        inside = area.contains(nodes) & (minutes < depth_minutes)
        depth_minutes[inside] = minutes
        depth_label[inside] = feature["properties"]["depthClass"]

    source = metric(shape(source_layer["features"][0]["geometry"]))
    kilometres = shapely_distance(nodes, source) / 1000
    distance_rule = rules["distance"]
    distance_minutes = np.minimum(distance_rule["maxMinutes"],
                                  distance_rule["minutesAtSource"] + distance_rule["minutesPerKm"] * kilometres)
    result = {}
    for number, position in enumerate(hazard):
        if depth_minutes[number] <= distance_minutes[number]:
            result[int(position)] = (float(depth_minutes[number]), f"depth {depth_label[number]}")
        else:
            result[int(position)] = (round(float(distance_minutes[number]), 1),
                                     f"distance {kilometres[number]:.2f} km")
    return result


def summary(graph, deadlines, population):
    people = {graph.index[item["nodeId"]]: item["people"] for item in population["nodes"]}
    total = sum(people.values())
    by_basis = Counter()
    bands = Counter()
    for position, count in people.items():
        minutes, basis = deadlines[position]
        by_basis[basis.split()[0]] += count
        bands[min(int(minutes // 15) * 15, 90)] += count
    lines = [f"{len(deadlines):,} hazard nodes; {total:,.0f} estimated people on {len(people):,} populated nodes"]
    lines.append("deciding rule: " + ", ".join(f"{basis} {count / total:.1%}" for basis, count in by_basis.most_common()))
    for start in sorted(bands):
        label = f"{start}-{start + 15} min" if start < 90 else "90 min"
        lines.append(f"  {label:>10}: {bands[start]:>9,.0f} people ({bands[start] / total:5.1%})")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--graph", type=Path, default=DEFAULT_GRAPH)
    parser.add_argument("--inputs", type=Path, default=INPUTS)
    parser.add_argument("--params", type=Path, default=PARAMS)
    parser.add_argument("--output", type=Path, help="Write per-node deadlines as JSON")
    args = parser.parse_args()
    try:
        graph = EvacuationGraph.load(args.graph)
        deadlines = compute(graph, load_json(args.inputs / "flood-depth.geojson"),
                            load_json(args.inputs / "flood-source.geojson"), load_json(args.params))
        print(summary(graph, deadlines, load_json(args.inputs / "population.json")))
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps({"nodes": [
                {"nodeId": graph.nodes[position]["id"], "deadlineMinutes": minutes, "basis": basis}
                for position, (minutes, basis) in sorted(deadlines.items())]}, indent=0) + "\n", encoding="utf-8")
            print(f"wrote {args.output}")
    except (OSError, KeyError, ValueError, StopIteration) as error:
        parser.exit(1, f"{error!r}\n")


if __name__ == "__main__":
    main()
