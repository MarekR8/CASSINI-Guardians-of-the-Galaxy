"""Write the task 03 fixture cases and expected.json (W4) from the Python reference model.

Each case is self-contained: graph (cassini.evacuation-input.v1), people and deadlines per node, validated
shelters, the full parameters and extra closures. expected.json keeps full precision so the C# port can be
checked with a tolerance of 1e-6.

    python experiments/routing/fixtures.py            # rewrite cases and expected.json
    python experiments/routing/fixtures.py --check    # fail if the model no longer reproduces expected.json
"""

import argparse
import copy
import json
import math
from pathlib import Path

from graph import ROOT, EvacuationGraph
from model import Model

CASES = ROOT / "data/demo/routing/cases"
EXPECTED = ROOT / "data/demo/routing/expected.json"
PARAMS = ROOT / "data/demo/routing/params.json"
TOLERANCE = 1e-6


def road(identifier, start, end, seconds, zone="hazard", cls="residential", capacity=900):
    return {"id": identifier, "from": start, "to": end, "class": cls, "lengthMetres": round(seconds * 1.3, 1),
            "geometry": [[0.0, 0.0], [0.0, 0.0]], "walk": [seconds, seconds], "drive": [None, None],
            "pedestriansPerHour": capacity, "vehiclesPerHour": None, "zone": zone, "closed": False,
            "intersectsObservedFlood": zone == "hazard"}


def graph(nodes, roads):
    return {
        "inputVersion": "cassini.evacuation-input.v1", "scenario": "synthetic-routing-fixture",
        "bounds": [0, 0, 0.01, 0.01], "osmTimestamp": "2024-10-19T00:00:00Z", "situationRevision": 0,
        "hazardObservation": {"productId": "exercise-flood", "sourceUrl": None, "source": "synthetic",
                              "acquisitionsUtc": ["2024-10-31T00:00:00Z"],
                              "coverage": {"type": "Polygon", "coordinates": [[[0, 0], [0.01, 0], [0.01, 0.01],
                                                                              [0, 0.01], [0, 0]]]},
                              "geoJson": {"type": "FeatureCollection", "features": []}},
        "closureEvidence": [], "boundaryNodeIds": [],
        "nodes": [{"id": node, "longitude": round(0.001 * (number + 1), 6), "latitude": 0.005, "zone": zone}
                  for number, (node, zone) in enumerate(nodes)],
        "roads": roads,
    }


def shelter(identifier, node, capacity, step_free=True, intake=None):
    return {"id": identifier, "name": identifier, "nodeId": node, "capacity": capacity,
            "intakePerHour": intake, "stepFree": step_free}


# Two flooded nodes (1, 2) and two dry shelters (10, 20). Path costs differ, so shortest paths are unique.
TWO_SHELTERS = graph(
    [(1, "hazard"), (2, "hazard"), (10, "safe"), (20, "safe")],
    [road("bridge", 1, 10, 300), road("street", 1, 2, 120), road("east", 2, 20, 260), road("lane", 2, 10, 500)],
)


