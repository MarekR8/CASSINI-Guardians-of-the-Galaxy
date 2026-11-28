"""Draw the task 03 inputs for a visual check: deadlines, estimated people, flood source and shelters.

Writes experiments/routing/out/inputs.png (ignored by Git). Requires matplotlib.
"""

import argparse
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
from shapely.geometry import shape

import deadlines
from graph import DEFAULT_GRAPH, EvacuationGraph
import shelters as shelter_file

OUT = Path(__file__).resolve().parent / "out"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--graph", type=Path, default=DEFAULT_GRAPH)
    parser.add_argument("--inputs", type=Path, default=deadlines.INPUTS)
    parser.add_argument("--shelters", type=Path, help="Shelter file (default: shelters.synthetic.json)")
    parser.add_argument("--output", type=Path, default=OUT / "inputs.png")
    args = parser.parse_args()

    graph = EvacuationGraph.load(args.graph)
    load = deadlines.load_json
    depth = load(args.inputs / "flood-depth.geojson")
    source = load(args.inputs / "flood-source.geojson")
    population = load(args.inputs / "population.json")
    deadline = deadlines.compute(graph, depth, source, load(deadlines.PARAMS))
    shelters = shelter_file.load(args.shelters or args.inputs / "shelters.synthetic.json", graph)

    fig, ax = plt.subplots(figsize=(13, 8.5), dpi=110)
    for road in graph.roads[::2]:
        xs, ys = zip(*road["geometry"])
        ax.plot(xs, ys, color="#d4d4d4", lw=0.3, zorder=1)
    for feature in depth["features"]:
        geometry = shape(feature["geometry"])
        for part in getattr(geometry, "geoms", [geometry]):
            ax.fill(*part.exterior.xy, color="#9ecae1", alpha=.5, lw=0, zorder=2)
    line = shape(source["features"][0]["geometry"])
    for part in getattr(line, "geoms", [line]):
        ax.plot(*part.xy, color="#08519c", lw=2.2, zorder=3)
    ax.plot([], [], color="#08519c", lw=2.2, label="flood source (OSM " + str(source["features"][0]["properties"]["name"]) + ")")
    ax.fill([], [], color="#9ecae1", alpha=.5, label="EMS flood depth layer")

    positions = [graph.index[item["nodeId"]] for item in population["nodes"]]
    minutes = [deadline[position][0] for position in positions]
    sizes = [max(2, item["people"] / 12) for item in population["nodes"]]
    dots = ax.scatter(graph.longitude[positions], graph.latitude[positions], c=minutes, s=sizes, cmap="RdYlGn",
                      vmin=15, vmax=60, alpha=.75, lw=0, zorder=4)
    fig.colorbar(dots, ax=ax, shrink=.6, label="deadline to leave the flood (minutes, model assumption)")

    for item in shelters["shelters"]:
        ax.scatter(item["longitude"], item["latitude"], marker="s" if item["stepFree"] else "X", s=170,
                   color="#3f007d", edgecolor="white", zorder=6)
        ax.annotate(f"{item['id']} · {item['capacity']:,}", (item["longitude"], item["latitude"]), xytext=(6, 6),
                    textcoords="offset points", fontsize=9, weight="bold", zorder=7)
    ax.scatter([], [], marker="s", color="#3f007d", label="shelter, step-free")
    ax.scatter([], [], marker="X", color="#3f007d", label="shelter, no step-free access")
    west, south, east, north = graph.bounds
    ax.set_xlim(west, east)
    ax.set_ylim(south, north)
    ax.set_aspect(graph.scale ** -1 * 111_320)
    ax.legend(loc="lower left", fontsize=8)
    mode = shelters["provenance"]["mode"]
    ax.set_title(f"Task 03 inputs · GHSL estimate {sum(i['people'] for i in population['nodes']):,.0f} people · "
                 f"{len(shelters['shelters'])} shelters ({mode})")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    fig.tight_layout()
    fig.savefig(args.output)
    print(f"wrote {args.output}")


if __name__ == "__main__":
    main()
