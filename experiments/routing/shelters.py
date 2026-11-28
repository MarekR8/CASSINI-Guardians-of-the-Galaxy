"""Load and validate a cassini.shelters.v1 file against an evacuation input graph."""

import argparse
import json
from pathlib import Path

from graph import DEFAULT_GRAPH, EvacuationGraph

SHELTERS_VERSION = "cassini.shelters.v1"
SNAP_METRES = 250


def _positive_int(value):
    return isinstance(value, int) and not isinstance(value, bool) and value >= 1


def _number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def validate(document, graph):
    """Return (accepted, rejected). Each rejection names the shelter and a reason code."""
    if document.get("sheltersVersion") != SHELTERS_VERSION:
        raise ValueError(f"sheltersVersion must be {SHELTERS_VERSION}.")
    provenance = document.get("provenance") or {}
    if provenance.get("mode") not in ("client", "synthetic") or not provenance.get("source"):
        raise ValueError("provenance needs a source and mode client or synthetic.")
    accepted, rejected, seen, used_nodes = [], [], set(), {}

    def reject(shelter, code, detail):
        rejected.append({"id": shelter.get("id"), "reason": code, "detail": detail})

    for shelter in document.get("shelters") or []:
        identifier = shelter.get("id")
        if not isinstance(identifier, str) or not identifier or not isinstance(shelter.get("name"), str) \
                or not shelter["name"]:
            reject(shelter, "InvalidShelter", "id and name are required.")
            continue
        if identifier in seen:
            reject(shelter, "DuplicateId", f"{identifier} appears more than once.")
            continue
        seen.add(identifier)
        if not _positive_int(shelter.get("capacity")):
            reject(shelter, "InvalidCapacity", "capacity must be a positive integer.")
            continue
        intake = shelter.get("intakePerHour")
        if intake is not None and not _positive_int(intake):
            reject(shelter, "InvalidIntake", "intakePerHour must be a positive integer or null.")
            continue
        step_free = shelter.get("stepFree", False)
        if not isinstance(step_free, bool):
            reject(shelter, "InvalidStepFree", "stepFree must be true or false.")
            continue

        node_id, longitude, latitude = shelter.get("nodeId"), shelter.get("longitude"), shelter.get("latitude")
        has_position = longitude is not None or latitude is not None
        if has_position and not (_number(longitude) and _number(latitude)):
            reject(shelter, "InvalidPosition", "longitude and latitude must both be numbers.")
            continue
        if node_id is not None:
            if node_id not in graph.index:
                reject(shelter, "UnknownNode", f"nodeId {node_id} is not in the graph.")
                continue
            position = graph.index[node_id]
            snapped = 0.0
            if has_position:
                if not graph.in_bounds(longitude, latitude):
                    reject(shelter, "OutsideMap", "The position is outside the graph bounds.")
                    continue
                offset = graph.project([longitude], [latitude]) - graph.project(
                    [graph.longitude[position]], [graph.latitude[position]])
                snapped = float((offset ** 2).sum() ** 0.5)
                if snapped > SNAP_METRES:
                    reject(shelter, "PositionMismatch",
                           f"nodeId is {snapped:.0f} m from the given position (limit {SNAP_METRES} m).")
                    continue
            if not graph.walkable[position]:
                reject(shelter, "NoWalkableRoad", "The node has no open pedestrian road.")
                continue
        elif has_position:
            if not graph.in_bounds(longitude, latitude):
                reject(shelter, "OutsideMap", "The position is outside the graph bounds.")
                continue
            positions, distances = graph.nearest([longitude], [latitude], graph.walkable)
            position, snapped = int(positions[0]), float(distances[0])
            if snapped > SNAP_METRES:
                reject(shelter, "NoRoadNearby", f"Nearest pedestrian road node is {snapped:.0f} m away.")
                continue
        else:
            reject(shelter, "MissingLocation", "Give longitude and latitude, a nodeId, or both.")
            continue

        zone = graph.zone[position]
        if zone != "safe":
            reject(shelter, "InHazardZone" if zone == "hazard" else "UnassessedZone",
                   f"The shelter node is classified as {zone}.")
            continue
        if not graph.main_component[position]:
            reject(shelter, "Unreachable", "The shelter is cut off from the main pedestrian network.")
            continue
        node = graph.nodes[position]["id"]
        if node in used_nodes:
            reject(shelter, "SharedNode", f"Snaps to the same node as {used_nodes[node]}.")
            continue
        used_nodes[node] = identifier
        accepted.append({
            "id": identifier, "name": shelter["name"], "nodeId": node,
            "longitude": float(graph.longitude[position]), "latitude": float(graph.latitude[position]),
            "snappedMetres": round(snapped, 1), "capacity": shelter["capacity"],
            "intakePerHour": intake, "stepFree": step_free,
        })
    if not document.get("shelters"):
        raise ValueError("The file lists no shelters.")
    return accepted, rejected


def load(path, graph):
    document = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    accepted, rejected = validate(document, graph)
    return {"provenance": document["provenance"], "scenario": document.get("scenario"),
            "shelters": accepted, "rejected": rejected}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("shelters", type=Path)
    parser.add_argument("--graph", type=Path, default=DEFAULT_GRAPH)
    args = parser.parse_args()
    try:
        result = load(args.shelters, EvacuationGraph.load(args.graph))
    except (OSError, KeyError, ValueError) as error:
        parser.exit(1, f"{error}\n")
    mode = result["provenance"]["mode"]
    print(f"{len(result['shelters'])} shelter(s) accepted, {len(result['rejected'])} rejected ({mode}).")
    for shelter in result["shelters"]:
        print(f"  ok  {shelter['id']}: node {shelter['nodeId']} ({shelter['snappedMetres']} m), "
              f"capacity {shelter['capacity']}, intake {shelter['intakePerHour'] or 'unlimited'}/h, "
              f"step-free {'yes' if shelter['stepFree'] else 'no'}")
    for item in result["rejected"]:
        print(f"  --  {item['id']}: {item['reason']} · {item['detail']}")
    if result["rejected"]:
        raise SystemExit(2)


if __name__ == "__main__":
    main()