def cases(params):
    def case(name, description, network, people, deadlines, shelters, closed=(), **model):
        settings = copy.deepcopy(params)
        settings["model"].update(model)
        return name, {
            "caseVersion": "cassini.routing-case.v1", "case": name, "description": description,
            "graph": network, "people": [{"nodeId": node, "people": count} for node, count in people.items()],
            "deadlines": [{"nodeId": node, "minutes": minutes} for node, minutes in deadlines.items()],
            "shelters": shelters, "params": settings, "closed": list(closed),
        }

    both = [shelter("s10", 10, 2000), shelter("s20", 20, 2000)]
    yield case("baseline", "Two flooded nodes split between two shelters before any closure.",
               TWO_SHELTERS, {1: 600, 2: 400}, {1: 30, 2: 30}, both)
    yield case("bridge-closed", "The bridge from node 1 to shelter s10 is closed; s10 is still reachable via node 2.",
               TWO_SHELTERS, {1: 600, 2: 400}, {1: 30, 2: 30}, both, closed=["bridge"])
    yield case("no-exit", "Every road to dry ground is closed: NoEligibleExit, never a distribution.",
               TWO_SHELTERS, {1: 600, 2: 400}, {1: 30, 2: 30}, both, closed=["bridge", "east", "lane"])
    yield case("flooded-before-arrival", "Deadlines of 4 and 5 minutes: slower profiles leave the flood too late.",
               TWO_SHELTERS, {1: 600, 2: 400}, {1: 4, 2: 5}, both)
    congested = copy.deepcopy(TWO_SHELTERS)
    congested["roads"][0]["pedestriansPerHour"] = 50
    yield case("congestion", "20,000 people, a narrow bridge and a small shelter: flow and places shift to s20.",
               congested, {1: 14000, 2: 6000}, {1: 60, 2: 60},
               [shelter("s10", 10, 3000, intake=6000), shelter("s20", 20, 30000)])
    yield case("wheelchair-steps", "Steps lead to the nearest step-free shelter; wheelchair users take the ramp and walk on.",
               graph([(1, "hazard"), (10, "safe"), (20, "safe")],
                     [road("ramp", 1, 10, 300, cls="footway"), road("stairs", 1, 20, 150, cls="steps"),
                      road("promenade", 10, 20, 200, zone="safe", cls="footway")]),
               {1: 1000}, {1: 30}, [shelter("s10", 10, 2000, step_free=False), shelter("s20", 20, 2000)])
    yield case("mixed-compliance", "Half the people are reached. The uninformed half takes the narrow bridge to the "
               "nearest dry node; the guided half is assigned on what is left and avoids it.",
               congested, {1: 14000, 2: 6000}, {1: 60, 2: 60},
               [shelter("s10", 10, 3000, intake=6000), shelter("s20", 20, 30000)], complianceShare=0.5)
    yield case("no-guidance", "Nobody is reached: everyone walks the free-flow shortest path to the nearest dry node.",
               congested, {1: 14000, 2: 6000}, {1: 60, 2: 60},
               [shelter("s10", 10, 3000, intake=6000), shelter("s20", 20, 30000)], complianceShare=0.0)
    wet_or_dry = graph([(1, "hazard"), (2, "hazard"), (3, "safe"), (10, "safe")],
                       [road("wet-1", 1, 2, 100), road("wet-2", 2, 10, 100), road("exit", 1, 3, 60),
                        road("dry", 3, 10, 250, zone="safe")])
    yield case("hazard-weight", "The quickest way to the shelter stays 200 s in the water; with lambda 5 the route "
               "leaves the water after 60 s and walks 250 s on dry ground.",
               wet_or_dry, {1: 500}, {1: 30}, [shelter("s10", 10, 2000)])
    yield case("hazard-weight-off", "The same network with lambda 1: plain travel time keeps people in the water.",
               wet_or_dry, {1: 500}, {1: 30}, [shelter("s10", 10, 2000)], hazardTimeWeight=1.0)
    yield case("no-reentry", "A short path re-enters the flood from dry ground; the dry detour must be used.",
               graph([(1, "hazard"), (2, "safe"), (3, "safe"), (10, "safe")],
                     [road("a", 1, 2, 50), road("wet", 2, 10, 60), road("b", 1, 3, 200),
                      road("dry", 3, 10, 210, zone="safe")]),
               {1: 500}, {1: 30}, [shelter("s10", 10, 2000)])


def run_case(document):
    network = EvacuationGraph(document["graph"])
    people = {network.index[item["nodeId"]]: item["people"] for item in document["people"]}
    deadlines = {network.index[item["nodeId"]]: (item["minutes"], "fixture") for item in document["deadlines"]}
    model = Model(network, people, deadlines, document["shelters"], document["params"], closed=document["closed"])
    result = model.run(exact=True)
    return {key: result[key] for key in ("status", "statusAll", "uninformed", "shelters", "nodes", "waves")}


def compare(expected, actual, path=""):
    """Yield the paths where two JSON values differ beyond the tolerance."""
    if isinstance(expected, dict) and isinstance(actual, dict):
        for key in sorted(set(expected) | set(actual), key=str):
            if key == "gap":
                continue
            yield from compare(expected.get(key), actual.get(key), f"{path}.{key}")
    elif isinstance(expected, list) and isinstance(actual, list) and len(expected) == len(actual):
        for number, (left, right) in enumerate(zip(expected, actual)):
            yield from compare(left, right, f"{path}[{number}]")
    elif isinstance(expected, float) or isinstance(actual, float):
        if not (isinstance(expected, (int, float)) and isinstance(actual, (int, float))
                and math.isclose(expected, actual, rel_tol=TOLERANCE, abs_tol=TOLERANCE)):
            yield f"{path}: {expected} != {actual}"
    elif expected != actual:
        yield f"{path}: {expected} != {actual}"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    params = json.loads(PARAMS.read_text(encoding="utf-8"))
    expected = {"expectedVersion": "cassini.routing-expected.v1", "model": "logit-sue-msa",
                "tolerance": TOLERANCE, "cases": {}}
    for name, document in cases(params):
        expected["cases"][name] = run_case(document)
        if not args.check:
            CASES.mkdir(parents=True, exist_ok=True)
            (CASES / f"{name}.json").write_text(json.dumps(document, indent=1) + "\n", encoding="utf-8")
    # JSON keys are strings, so compare through a serialisation round trip.
    expected = json.loads(json.dumps(expected))
    if args.check:
        stored = json.loads(EXPECTED.read_text(encoding="utf-8"))
        differences = list(compare(stored["cases"], expected["cases"]))
        if differences:
            raise SystemExit("expected.json differs:\n" + "\n".join(differences[:20]))
        print(f"{len(expected['cases'])} cases match expected.json")
        return
    EXPECTED.write_text(json.dumps(expected, indent=1) + "\n", encoding="utf-8")
    for name, result in expected["cases"].items():
        print(f"{name:>24}: " + ", ".join(f"{key} {value:,.1f}" for key, value in result["status"].items())
              + " | " + ", ".join(f"{item['id']} {item['expectedLoad']:,.1f}" for item in result["shelters"]))


if __name__ == "__main__":
    main()
