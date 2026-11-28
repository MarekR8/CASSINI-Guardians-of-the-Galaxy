"""The documented exercise over the real gateway and four local services."""

from concurrent.futures import ThreadPoolExecutor
import json
import math
import sys
from urllib.error import HTTPError
from urllib.request import ProxyHandler, Request, build_opener


BASE = sys.argv[1]
LOCAL_HTTP = build_opener(ProxyHandler({}))


def request(path, body=None, expected=200):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"} if data else {}
    req = Request(BASE + path, data=data, headers=headers)
    try:
        response = LOCAL_HTTP.open(req, timeout=10)
    except HTTPError as error:
        response = error
    with response:
        content = response.read()
        assert response.status == expected, (req.full_url, response.status, content)
        if "json" in response.headers.get("content-type", ""):
            return json.loads(content)
        return content.decode()


def get(service, path):
    return request(f"/api/{service}/queries/{path}")


def post(service, path, body, expected=200):
    return request(f"/api/{service}/commands/{path}", body, expected)


page = request("/")
assert "Koordynacja ewakuacji" in page
assert 'id="satellite-credit"' in page and 'type="file"' not in page
assert 'id="map-zoom-in"' in page and 'id="map-zoom-reset"' in page
assert "commands/draw" in request("/app.js")
assert "createMapViewport" in request("/map-viewport.js")
scene = request("/maps/satellite.json")["sample"]
archive = scene["floodArchive"]
assert archive["productId"] == "EMSR756_AOI03_DEL_PRODUCT_observedEventA_v1"
assert archive["observedAt"] == "2024-09-15T05:01:00Z" and archive["geometry"]["coordinates"]
with LOCAL_HTTP.open(BASE + "/maps/" + scene["image"], timeout=10) as image:
    assert image.headers.get_content_type() == "image/jpeg"
    assert image.read(3) == b"\xff\xd8\xff"
with LOCAL_HTTP.open(BASE + "/maps/" + scene["detail"]["image"], timeout=10) as image:
    assert image.headers.get_content_type() == "image/jpeg"
    assert image.read(3) == b"\xff\xd8\xff"

initial = get("situation", "snapshot")
assert initial["revision"] == 0 and initial["closedSegments"] == []
assert initial["hazards"][0]["source"] == "synthetic"
assert initial["hazards"][0]["provenance"]["mode"] == "mock"
assert initial["populationEstimate"]["value"] == 4872

assert 'id="integration-preview"' in page
ems = get("situation", "integration-copernicus")
flypix = get("situation", "integration-flypix")
city_data = get("situation", "city-data")
city_map = get("situation", "city-map")
assert all(result["mode"] == "mock" for result in (ems, flypix, city_data, city_map))
assert ems["activationCode"] == "EMSR756" and ems["aoiNumber"] == 3
assert ems["productType"] == "delineation" and ems["productId"].startswith("mock-")
assert flypix["imageProductId"].startswith("mock-") and flypix["modelId"].startswith("mock-")
assert 0 <= flypix["geoJson"]["features"][0]["properties"]["confidence"] <= 1
assert city_data["datasetId"] == city_map["datasetId"]
assert city_data["zones"] == [{"zoneId": city_map["geoJson"]["features"][0]["properties"]["zoneId"], "people": 120}]
for preview in (ems, flypix, city_map):
    west, south, east, north = preview["bounds"]
    assert preview["geoJson"]["type"] == "FeatureCollection"
    geometry = preview["geoJson"]["features"][0]["geometry"]
    assert geometry["type"] == "Polygon"
    ring = geometry["coordinates"][0]
    assert ring[0] == ring[-1] and all(west <= lon <= east and south <= lat <= north for lon, lat in ring)
assert get("situation", "snapshot") == initial

alert_cmd = {"commandId": "alert-retry"}
first_alert = post("management", "alert", alert_cmd)
assert post("management", "alert", alert_cmd) == first_alert
incident = get("management", "incident")
assert len(incident["alerts"]) == 1

baseline = get("routing", "distribution?node=walk-start")
options = {option["exit"]: option for option in baseline["options"]}
assert set(options) == {"exit-east", "exit-north"}
assert math.isclose(sum(option["weight"] for option in options.values()), 1)
assert options["exit-east"]["weight"] > options["exit-north"]["weight"] > 0
assert options["exit-east"]["seconds"] == 780 and "19:20" in options["exit-east"]["departsAt"]
assert options["exit-north"]["seconds"] == 1080 and "19:22" in options["exit-north"]["departsAt"]

draw_cmd = {"commandId": "east-draw", "node": "walk-start", "revision": 0, "seed": 1}
east = post("routing", "draw", draw_cmd)
assert east["route"]["exit"] == "exit-east"
assert post("routing", "draw", draw_cmd) == east
north = post("routing", "draw", {**draw_cmd, "commandId": "north-draw", "seed": 0})
assert north["route"]["exit"] == "exit-north"

