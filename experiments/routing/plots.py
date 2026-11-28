"""Figures and numbers for the task 03 maths note (docs/routing-math-note.md).

Runs the Valencia reference model for several theta values and for the baseline, then writes small PNGs to
docs/images/routing/ and the numbers to experiments/routing/out/note-numbers.json. About 5 minutes.

    python experiments/routing/plots.py
    python experiments/routing/plots.py --replot    # redraw from out/note-numbers.json without running the model
"""

import json
import sys
from types import SimpleNamespace

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

import forecast as hazard_forecast  # noqa: E402
import model as reference  # noqa: E402
from graph import ROOT  # noqa: E402

IMAGES = ROOT / "docs/images/routing"
THETAS = [0.03, 0.1, 0.3, 1.0, 3.0]
SERIES = ["#257de0", "#f47230", "#00b894"]  # categorical slots 1-3 (validated all-pairs, light surface)
INK, MUTED, GRID = "#191919", "#666666", "#e6e6e6"


def arguments(**changes):
    values = dict(graph=reference.DEFAULT_GRAPH, inputs=reference.deadline_rules.INPUTS,
                  params=reference.deadline_rules.PARAMS, shelters=None, close=[], theta=None,
                  forecast=None, compliance=None, hazard_weight=None)
    values.update(changes)
    return SimpleNamespace(**values)


def summary(result):
    total = sum(result["statusAll"].values())
    return {"onTime": result["statusAll"].get("Ok", 0) / total, "p95": result["clearanceMinutes"]["p95"],
            "noPlace": result["statusAll"].get("ShelterCapacityExhausted", 0),
            "overflow": sum(item["overflow"] for item in result["shelters"]),
            "iterations": [wave["iterations"] for wave in result["waves"]]}


def style(axis, title, ylabel):
    axis.set_title(title, loc="left", color=INK, fontsize=11)
    axis.set_ylabel(ylabel, color=MUTED, fontsize=9)
    axis.tick_params(colors=MUTED, labelsize=8.5, length=0)
    axis.grid(axis="y", color=GRID, linewidth=0.8)
    axis.set_axisbelow(True)
    for side in ("top", "right", "left"):
        axis.spines[side].set_visible(False)
    axis.spines["bottom"].set_color(GRID)


def main():
    if "--replot" in sys.argv:
        draw(json.loads((ROOT / "experiments/routing/out/note-numbers.json").read_text(encoding="utf-8")))
        return
    numbers = {"thetas": THETAS, "theta": {}, "compare": {}}
    runs = {}
    for theta in THETAS:
        print(f"theta {theta} ...", flush=True)
        runs[theta] = reference.build(arguments(theta=theta)).run()
        numbers["theta"][str(theta)] = summary(runs[theta])

    # The most populated independent group that the default theta splits across at least two shelters.
    default = runs[0.3]["nodes"]
    population = {item["nodeId"]: item["people"] for item in
                  json.loads((reference.deadline_rules.INPUTS / "population.json").read_text(encoding="utf-8"))["nodes"]}
    node = next(node for node in sorted(default, key=lambda key: -population.get(int(key), 0))
                if default[node]["independent"]["shares"] and
                sum(value >= 0.1 for value in default[node]["independent"]["shares"].values()) >= 2)
    shelters = sorted(default[node]["independent"]["shares"], key=lambda key: -default[node]["independent"]["shares"][key])[:3]
    curves = {shelter: [runs[theta]["nodes"][node]["independent"]["shares"].get(shelter, 0) for theta in THETAS]
              for shelter in shelters}
    numbers["node"] = {"osmNodeId": node, "shares": curves}

    model = reference.build(arguments())
    baseline = model.run(baseline=True)
    no_weight = reference.build(arguments(hazard_weight=1.0)).run()
    with_forecast = reference.build(arguments(forecast=hazard_forecast.MOCK)).run()
    numbers["compare"] = {"baseline 1/t": summary(baseline), "logit, lambda 1": summary(no_weight),
                          "logit, lambda 5": summary(runs[0.3]), "logit, lambda 5 + forecast": summary(with_forecast)}

    out = ROOT / "experiments/routing/out"
    out.mkdir(exist_ok=True)
    (out / "note-numbers.json").write_text(json.dumps(numbers, indent=1) + "\n", encoding="utf-8")
    draw(numbers)
    print(json.dumps(numbers, indent=1))


def draw(numbers):
    node, curves = numbers["node"]["osmNodeId"], numbers["node"]["shares"]
    IMAGES.mkdir(parents=True, exist_ok=True)
    plt.rcParams.update({"font.family": "DejaVu Sans", "figure.dpi": 150})

    figure, axis = plt.subplots(figsize=(6.4, 3.4))
    for color, (shelter, values) in zip(SERIES, curves.items()):
        axis.plot(THETAS, values, color=color, linewidth=2, marker="o", markersize=5)
        # Direct labels at the low-theta end, where the shares are apart; at high theta two lines meet at 0.
        axis.annotate(shelter, (THETAS[0], values[0]), xytext=(-8, 0), textcoords="offset points",
                      ha="right", va="center", fontsize=8.5, color=INK)
    axis.set_xscale("log")
    axis.set_xticks(THETAS, [str(theta) for theta in THETAS])
    axis.set_ylim(0, 1)
    axis.set_xlim(THETAS[0] * 0.4, THETAS[-1] * 1.3)
    axis.set_xlabel("θ (per minute)", color=MUTED, fontsize=9)
    style(axis, f"Route shares p_i(θ) at OSM node {node} (independent walkers)", "share")
    axis.legend(curves, frameon=False, fontsize=8, loc="center right")
    figure.tight_layout()
    figure.savefig(IMAGES / "p-theta.png")

    figure, axis = plt.subplots(figsize=(6.4, 3.2))
    labels = list(numbers["compare"])
    values = [numbers["compare"][label]["onTime"] * 100 for label in labels]
    bars = axis.bar(labels, values, color=SERIES[0], width=0.55, edgecolor="white", linewidth=2)
    for bar, value in zip(bars, values):
        axis.annotate(f"{value:.1f} %", (bar.get_x() + bar.get_width() / 2, value), xytext=(0, 3),
                      textcoords="offset points", ha="center", fontsize=8.5, color=INK)
    axis.set_ylim(0, 100)
    axis.tick_params(axis="x", labelsize=8)
    style(axis, "Leave the flood before the deadline, Valencia (everyone guided)", "% of people")
    figure.tight_layout()
    figure.savefig(IMAGES / "on-time.png")


if __name__ == "__main__":
    main()
