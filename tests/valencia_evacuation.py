"""Task 03 evacuation model through the gateway: plan, distribution, draw, withdrawal and recalculation.

Called by valencia_integration.py with its HTTP helper; the services run in fresh temporary state.
"""

import json
from pathlib import Path
import time
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import ProxyHandler, Request, build_opener

HTTP = build_opener(ProxyHandler({}))
POPULATION = Path(__file__).resolve().parents[1] / "data/demo/routing/valencia/population.json"
PROFILES = ("independent", "slow", "wheelchair")


def request(base, path, body=None):
    """Return (status, JSON) without asserting, for answers whose status is part of the check."""
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"} if body is not None else {}
    try:
        response = HTTP.open(Request(base + path, data, headers), timeout=60)
    except HTTPError as error:
        response = error
    with response:
        return response.status, json.loads(response.read() or b"{}")


def wait_plan(base, revision, timeout=180):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        status, plan = request(base, "/api/routing/queries/evacuation-plan")
        if status == 200 and plan["revision"] == revision:
            return plan
        assert status in (200, 503), (status, plan)
        assert plan.get("code") != "PlanFailed", plan
        time.sleep(1)
    raise AssertionError(f"No evacuation plan for revision {revision} within {timeout} s.")


def distribution(base, node, profile="independent"):
    return request(base, "/api/routing/queries/distribution?" + urlencode({"node": node, "profile": profile}))


def check_plan(plan):
    assert plan["model"] == "logit-sue"
    people = plan["people"]
    assert people["kind"] == "estimate" and people["estimated"] > 100_000 and people["handedOver"] > 0
    routed = sum(plan["status"].values())
    assert abs(routed - people["guided"]) < 1, (routed, people)
    assert abs(routed + people["uninformed"] + people["handedOver"] - people["estimated"]) < 1, (routed, people)
    assert abs(sum(plan["statusAll"].values()) - routed - people["uninformed"]) < 1, plan["statusAll"]
    assert 0 <= plan["complianceShare"] <= 1
    assert plan["status"].get("Ok", 0) > 0.5 * routed, plan["status"]
    assert sum(shelter["expectedLoad"] for shelter in plan["shelters"]) <= routed + 1
    assert all(shelter["overflow"] < 0.02 * shelter["capacity"] + 500 for shelter in plan["shelters"]), plan["shelters"]
    assert all(0 < wave["iterations"] < 100 for wave in plan["waves"]), plan["waves"]


def find_nodes(base):
    """The most populated node split across two shelters, and one populated node without an eligible exit."""
    population = json.loads(POPULATION.read_text(encoding="utf-8"))["nodes"]
    split = blocked = None
    for item in sorted(population, key=lambda entry: -entry["people"]):
        status, answer = distribution(base, item["nodeId"])
        assert status == 200, (item, status, answer)
        if split is None and answer["status"] == "Ok" and len(answer["options"]) >= 2:
            split = answer
        if blocked is None and answer["status"] == "NoEligibleExit":
            blocked = answer
        if split and blocked:
            break
    assert split and blocked, "Expected a split node and a node without an exit in the Valencia plan."
    return split, blocked


def run(base):
    snapshot = request(base, "/api/situation/queries/snapshot")[1]
    plan = wait_plan(base, snapshot["revision"])
    check_plan(plan)

    split, blocked = find_nodes(base)
    node = split["node"]
    assert split["model"] == "logit-sue" and split["revision"] == plan["revision"]
    assert abs(sum(option["weight"] for option in split["options"]) - 1) < 1e-9
    assert all(option["segments"] and option["expectedLoad"] >= 0 and option["capacity"] > 0 for option in split["options"])
    assert split["leaveFloodMinutes"] <= split["deadlineMinutes"]
    assert blocked["options"] == [] and blocked["leaveFloodMinutes"] is None

    for profile in PROFILES:
        status, answer = distribution(base, node, profile)
        assert status == 200 and answer["profile"] == profile
    wheelchair = distribution(base, node, "wheelchair")[1]
    assert all(option["stepFree"] for option in wheelchair["options"]), wheelchair
    assert distribution(base, node, "assisted")[0] == 400  # handed over, not routed by task 03
    assert distribution(base, "not-a-node")[0] == 400

    draw = {"commandId": f"evacuation-{node}", "node": node, "revision": plan["revision"], "seed": 7,
            "mode": "independent"}
    status, drawn = request(base, "/api/routing/commands/draw", draw)
    assert status == 200 and drawn["route"]["exit"] in {option["exit"] for option in split["options"]}, drawn
    assert request(base, "/api/routing/commands/draw", draw)[1] == drawn
    status, conflict = request(base, "/api/routing/commands/draw", {**draw, "seed": 8})
    assert status == 409 and conflict["code"] == "CommandConflict", conflict
    status, repeated = request(base, "/api/routing/commands/draw", {**draw, "commandId": draw["commandId"] + "-again"})
    assert status == 200 and repeated["route"]["id"] == drawn["route"]["id"], "the same seed must reproduce the draw"
    status, refused = request(base, "/api/routing/commands/draw",
                              {**draw, "commandId": f"evacuation-{blocked['node']}", "node": blocked["node"]})
    assert status == 422 and refused["code"] == "NoEligibleExit", refused
    assert request(base, f"/api/routing/queries/draws/{draw['commandId']}")[1]["state"] == "Active"

    segments = drawn["route"]["segments"]
    segment = segments[len(segments) // 2]
    status, closed = request(base, "/api/situation/commands/closure", {
        "commandId": f"evacuation-close-{segment}", "segmentId": segment, "closed": True,
        "expectedRevision": plan["revision"]})
    assert status == 200 and segment in closed["closedSegments"], closed
    assert request(base, f"/api/routing/queries/draws/{draw['commandId']}")[1]["state"] == "Withdrawn"
    status, stale = request(base, "/api/routing/commands/draw", {**draw, "commandId": draw["commandId"] + "-stale"})
    assert status == 409 and stale["code"] == "RevisionConflict", stale
    status, pending = distribution(base, node)
    assert status == 503 and pending["code"] == "PlanCalculating" or status == 200 and pending["revision"] == closed["revision"]

    replanned = wait_plan(base, closed["revision"])
    check_plan(replanned)
    status, after = distribution(base, node)
    assert status == 200 and after["revision"] == closed["revision"]
    assert all(segment not in option["segments"] for option in after["options"]), "a closed segment is still offered"
    return (f"evacuation plan {plan['status'].get('Ok', 0):,.0f} on time, draw, withdrawal "
            f"and replanning in revision {closed['revision']}")
