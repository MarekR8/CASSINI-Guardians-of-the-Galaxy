"""Check the real Valencia graph and EMS observation through the local demo."""

import json
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import ProxyHandler, Request, build_opener


BASE = sys.argv[1]
HTTP = build_opener(ProxyHandler({}))


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
        root = Path(__file__).resolve().parents[1]
        with TemporaryDirectory(prefix="valencia-review-", dir=root / "artifacts") as directory:
            directory = Path(directory)
            (directory / "roads.geojson").write_text(json.dumps(payload["geoJson"]), encoding="utf-8")
            config = {field: payload[field] for field in ("layerId", "imageProductId", "observedAt", "mode")}
            manifest = directory / "manifest.json"
            manifest.write_text(json.dumps({"flypix": {**config, "geoJson": "roads.geojson"}}), encoding="utf-8")
            subprocess.run([sys.executable, str(root / "experiments/network/import_valencia_review.py"),
                            str(manifest), "--demo", BASE], check=True)
        snapshot = call("/api/situation/queries/snapshot")
    else:
        snapshot = call("/api/situation/commands/import-flypix", payload)
    feature = next(item for item in snapshot["reviewFeatures"] if item["layerId"] == key)
    assert feature["status"] == "Pending" and feature["segmentIds"]
    return snapshot, feature


def approve(snapshot, feature):
    return call("/api/situation/commands/review-decision", {
        "commandId": feature["id"] + "-approve", "expectedRevision": snapshot["revision"],
        "reviewFeatureId": feature["id"], "decision": "approve",
    })


network = call("/api/routing/queries/network")
assert network["scenario"] == "valencia" and not network["nodes"]
city = network["city"]
assert city["bounds"] == [-.62, 39.24, -.25, 39.61] and city["roadCount"] > 100_000
assert city["zoneCounts"]["safe"] > 0 and city["zoneCounts"]["hazard"] > 0
assert call("/maps/city.svg").startswith(b"<svg")
assert call("/maps/city-nodes.svg").startswith(b"<svg")
images = call("/maps/city-imagery.json")["scenes"]
assert images["before"]["bounds"] == images["event"]["bounds"]
assert call("/maps/sentinel-before.jpg").startswith(b"\xff\xd8")
assert call("/maps/sentinel-event.jpg").startswith(b"\xff\xd8")
assert b'id="valencia-tools"' in call("/")

initial = call("/api/situation/queries/snapshot")
assert initial["revision"] == 0 and not initial["closedSegments"]
assert initial["hazards"][0]["productId"] == "EMSR773_AOI01_DEL_PRODUCT_v1"
assert initial["hazards"][0]["source"] == "copernicus-ems-archive"
baseline = route()["route"]
assert baseline and baseline["segments"]
edge = call("/api/routing/queries/city-edge/" + baseline["segments"][0])
assert edge["lengthMetres"] > 0 and edge["pedestriansPerHour"] > 0
assert edge["class"] and isinstance(edge["intersectsObservedFlood"], bool)
node = call("/api/routing/queries/city-node/" + str(edge["from"]))
assert node["id"] == edge["from"] and node["zone"] == "safe"

pending, feature = review(0, "valencia-narrow-fixture", baseline["geometry"][len(baseline["geometry"]) // 2],
                          .00010, use_cli=True)
overlap = set(feature["segmentIds"]) & set(baseline["segments"])
assert overlap and not pending["closedSegments"] and route()["route"] == baseline
confirmed = approve(pending, feature)
assert overlap <= set(confirmed["closedSegments"])
assert {road["id"] for road in call("/api/situation/queries/closed-roads")["roads"]} == set(confirmed["closedSegments"])
rerouted = route()["route"]
assert rerouted and not set(rerouted["segments"]) & set(confirmed["closedSegments"])
check = call("/api/routing/queries/validate-route?" + urlencode({"segments": sorted(overlap)[0]}))
assert check["reason"] == "ClosedSegment"

isolated, barrier = review(confirmed["revision"], "valencia-isolation-fixture", [-.378, 39.477], .006)
approve(isolated, barrier)
assert route()["route"] is None
print(f"Valencia integration passed: {city['roadCount']:,} edges, classifications, evidence review, detour and no route.")
