"""Unit tests for the task 03 input loaders: python -m unittest discover -s experiments/routing"""

import copy
import unittest

import deadlines
from graph import ROOT, EvacuationGraph
from prepare_inputs import depth_range
import shelters

EXAMPLE = ROOT / "data/demo/routing/evacuation-input.v1.example.json"


def document(*items):
    return {"sheltersVersion": "cassini.shelters.v1", "scenario": "synthetic-routing-fixture",
            "provenance": {"source": "unit test", "mode": "synthetic"}, "shelters": list(items)}


def shelter(identifier="s1", **fields):
    return {"id": identifier, "name": "Test", "capacity": 100, **fields}


class ShelterValidation(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.graph = EvacuationGraph.load(EXAMPLE)

    def reasons(self, *items):
        accepted, rejected = shelters.validate(document(*items), self.graph)
        return [item["id"] for item in accepted], {item["id"]: item["reason"] for item in rejected}

    def test_accepts_dry_reachable_node_by_id(self):
        accepted, rejected = shelters.validate(document(shelter(nodeId=103)), self.graph)
        self.assertEqual(rejected, [])
        self.assertEqual(accepted[0]["nodeId"], 103)
        self.assertFalse(accepted[0]["stepFree"])

    def test_snaps_position_to_nearest_walkable_node(self):
        accepted, _ = shelters.validate(document(shelter(longitude=0.0041, latitude=0.0069)), self.graph)
        self.assertEqual(accepted[0]["nodeId"], 103)
        self.assertLess(accepted[0]["snappedMetres"], 20)

    def test_rejections_carry_reason_codes(self):
        _, reasons = self.reasons(
            shelter("hazard", nodeId=101),
            shelter("closed-only", nodeId=102),
            shelter("outside", longitude=0.5, latitude=0.5),
            shelter("unknown", nodeId=999),
            shelter("far", nodeId=103, longitude=0.009, latitude=0.001),
            shelter("empty", nodeId=103, capacity=0),
            shelter("nowhere"),
            shelter("intake", nodeId=103, intakePerHour=0),
        )
        self.assertEqual(reasons, {
            "hazard": "InHazardZone", "closed-only": "NoWalkableRoad", "outside": "OutsideMap",
            "unknown": "UnknownNode", "far": "PositionMismatch", "empty": "InvalidCapacity",
            "nowhere": "MissingLocation", "intake": "InvalidIntake",
        })

    def test_duplicate_ids_and_shared_nodes(self):
        accepted, reasons = self.reasons(shelter("a", nodeId=103), shelter("a", nodeId=103),
                                         shelter("b", longitude=0.004, latitude=0.007))
        self.assertEqual(accepted, ["a"])
        self.assertEqual(reasons, {"a": "DuplicateId", "b": "SharedNode"})

    def test_closure_cuts_off_a_shelter(self):
        graph = copy.deepcopy(self.graph.document)
        for road in graph["roads"]:
            road["closed"] = True
        _, rejected = shelters.validate(document(shelter(nodeId=103)), EvacuationGraph(graph))
        self.assertEqual(rejected[0]["reason"], "NoWalkableRoad")

    def test_rejects_file_without_provenance(self):
        broken = document(shelter(nodeId=103))
        broken["provenance"] = {"source": "", "mode": "client"}
        with self.assertRaises(ValueError):
            shelters.validate(broken, self.graph)


PARAMS = {"deadline": {
    "depthMinutes": [{"minMetres": 2.0, "minutes": 15}, {"minMetres": 1.0, "minutes": 30},
                     {"minMetres": 0.5, "minutes": 45}, {"minMetres": 0.0, "minutes": 60}],
    "depthToleranceMetres": 25,
    "distance": {"minutesAtSource": 15, "minutesPerKm": 10, "maxMinutes": 90},
}}


def depth_layer(min_metres, label):
    square = [[0.0, 0.003], [0.002, 0.003], [0.002, 0.005], [0.0, 0.005], [0.0, 0.003]]
    return {"features": [{"properties": {"depthClass": label, "minMetres": min_metres},
                          "geometry": {"type": "Polygon", "coordinates": [square]}}]}


def source_at(longitude):
    return {"features": [{"geometry": {"type": "LineString",
                                       "coordinates": [[longitude, 0.0], [longitude, 0.01]]}}]}


class Deadlines(unittest.TestCase):
    """Node 101 is the only hazard node of the example, at longitude 0.001."""

    @classmethod
    def setUpClass(cls):
        cls.graph = EvacuationGraph.load(EXAMPLE)
        cls.node = cls.graph.index[101]

    def test_only_hazard_nodes_get_a_deadline(self):
        result = deadlines.compute(self.graph, {"features": []}, source_at(0.009), PARAMS)
        self.assertEqual(list(result), [self.node])

    def test_depth_rule_wins_far_from_the_source(self):
        minutes, basis = deadlines.compute(self.graph, depth_layer(1.0, "1.00 - 2.00"), source_at(0.1), PARAMS)[self.node]
        self.assertEqual((minutes, basis), (30.0, "depth 1.00 - 2.00"))

    def test_distance_rule_wins_near_the_source(self):
        minutes, basis = deadlines.compute(self.graph, depth_layer(0.0, "Below 0.50"), source_at(0.002), PARAMS)[self.node]
        self.assertTrue(basis.startswith("distance"))
        self.assertAlmostEqual(minutes, 15 + 10 * 0.111, delta=0.2)  # 0.001 degree is about 111 m

    def test_distance_rule_is_capped(self):
        minutes, _ = deadlines.compute(self.graph, {"features": []}, source_at(0.5), PARAMS)[self.node]
        self.assertEqual(minutes, 90)


class DepthClasses(unittest.TestCase):
    def test_ems_labels(self):
        self.assertEqual(depth_range("Below 0.50"), (0.0, 0.5))
        self.assertEqual(depth_range("1.00 - 2.00"), (1.0, 2.0))
        self.assertEqual(depth_range("Above 6.00"), (6.0, None))
        with self.assertRaises(ValueError):
            depth_range("deep")


if __name__ == "__main__":
    unittest.main()
