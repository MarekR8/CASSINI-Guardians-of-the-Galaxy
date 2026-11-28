"""Verify the opt-in catalog through the real Gateway and owner services."""

from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import sys
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import ProxyHandler, Request, build_opener


ROOT = Path(__file__).resolve().parents[1]
FIXTURE = json.loads((ROOT / "data/demo/sheltering/sample-sites.json").read_text(encoding="utf-8"))
BASE = sys.argv[1]
HTTP = build_opener(ProxyHandler({}))
AT = FIXTURE["exerciseAt"]
INCIDENT = "incident-42"


def request(path, payload=None):
    data = json.dumps(payload).encode() if payload is not None else None
    headers = {"Content-Type": "application/json"} if data else {}
    try:
        response = HTTP.open(Request(BASE + path, data, headers), timeout=10)
    except HTTPError as error:
        response = error
    with response:
        body = response.read()
        result = json.loads(body) if "json" in response.headers.get("Content-Type", "") else body
        return response.status, result


def check(path, payload=None, expected=200):
    status, result = request(path, payload)
    assert status == expected, (path, status, result)
    return result


def points(at=AT, eligible=False):
    query = urlencode({"incidentId": INCIDENT, "at": at, "eligibleOnly": str(eligible).lower()})
    return check("/api/situation/queries/assembly-points?" + query)


def shelters(at=AT, eligible=False):
    query = urlencode({"at": at, "eligibleOnly": str(eligible).lower()})
    return check("/api/sheltering/queries/shelter-availability?" + query)


def candidates(at=AT):
    return check("/api/routing/queries/destinations?" + urlencode({"incidentId": INCIDENT, "at": at}))


def command(fixture, command_id, version=None, **changes):
    args = {key: value for key, value in fixture.items() if key not in ("version", "commitments")}
    return {**args, "commandId": command_id,
            "expectedVersion": fixture["version"] if version is None else version, **changes}


def ids(rows, field):
    return {row[field] for row in rows}


POINT_PATH = "/api/situation/commands/set-assembly-point"
SHELTER_PATH = "/api/sheltering/commands/configure-shelter"
point = FIXTURE["assemblyPoints"][0]
a, b = FIXTURE["shelters"]
close_a = command(a, "close-a", version=2, status="closed")
close_point = command(point, "close-point", status="closed")

if "--after-restart" in sys.argv:
    assert check(SHELTER_PATH, close_a)["item"]["version"] == 3
    assert check(POINT_PATH, close_point)["item"]["version"] == 2
    assert ids(candidates()["shelters"], "shelterId") == {"shelter-b"}
    assert not candidates()["assemblyPoints"]
    assert shelters()["shelters"][0]["commitments"] == a["commitments"]
    print("Sheltering restart: saved state and command receipts survived.")
    sys.exit(0)

assert check("/features") == {"sheltering": True}
assert b'id="site-catalog"' in check("/") and b"queries/shelter-availability" in check("/site-catalog.js")
nodes = {node["id"]: node for node in check("/api/routing/queries/network")["nodes"]}
for site in [point, a, b]:
    node = nodes[site["place"]["graphNodeId"]]
    assert (node["longitude"], node["latitude"]) == (site["place"]["longitude"], site["place"]["latitude"])

start = candidates()
assert start["situationRevision"] == start["assemblyPointRevision"] == start["shelterRevision"] == 0
assert ids(start["shelters"], "shelterId") == {"shelter-a", "shelter-b"}
assert ids(start["assemblyPoints"], "pointId") == {"assembly-north"}
assert shelters()["shelters"][0]["available"] == {"places": 5, "accessiblePlaces": 1}

draft = command(point, "new-point", version=0, pointId="assembly-review", status="draft",
                evidence={**point["evidence"], "verification": "unverified"})
assert check(POINT_PATH, draft)["item"]["version"] == 1
assert ids(points()["assemblyPoints"], "pointId") == {"assembly-north", "assembly-review"}
assert ids(points(eligible=True)["assemblyPoints"], "pointId") == {"assembly-north"}
invalid_open = {**draft, "commandId": "open-without-verification", "expectedVersion": 1, "status": "open"}
assert check(POINT_PATH, invalid_open, 422)["code"] == "UnverifiedEvidence"
assert check(SHELTER_PATH, command(b, "update-occupancy", commitments=b["commitments"]), 400) is not None

reduced = command(a, "reduce-a", resources={"places": 1, "accessiblePlaces": 1, "intakePerHour": 1})
changed = check(SHELTER_PATH, reduced)
assert changed["item"]["commitments"] == a["commitments"]
row = shelters()["shelters"][0]
assert row["overcommitted"] and not row["eligible"] and row["ineligibleReason"] == "Overcommitted"

closed = check(SHELTER_PATH, close_a)
assert check(SHELTER_PATH, close_a) == closed
assert check(SHELTER_PATH, {**close_a, "name": "Conflicting payload"}, 409)["code"] == "CommandConflict"
assert check(SHELTER_PATH, {**close_a, "commandId": "old-version"}, 409)["code"] == "VersionConflict"
assert shelters()["shelters"][0]["ineligibleReason"] == "SiteClosed"
assert ids(candidates()["shelters"], "shelterId") == {"shelter-b"}
assert not shelters("2026-11-27T21:00:00Z", eligible=True)["shelters"]

assert check(POINT_PATH, close_point)["item"]["status"] == "closed"
assert not candidates()["assemblyPoints"]
assert next(item for item in points()["assemblyPoints"] if item["pointId"] == "assembly-north")["status"] == "closed"
assert request("/api/situation/commands/check-in", {"caseId": "case-14"})[0] in (404, 405)
assert shelters()["shelters"][0]["commitments"] == a["commitments"]

with ThreadPoolExecutor(max_workers=2) as pool:
    outcomes = list(pool.map(lambda index: request(SHELTER_PATH,
                        command(b, f"b-update-{index}", name=f"North B {index}")), (1, 2)))
assert sorted(status for status, _ in outcomes) == [200, 409], outcomes

check("/api/situation/commands/closure", {
    "commandId": "close-bridge", "expectedRevision": 0, "segmentId": "bridge-2", "closed": True,
})
assert not candidates()["shelters"]
assert shelters()["shelters"][1]["ineligibleReason"] == "StaleSituationEvidence"
stale = command(b, "stale-opening", version=2)
assert check(SHELTER_PATH, stale, 409)["code"] == "StaleSituationEvidence"
verified = command(b, "reverify-b", version=2, evidence={**b["evidence"], "situationRevision": 1})
check(SHELTER_PATH, verified)
assert ids(candidates()["shelters"], "shelterId") == {"shelter-b"}
print("Sheltering catalogs: verified availability, limits, closure, current evidence and concurrent updates.")
