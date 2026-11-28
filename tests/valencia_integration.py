"""Check the real Valencia graph and EMS observation through the local demo."""

import json
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import ProxyHandler, Request, build_opener
from zipfile import ZipFile

import valencia_evacuation


BASE = sys.argv[1]
INPUTS_ONLY = "--inputs-only" in sys.argv[2:]
HTTP = build_opener(ProxyHandler({}))
ROOT = Path(__file__).resolve().parents[1]


def call(path, body=None, status=200):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"} if body is not None else {}
    try:
        response = HTTP.open(Request(BASE + path, data, headers), timeout=30)
    except HTTPError as error:
        response = error
    with response:
        result = response.read()
        assert response.status == status, (path, response.status, result[:300])
        return json.loads(result) if "json" in response.headers.get("Content-Type", "") else result


def route():
    query = urlencode({"from": "-0.378,39.477", "to": "-0.364,39.476", "mode": "walking"})
    return call("/api/routing/queries/city-route?" + query)


def review(revision, key, centre, span, use_cli=False):
    lon, lat = centre
    ring = [[lon-span, lat-span], [lon+span, lat-span], [lon+span, lat+span],
            [lon-span, lat+span], [lon-span, lat-span]]
    payload = {
        "commandId": key + "-import", "expectedRevision": revision, "layerId": key,
        "imageProductId": "integration-fixture", "observedAt": "2024-10-31T18:03:00Z", "mode": "synthetic",
        "geoJson": {"type": "FeatureCollection", "features": [
            {"type": "Feature", "properties": {}, "geometry": {"type": "Polygon", "coordinates": [ring]}}
        ]},
    }
    if use_cli:
        with TemporaryDirectory(prefix="valencia-review-", dir=ROOT / "artifacts") as directory:
            directory = Path(directory)
            (directory / "roads.geojson").write_text(json.dumps(payload["geoJson"]), encoding="utf-8")
            config = {field: payload[field] for field in ("layerId", "imageProductId", "observedAt", "mode")}
            manifest = directory / "manifest.json"
            manifest.write_text(json.dumps({"flypix": {**config, "geoJson": "roads.geojson"}}), encoding="utf-8")
            subprocess.run([sys.executable, str(ROOT / "experiments/network/import_valencia_review.py"),
                            str(manifest), "--demo", BASE], check=True)
        snapshot = call("/api/situation/queries/snapshot")
    else:
        snapshot = call("/api/situation/commands/import-flypix", payload)
    feature = next(item for item in snapshot["reviewFeatures"] if item["layerId"] == key)
    assert feature["status"] == "Pending" and feature["segmentIds"]
    return snapshot, feature


def review_decision(snapshot, feature, decision):
    return {"commandId": feature["id"] + "-" + decision, "expectedRevision": snapshot["revision"],
            "reviewFeatureId": feature["id"], "decision": decision}


network = call("/api/routing/queries/network")
assert network["scenario"] == "valencia" and not network["nodes"]
if INPUTS_ONLY:
    call("/api/routing/queries/evacuation-plan", status=404)
city = network["city"]
assert city["bounds"] == [-.62, 39.24, -.25, 39.61] and city["roadCount"] > 100_000
assert city["zoneCounts"]["safe"] > 0 and city["zoneCounts"]["hazard"] > 0
assert call("/maps/city.svg").startswith(b"<svg")
assert call("/maps/city-nodes.svg").startswith(b"<svg")
images = call("/maps/city-imagery.json")["scenes"]
assert images["before"]["bounds"] == images["event"]["bounds"]
with ZipFile(ROOT / "data/demo/routing/valencia-task03.zip") as archive:
    handoff = json.loads(archive.read("valencia-graph.json"))
assert handoff["inputVersion"] == "cassini.evacuation-input.v1"
assert handoff["bounds"] == images["event"]["bounds"]
assert handoff["nodes"] and handoff["roads"] and handoff["hazardObservation"]["productId"] == "EMSR773_AOI01_DEL_PRODUCT_v1"
assert call("/maps/sentinel-before.jpg").startswith(b"\xff\xd8")
assert call("/maps/sentinel-event.jpg").startswith(b"\xff\xd8")
page = call("/")
assert b'id="valencia-tools"' in page
assert b'data-integration="integration-sentinel-1"' in page
assert b'data-integration="integration-copernicus-dem"' in page

