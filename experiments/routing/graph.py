"""Load a cassini.evacuation-input.v1 document and expose the pedestrian network."""

import json
import math
from pathlib import Path
from zipfile import ZipFile

import numpy as np
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components
from scipy.spatial import cKDTree

ROOT = Path(__file__).resolve().parents[2]
INPUT_VERSION = "cassini.evacuation-input.v1"
DEFAULT_GRAPH = ROOT / "data/demo/routing/valencia-task03.zip"
EARTH_METRES = 111_320


def read_document(path):
    """Read the graph from a JSON file or from valencia-graph.json inside a task 03 ZIP package."""
    path = Path(path)
    if path.suffix.lower() == ".zip":
        with ZipFile(path) as archive:
            names = [name for name in archive.namelist() if name.endswith("-graph.json")]
            if len(names) != 1:
                raise ValueError(f"{path.name}: expected exactly one *-graph.json entry.")
            document = json.loads(archive.read(names[0]))
    else:
        document = json.loads(path.read_text(encoding="utf-8-sig"))
    if document.get("inputVersion") != INPUT_VERSION:
        raise ValueError(f"{path.name}: inputVersion must be {INPUT_VERSION}.")
    return document


class EvacuationGraph:
    """Nodes, roads and the walkable network of one evacuation input document."""

    def __init__(self, document):
        self.document = document
        self.bounds = document["bounds"]
        self.revision = document["situationRevision"]
        self.nodes = document["nodes"]
        self.roads = document["roads"]
        self.index = {node["id"]: position for position, node in enumerate(self.nodes)}
        self.zone = np.array([node["zone"] for node in self.nodes])
        self.longitude = np.array([node["longitude"] for node in self.nodes], dtype=float)
        self.latitude = np.array([node["latitude"] for node in self.nodes], dtype=float)
        self.scale = EARTH_METRES * math.cos(math.radians((self.bounds[1] + self.bounds[3]) / 2))
        self.walkable = np.zeros(len(self.nodes), dtype=bool)
        rows, cols = [], []
        for road in self.roads:
            if road["closed"] or all(cost is None for cost in road["walk"]):
                continue
            a, b = self.index[road["from"]], self.index[road["to"]]
            self.walkable[[a, b]] = True
            rows.append(a)
            cols.append(b)
        size = len(self.nodes)
        matrix = coo_matrix((np.ones(len(rows)), (rows, cols)), shape=(size, size))
        _, labels = connected_components(matrix, directed=False)
        counts = np.bincount(labels[self.walkable], minlength=labels.max() + 1)
        self.main_component = self.walkable & (labels == counts.argmax())
        self._trees = {}

    @classmethod
    def load(cls, path=DEFAULT_GRAPH):
        return cls(read_document(path))

    def project(self, longitude, latitude):
        """Local metric coordinates; adequate for snapping within a city-sized window."""
        return np.column_stack([np.asarray(longitude) * self.scale, np.asarray(latitude) * EARTH_METRES])

    def in_bounds(self, longitude, latitude):
        west, south, east, north = self.bounds
        return west <= longitude <= east and south <= latitude <= north

    def nearest(self, longitude, latitude, mask):
        """Return (node positions, distances in metres) of the nearest node selected by mask."""
        key = mask.tobytes()
        if key not in self._trees:
            candidates = np.flatnonzero(mask)
            if candidates.size == 0:
                raise ValueError("No candidate nodes to snap to.")
            self._trees[key] = (candidates, cKDTree(self.project(self.longitude[candidates], self.latitude[candidates])))
        candidates, tree = self._trees[key]
        distances, positions = tree.query(self.project(longitude, latitude))
        return candidates[positions], distances
