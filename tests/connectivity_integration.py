"""Task 6 over the real gateway: field link profiles, store-and-forward, evidenced exits and EWSS."""

import json
import sys
import time
from urllib.error import HTTPError
from urllib.request import ProxyHandler, Request, build_opener


BASE = sys.argv[1]
LOCAL_HTTP = build_opener(ProxyHandler({}))
FIELD = {"X-Cassini-Link": "field"}


def request(path, body=None, expected=200, headers=None):
    data = json.dumps(body).encode() if body is not None else None
    req = Request(BASE + path, data=data, headers={
        **({"Content-Type": "application/json"} if data else {}), **(headers or {})})
    try:
        response = LOCAL_HTTP.open(req, timeout=15)
    except HTTPError as error:
        response = error
    with response:
        content = response.read()
        assert response.status == expected, (req.full_url, response.status, content)
        return json.loads(content), response.headers


def get(service, path):
    return request(f"/api/{service}/queries/{path}")[0]


def post(service, path, body, expected=200):
    return request(f"/api/{service}/commands/{path}", body, expected)[0]


def field(service, path, body, expected=200):
    return request(f"/api/{service}/commands/{path}", body, expected, FIELD)


def set_profile(name):
    return post("link", "link-profile", {"commandId": f"profile-{name}-{time.monotonic_ns()}", "profile": name})


def wait_drained(timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        status = get("link", "link-status")
        if status["pending"] == 0:
            return status
        time.sleep(0.1)
    raise AssertionError(("Queue was not drained", status))


def counters():
    return get("management", "incident")["counters"]


exit_fix = {"observedAt": "2026-11-27T19:50:00Z", "latitude": 50.4410, "longitude": 16.6540, "accuracy": 9,
            "evidence": {"galileoUsed": True, "osnma": "simulated", "egnos": "unknown"}, "mode": "simulated"}

status = get("link", "link-status")
assert status["profile"]["name"] == "terrestrial" and status["pending"] == 0
assert {item["name"] for item in status["profiles"]} == {"terrestrial", "iris2", "govsatcom", "down"}
assert post("link", "link-profile", {"commandId": "bad-profile", "profile": "starlink"}, 400)["code"] == "UnknownProfile"

# Terrestrial: a field command is forwarded with the simulated transit time.
case = get("management", "cases/case-16")
case, headers = field("management", "progress", {
    "commandId": "field-ack-16", "caseId": "case-16", "step": "acknowledgement", "expectedVersion": case["version"]})
assert case["progress"] == "Acknowledged" and headers["X-Cassini-Link"] == "terrestrial"
assert int(headers["X-Cassini-Link-Transit-Ms"]) >= 10

# Down: commands wait in the queue and nothing reaches Management.
before = counters()
set_profile("down")
moving = {"commandId": "field-moving-16", "caseId": "case-16", "step": "moving", "expectedVersion": case["version"]}
queued, headers = field("management", "progress", moving, 202)
assert queued["status"] == "Queued" and queued["entry"]["state"] == "Queued" and headers["X-Cassini-Link"] == "down"
again, _ = field("management", "progress", moving, 202)
assert again["entry"]["entryId"] == queued["entry"]["entryId"], "A retry must not queue a second copy."
conflict, _ = field("management", "progress", {**moving, "expectedVersion": 99}, 409)
assert conflict["code"] == "CommandIdConflict"
exit_report = {"commandId": "field-exit-16", "caseId": "case-16", "step": "exit-confirmed", **exit_fix}
field("management", "progress", exit_report, 202)
assert get("management", "cases/case-16") == case, "Nothing is delivered while the link is down."
assert get("link", "link-status")["pending"] == 2
assert counters()["confirmedOutside"] == before["confirmedOutside"]

# IRIS²: the queue drains in order, once.
set_profile("iris2")
status = wait_drained()
delivered = [entry for entry in status["entries"] if entry["commandId"] in ("field-moving-16", "field-exit-16")]
assert [entry["state"] for entry in delivered] == ["Delivered", "Delivered"], delivered
assert all(entry["deliveredOver"] == "iris2" for entry in delivered)
case = get("management", "cases/case-16")
assert case["progress"] == "ExitConfirmed" and case["version"] == moving["expectedVersion"] + 2, case
retry, _ = field("management", "progress", moving)
assert retry["progress"] == "Moving", "A late retry returns the original receipt instead of a new record."
assert get("management", "cases/case-16")["version"] == case["version"]
assert counters()["confirmedOutside"] == before["confirmedOutside"] + 1

# Pickup does not count as outside the zone.
assisted = get("management", "cases/case-15")
picked = post("management", "progress", {
    "commandId": "pickup-15", "caseId": "case-15", "step": "pickup", "expectedVersion": assisted["version"]})
assert picked["progress"] == "PickedUp"
assert counters()["confirmedOutside"] == before["confirmedOutside"] + 1

# EWSS mock broadcast.
zone = {"type": "Polygon", "coordinates": [[[16.64, 50.43], [16.66, 50.43], [16.66, 50.44], [16.64, 50.44],
                                             [16.64, 50.43]]]}
alert = {"commandId": "ewss-zone", "level": "severe", "text": "Powódź. Opuść strefę trasą z aplikacji.", "zone": zone}
broadcast = post("management", "ewss-alert", alert)
assert broadcast["delivery"] == "simulated" and broadcast["provenance"]["mode"] == "mock"
assert post("management", "ewss-alert", alert) == broadcast
assert post("management", "ewss-alert", {**alert, "commandId": "ewss-long", "text": "x" * 101}, 400)["code"] == \
    "InvalidEwssAlert"
assert [item["broadcastId"] for item in get("management", "ewss-alerts")["alerts"]] == [broadcast["broadcastId"]]

# Measured transit per profile (field alert command, Gateway → Management on this machine).
measured = {}
for profile in ("terrestrial", "iris2", "govsatcom"):
    set_profile(profile)
    started = time.monotonic()
    _, headers = field("management", "alert", {"commandId": "latency-" + profile})
    measured[profile] = (round((time.monotonic() - started) * 1000), int(headers["X-Cassini-Link-Transit-Ms"]))
assert measured["terrestrial"][0] < measured["iris2"][0] < measured["govsatcom"][0], measured
assert measured["govsatcom"][1] >= 600
set_profile("terrestrial")

print("Connectivity integration passed: field queue while down, single delivery over IRIS², "
      "pickup vs exit counters, EWSS mock. Measured ms (total, simulated):", measured)