initial = call("/api/situation/queries/snapshot")
assert initial["revision"] == 0 and not initial["closedSegments"]
assert initial["hazards"][0]["productId"] == "EMSR773_AOI01_DEL_PRODUCT_v1"
assert initial["hazards"][0]["source"] == "copernicus-ems-archive"
ems_preview = call("/api/situation/queries/integration-copernicus")
assert ems_preview["mode"] == "mock" and ems_preview["activationCode"] == "EMSR773" and ems_preview["aoiNumber"] == 1
mask = call("/api/situation/queries/integration-sentinel-1")
dem = call("/api/situation/queries/integration-copernicus-dem")
flypix = call("/api/situation/queries/integration-flypix")
assert mask["mode"] == dem["mode"] == flypix["mode"] == "mock"
assert mask["source"] == "copernicus-sentinel-1" and mask["populationEstimate"]["source"] == "synthetic GHSL example"
assert dem["geoJson"]["features"][0]["properties"]["kind"] == "potential_low_elevation"
west, south, east, north = images["event"]["bounds"]
for preview in (ems_preview, mask, dem, flypix):
    geometry = preview["geoJson"]["features"][0]["geometry"]
    assert geometry["type"] == "Polygon"
    ring = geometry["coordinates"][0]
    assert ring[0] == ring[-1] and all(west <= lon <= east and south <= lat <= north for lon, lat in ring)
city_preview = call("/api/situation/queries/city-map")
assert city_preview["datasetId"] == "mock-valencia-city-v1"
assert call("/api/situation/queries/snapshot") == initial
baseline = route()["route"]
assert baseline and baseline["segments"]
validate_path = "/api/routing/queries/validate-route?"
validation = call(validate_path + urlencode({"segments": ",".join(baseline["segments"])}))
assert validation["ok"] is True and validation["reason"] is None
edge = call("/api/routing/queries/city-edge/" + baseline["segments"][0])
assert edge["lengthMetres"] > 0 and edge["pedestriansPerHour"] > 0
assert edge["class"] and isinstance(edge["intersectsObservedFlood"], bool)
node = call("/api/routing/queries/city-node/" + str(edge["from"]))
assert node["id"] == edge["from"] and node["zone"] == "safe"

centre = baseline["geometry"][len(baseline["geometry"]) // 2]
decision_path = "/api/situation/commands/review-decision"
subprocess.run([sys.executable, str(ROOT / "experiments/network/import_valencia_review.py"),
                "--mock", "--demo", BASE], check=True)
pending = call("/api/situation/queries/snapshot")
rejected_feature, = pending["reviewFeatures"]
assert rejected_feature["status"] == "Pending" and rejected_feature["segmentIds"] and not pending["closedSegments"]
assert rejected_feature["mode"] == "synthetic" and rejected_feature["imageProductId"] == flypix["imageProductId"]
assert rejected_feature["confidence"] == flypix["geoJson"]["features"][0]["properties"]["confidence"]
rejected = call(decision_path, review_decision(pending, rejected_feature, "reject"))
assert rejected["revision"] == pending["revision"] + 1 and not rejected["closedSegments"]
assert next(item for item in rejected["reviewFeatures"] if item["id"] == rejected_feature["id"])["status"] == "Rejected"
assert route()["route"] == baseline

pending, feature = review(rejected["revision"], "valencia-narrow-fixture", centre, .00010, use_cli=True)
overlap = set(feature["segmentIds"]) & set(baseline["segments"])
assert overlap and not pending["closedSegments"] and route()["route"] == baseline
approval = review_decision(pending, feature, "approve")
confirmed = call(decision_path, approval)
assert confirmed["revision"] == pending["revision"] + 1
assert overlap <= set(confirmed["closedSegments"])
for segment in overlap:
    evidence = confirmed["closureEvidence"][segment]
    assert evidence["reviewFeatureId"] == feature["id"] and evidence["source"] == "exercise-verified"
assert call(decision_path, approval) == confirmed
assert call(decision_path, {**approval, "decision": "reject"}, status=409)["code"] == "CommandConflict"
assert call("/api/situation/queries/snapshot") == confirmed
assert {road["id"] for road in call("/api/situation/queries/closed-roads")["roads"]} == set(confirmed["closedSegments"])
rerouted = route()["route"]
assert rerouted and not set(rerouted["segments"]) & set(confirmed["closedSegments"])
segment = min(overlap)
check = call(validate_path + urlencode({"segments": segment, "at": validation["at"]}))
assert check["ok"] is False and check["reason"] == "ClosedSegment"
assert check["revision"] == confirmed["revision"] and check["at"] == validation["at"]

reopened = call("/api/situation/commands/closure", {
    "commandId": "reopen-fixture-segment", "expectedRevision": confirmed["revision"],
    "segmentId": segment, "closed": False,
})
assert segment not in reopened["closureEvidence"]
assert call(validate_path + urlencode({"segments": segment}))["ok"] is True

isolated, barrier = review(reopened["revision"], "valencia-isolation-fixture", [-.378, 39.477], .006)
call(decision_path, review_decision(isolated, barrier, "approve"))
assert route()["route"] is None
summary = f"{city['roadCount']:,} edges, classifications, evidence review, detour and no route"
if not INPUTS_ONLY:
    summary += "; " + valencia_evacuation.run(BASE)
print(f"Valencia integration passed: {summary}.")
