"""Unit tests for the task 03 model on small hand-made graphs: python -m unittest discover -s experiments/routing"""

import copy
import json
import unittest

from graph import ROOT, EvacuationGraph
from model import Model, congestion

PARAMS = json.loads((ROOT / "data/demo/routing/params.json").read_text(encoding="utf-8"))


def road(identifier, start, end, seconds, zone="safe", cls="residential", capacity=900):
    return {"id": identifier, "from": start, "to": end, "class": cls, "lengthMetres": seconds * 1.3,
            "geometry": [[0, 0], [0, 0]], "walk": [seconds, seconds], "drive": [None, None],
            "pedestriansPerHour": capacity, "vehiclesPerHour": None, "zone": zone, "closed": False,
            "intersectsObservedFlood": zone == "hazard"}


def network(nodes, roads):
    return EvacuationGraph({
        "inputVersion": "cassini.evacuation-input.v1", "scenario": "test", "bounds": [0, 0, 0.01, 0.01],
        "osmTimestamp": "2024-10-19T00:00:00Z", "situationRevision": 0, "hazardObservation": {},
        "closureEvidence": [], "boundaryNodeIds": [],
        "nodes": [{"id": node, "longitude": 0.001 * number, "latitude": 0.001, "zone": zone}
                  for number, (node, zone) in enumerate(nodes)],
        "roads": roads,
    })


def shelter(identifier, node, capacity=1000, step_free=True):
    return {"id": identifier, "nodeId": node, "capacity": capacity, "intakePerHour": None, "stepFree": step_free}


def params(**model):
    result = copy.deepcopy(PARAMS)
    result["model"].update(model)
    return result


def run(graph, people, deadline, shelters, settings=None, closed=()):
    positions = {graph.index[node]: count for node, count in people.items()}
    deadlines = {graph.index[node]: (minutes, "test") for node, minutes in deadline.items()}
    return Model(graph, positions, deadlines, shelters, settings or params(), closed=closed).run()


# Hazard node 1 reaches two dry shelters: 10 (100 s away) and 20 (200 s away).
FORK = network([(1, "hazard"), (10, "safe"), (20, "safe")],
               [road("near", 1, 10, 100, "hazard"), road("far", 1, 20, 200, "hazard")])


class ModelRules(unittest.TestCase):
    def test_shares_sum_to_one_and_prefer_the_nearer_shelter(self):
        result = run(FORK, {1: 100}, {1: 60}, [shelter("s10", 10), shelter("s20", 20)])
        for group in result["nodes"][1].values():
            self.assertAlmostEqual(sum(group["shares"].values()), 1, places=3)
            self.assertGreater(group["shares"]["s10"], group["shares"]["s20"])
        self.assertEqual(result["status"], {"Ok": round(100 * 0.95)})

    def test_theta_limits(self):
        sharp = run(FORK, {1: 10}, {1: 60}, [shelter("s10", 10), shelter("s20", 20)], params(thetaPerMinute=50))
        flat = run(FORK, {1: 10}, {1: 60}, [shelter("s10", 10), shelter("s20", 20)], params(thetaPerMinute=1e-6))
        self.assertGreater(sharp["nodes"][1]["independent"]["shares"]["s10"], 0.999)
        self.assertAlmostEqual(flat["nodes"][1]["independent"]["shares"]["s10"], 0.5, places=3)

    def test_closure_removes_a_shelter_and_then_every_exit(self):
        one = run(FORK, {1: 10}, {1: 60}, [shelter("s10", 10), shelter("s20", 20)], closed=["near"])
        self.assertEqual(one["nodes"][1]["independent"]["shares"], {"s20": 1.0})
        none = run(FORK, {1: 10}, {1: 60}, [shelter("s10", 10), shelter("s20", 20)], closed=["near", "far"])
        self.assertEqual(set(none["status"]), {"NoEligibleExit"})

    def test_deadline_marks_late_groups(self):
        # 100 s for independent walkers, 143 s for slow ones (speed 0.7): a 2-minute deadline splits them.
        result = run(FORK, {1: 100}, {1: 2}, [shelter("s10", 10)])
        self.assertEqual(result["nodes"][1]["independent"]["status"], "Ok")
        self.assertEqual(result["nodes"][1]["slow"]["status"], "DeadlineInfeasible")

    def test_wheelchair_needs_step_free_shelter_and_no_steps(self):
        graph = network([(1, "hazard"), (10, "safe"), (20, "safe")],
                        [road("ramp", 1, 10, 100, "hazard"), road("stairs", 1, 20, 50, "hazard", cls="steps")])
        result = run(graph, {1: 100}, {1: 60}, [shelter("no-ramp", 10, step_free=False), shelter("s20", 20)])
        self.assertEqual(result["nodes"][1]["wheelchair"]["status"], "NoEligibleExit")
        self.assertEqual(result["nodes"][1]["independent"]["status"], "Ok")

    def test_no_reentry_into_the_flood(self):
        # 1 -> 2 is short but 2 -> 10 crosses the flood again from dry ground; the dry detour 1 -> 3 -> 10 must win.
        graph = network([(1, "hazard"), (2, "safe"), (3, "safe"), (10, "safe")],
                        [road("a", 1, 2, 50, "hazard"), road("wet", 2, 10, 50, "hazard"),
                         road("b", 1, 3, 200, "hazard"), road("dry", 3, 10, 200)])
        result = run(graph, {1: 10}, {1: 60}, [shelter("s10", 10)])
        self.assertEqual(result["nodes"][1]["independent"]["status"], "Ok")
        self.assertAlmostEqual(result["nodes"][1]["independent"]["leaveFloodMinutes"], 200 / 60, places=1)

    def test_shelter_capacity_shifts_people(self):
        result = run(FORK, {1: 2000}, {1: 60}, [shelter("s10", 10, capacity=400), shelter("s20", 20, capacity=5000)])
        loads = {item["id"]: item["expectedLoad"] for item in result["shelters"]}
        self.assertLess(loads["s10"], 400 * 1.1)
        self.assertGreater(loads["s20"], 1400)

    def test_congestion_is_continuous_and_grows_linearly_above_capacity(self):
        below, at, above = congestion([0.5, 1.0, 2.0], 0.15, 4)
        self.assertAlmostEqual(at, 0.15)
        self.assertAlmostEqual(above - at, 0.15 * 4)
        self.assertLess(below, at)


class Fixtures(unittest.TestCase):
    def test_model_reproduces_expected_json(self):
        import fixtures
        stored = json.loads(fixtures.EXPECTED.read_text(encoding="utf-8"))
        for path in sorted(fixtures.CASES.glob("*.json")):
            document = json.loads(path.read_text(encoding="utf-8"))
            actual = json.loads(json.dumps(fixtures.run_case(document)))
            differences = list(fixtures.compare(stored["cases"][document["case"]], actual))
            self.assertEqual(differences, [], path.name)


if __name__ == "__main__":
    unittest.main()