jobs = get("dispatch", "assignments")["requests"]


def assign(item):
    body = {"commandId": "assign-" + item["requestId"], "requestId": item["requestId"],
            "unitId": "unit-3", "expectedVersion": item["version"]}
    try:
        return post("dispatch", "assign", body)
    except AssertionError as failure:
        _, status, content = failure.args[0]
        assert status == 409 and json.loads(content)["code"] == "UnitUnavailable", failure
        return None


with ThreadPoolExecutor(max_workers=2) as pool:
    attempts = list(pool.map(assign, jobs[:2]))
assignments = [item for item in attempts if item]
assert len(assignments) == 1, attempts
assignment = assignments[0]
assert assignment["route"]["exit"] == "exit-east"

bridge = {"commandId": "close-bridge", "segmentId": "bridge-2", "closed": True, "expectedRevision": 0}
after_bridge = post("situation", "closure", bridge)
assert after_bridge["revision"] == 1
assert post("situation", "closure", bridge) == after_bridge
assert post("routing", "draw", draw_cmd, 409)["code"] == "RevisionConflict"
assert get("routing", "draws/east-draw")["state"] == "Withdrawn"
assert get("routing", "draws/north-draw")["state"] == "Active"
after_distribution = get("routing", "distribution?node=pickup")
assert len(after_distribution["options"]) == 1
assert after_distribution["options"][0]["exit"] == "exit-north"
assert after_distribution["options"][0]["weight"] == 1
assert "bridge-2" not in after_distribution["options"][0]["segments"]

status = get("dispatch", "assignments")["assignments"][0]
assert status["routeCurrent"] is False
assert post("dispatch", "progress", {
    "commandId": "stale-assignment-ack", "assignmentId": assignment["assignmentId"],
    "expectedVersion": assignment["version"], "step": "acknowledge",
}, 409)["code"] == "RouteRequiresReview"
assignment = post("dispatch", "replan", {
    "commandId": "replan-unit", "assignmentId": assignment["assignmentId"], "expectedVersion": assignment["version"],
})
assert assignment["route"]["exit"] == "exit-north"
for step in ("acknowledge", "arrive", "pickup", "complete"):
    assignment = post("dispatch", "progress", {
        "commandId": "unit-" + step, "assignmentId": assignment["assignmentId"],
        "expectedVersion": assignment["version"], "step": step,
    })
    case = get("management", "cases/" + assignment["caseId"])
    assert case["progress"] != "ExitConfirmed"
assert assignment["state"] == "Completed"

case = get("management", "cases/case-14")
case = post("management", "progress", {
    "commandId": "observed-position", "caseId": "case-14", "step": "position",
    "node": "pickup", "expectedVersion": case["version"], "observedAt": "2026-11-27T19:30:00Z",
})
assert case["node"] == "pickup" and case["progress"] == "Open"
new_request = post("dispatch", "request", {
    "commandId": "request-support", "caseId": "case-14", "pickupNode": "pickup",
})
assert new_request["pickupNode"] == "pickup" and new_request["caseId"] == "case-14"
for step in ("acknowledgement", "moving", "exit-confirmed"):
    case = post("management", "progress", {
        "commandId": "case-" + step, "caseId": "case-14", "step": step, "expectedVersion": case["version"],
    })
assert case["progress"] == "ExitConfirmed"

no_exit = post("situation", "closure", {
    "commandId": "close-north", "segmentId": "north-link", "closed": True, "expectedRevision": 1,
})
assert no_exit["stage"] == 2 and no_exit["revision"] == 2
assert get("routing", "distribution?node=pickup")["options"] == []
assert post("routing", "draw", {**draw_cmd, "node": "pickup", "revision": 2, "commandId": "no-route"},
            422)["code"] == "NoEligibleRoute"

sample = {
    "type": "FeatureCollection",
    "features": [{"type": "Feature", "properties": {}, "geometry": {
        "type": "Polygon", "coordinates": [[[16.64, 50.43], [16.66, 50.43], [16.66, 50.44],
                                            [16.64, 50.44], [16.64, 50.43]]],
    }}],
}
after_import = post("situation", "import-copernicus", {
    "commandId": "import-test", "expectedRevision": 2, "productId": "TEST-PRODUCT",
    "observedAt": "2026-11-27T18:00:00Z", "geoJson": sample,
})
imported = after_import["hazards"][-1]
assert imported["source"] == "copernicus-ems-operator-import" and imported["productId"] == "TEST-PRODUCT"
assert imported["provenance"]["mode"] == "recorded"
assert imported["provenance"]["observedAt"] == imported["observedAt"]
assert after_import["populationEstimate"] is None
assert get("routing", "distribution?node=pickup")["options"] == []

print("Offline integration passed: gateway, alert retry, weighted routes, withdrawal, dispatch booking and pickup, blocked exits, local import.")
