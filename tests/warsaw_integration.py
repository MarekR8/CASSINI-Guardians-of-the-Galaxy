"""Verify the Warsaw map, evidence import and reviewed bridge closure through the gateway."""

import json
from pathlib import Path
import subprocess
import sys
import tempfile
from urllib.error import HTTPError
from urllib.request import ProxyHandler, Request, build_opener


BASE = sys.argv[1]
ROOT = Path(__file__).resolve().parents[1]
HTTP = build_opener(ProxyHandler({}))


def request(path, body=None, status=200):
    headers = {"Content-Type": "application/json"} if body is not None else {}
    data = json.dumps(body).encode() if body is not None else None
    try:
        response = HTTP.open(Request(BASE + path, data, headers), timeout=8)
    except HTTPError as error:
        response = error
    with response:
        content = response.read()
        assert response.status == status, (path, response.status, content)
        return json.loads(content) if "json" in response.headers["Content-Type"] else content


def snapshot():
    return request("/api/situation/queries/snapshot")


def distribution():
    return request("/api/routing/queries/distribution?node=walk-start")


def command(name, payload, status=200):
    return request("/api/situation/commands/" + name, payload, status)


assert b'id="review-panel"' in request("/")
network = request("/api/routing/queries/network")
assert network["scenario"] == "warsaw" and "OpenStreetMap" in network["attribution"]
assert len(network["contextRoads"]) > 50 and network["waterways"]
bridge = next(segment for segment in network["segments"] if segment["id"] == "bridge-2")
assert len(bridge["geometry"]) > 5 and bridge["drivingGeometry"] != bridge["reverseDrivingGeometry"]

initial = snapshot()
feature = initial["reviewFeatures"][0]
assert feature["mode"] == "synthetic" and feature["status"] == "Pending"
assert feature["segmentIds"] == ["bridge-2"], feature["segmentIds"]
assert initial["closedSegments"] == []
baseline = distribution()["options"]
assert {option["exit"] for option in baseline} == {"exit-east", "exit-north"}
assert abs(sum(option["weight"] for option in baseline) - 1) < 1e-9
assert request("/api/routing/queries/validate-route?segments=bridge-2")["ok"]

draw = request("/api/routing/commands/draw",
               {"commandId": "warsaw-draw", "node": "walk-start", "destination": "exit-east",
                "revision": 0, "seed": 1})
assert "bridge-2" in draw["route"]["segments"]

with tempfile.TemporaryDirectory() as directory:
    package = Path(directory)
    polygon = {"type": "FeatureCollection", "features": [
        {"type": "Feature", "properties": {"confidence": 0.84}, "geometry": feature["geometry"]}
    ]}
    (package / "evidence.geojson").write_text(json.dumps(polygon))
    manifest = package / "manifest.json"
    manifest.write_text(json.dumps({
        "hazards": [{"source": "sentinel-1", "sourceUrl": "https://browser.dataspace.copernicus.eu/",
                     "productId": "test-sentinel-1", "observedAt": "2026-11-27T18:00:00Z",
                     "geoJson": "evidence.geojson"}],
        "flypix": {"mode": "synthetic", "imageProductId": "test-raster", "layerId": "test-export",
                   "observedAt": "2026-11-27T18:00:00Z", "geoJson": "evidence.geojson"},
    }))
    run_import = [sys.executable, str(ROOT / "experiments/network/import_warsaw_evidence.py"),
                  str(manifest), "--demo", BASE]
    subprocess.run(run_import, check=True)
    first_import = snapshot()
    subprocess.run(run_import, check=True)
    assert snapshot() == first_import

recorded = next(item for item in first_import["reviewFeatures"] if item["layerId"] == "test-export")
assert recorded["segmentIds"] == ["bridge-2"] and recorded["confidence"] == 0.84
assert first_import["hazards"][-1]["source"] == "copernicus-sentinel-1-operator-import"
assert distribution()["options"] == baseline
assert request("/api/routing/queries/draws/warsaw-draw")["state"] == "Active"

rejected = command("review-decision", {
    "commandId": "reject", "expectedRevision": first_import["revision"],
    "reviewFeatureId": recorded["id"], "decision": "reject",
})
assert rejected["closedSegments"] == [] and distribution()["options"] == baseline

decision = {"commandId": "approve", "expectedRevision": rejected["revision"],
            "reviewFeatureId": feature["id"], "decision": "approve"}
approved = command("review-decision", decision)
assert approved["revision"] == rejected["revision"] + 1
assert approved["closedSegments"] == ["bridge-2"]
assert approved["closureEvidence"]["bridge-2"]["reviewFeatureId"] == feature["id"]
assert approved["closureEvidence"]["bridge-2"]["source"] == "exercise-verified"
assert command("review-decision", decision) == approved
assert command("review-decision", {**decision, "decision": "reject"}, 409)["code"] == "CommandConflict"
assert command("review-decision", {**decision, "commandId": "another", "expectedRevision": rejected["revision"]},
               409)["code"] == "RevisionConflict"
assert request("/api/routing/queries/draws/warsaw-draw")["state"] == "Withdrawn"
assert [item["exit"] for item in distribution()["options"]] == ["exit-north"]
assert request("/api/routing/queries/validate-route?segments=bridge-2")["reason"] == "ClosedSegment"
assert request("/api/routing/queries/assistance-route?from=exit-east&pickup=pickup")["exit"] == "exit-north"

no_exit = command("closure", {"commandId": "block-north", "expectedRevision": approved["revision"],
                              "segmentId": "north-link", "closed": True})
assert "north-link" in no_exit["closedSegments"] and distribution()["options"] == []

print("Warsaw integration passed: OSM, local Copernicus/FlyPix import, review and retry, route withdrawal, detour, no exit.")
