"""Replay SH-A04 and SH-A06 (SH-T04 reservations and reassignment) through the Gateway and owner services."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from offline_demo import ARTIFACTS, LOCAL_HTTP, ROOT, build, start_services


def read(path):
    return json.loads((ROOT / path).read_text(encoding="utf-8"))


SITES = read("data/demo/sheltering/sample-sites.json")
AT = SITES["exerciseAt"]
INCIDENT = "incident-42"
LEG = "leg-shelter"
CLEAR_BY = "2026-11-27T19:58:00Z"
EXPIRES = "2026-11-27T20:00:00Z"
# Synthetic exercise people routed from a graph node. Management does not track them.
WALKERS = {"case-31": "walk-start", "case-32": "walk-start"}


class Client:
    """Calls owners through the Gateway; with `sheltering`, Sheltering paths go straight to that service instance."""

    def __init__(self, base, sheltering=None):
        self.base = base
        self.sheltering = sheltering

    def raw(self, path, body=None):
        prefix = "sheltering/"
        url = (self.sheltering + "/" + path[len(prefix):] if self.sheltering and path.startswith(prefix)
               else self.base + "/api/" + path)
        data = json.dumps(body).encode("utf-8") if body is not None else None
        try:
            response = LOCAL_HTTP.open(Request(url, data, {"Content-Type": "application/json"}), timeout=15)
        except HTTPError as error:
            response = error
        with response:
            return response.status, json.load(response)

    def call(self, path, body=None, expected=200, code=None):
        status, payload = self.raw(path, body)
        assert status == expected, (path, status, payload)
        if code is not None:
            assert payload["code"] == code, (path, payload)
        return payload

    def command(self, name, body, expected=200, code=None):
        return self.call("sheltering/commands/" + name, body, expected, code)

    def query(self, path, args, expected=200, code=None):
        return self.call(path + "?" + urlencode(args), expected=expected, code=code)

    def shelters(self):
        rows = self.query("sheltering/queries/shelter-availability", {"at": AT})["shelters"]
        return {row["shelterId"]: row for row in rows}

    def assignment(self, case_id):
        return self.query("sheltering/queries/shelter-assignment",
                          {"incidentId": INCIDENT, "caseId": case_id, "legId": LEG})["assignment"]

    def candidates(self, case_ids, **route):
        args = {"incidentId": INCIDENT, "legId": LEG, "caseIds": ",".join(case_ids), "at": AT, "clearBy": CLEAR_BY, **route}
        return self.query("sheltering/queries/shelter-candidates", args)

    def route(self, shelter_id, query):
        """Read SH-W3 evidence directly from Routing for one shelter."""
        args = {"incidentId": INCIDENT, "legId": LEG, "destinationKind": "shelter", "destinationId": shelter_id,
                **{key: str(value).lower() if isinstance(value, bool) else value
                   for key, value in query.items() if value is not None}}
        result = self.query("routing/queries/destination-route", args)
        return {"evidenceId": result["evidence"]["evidenceId"] if result["evidence"] else None, "query": query}, result

    def version(self, shelter_id):
        return self.shelters()[shelter_id]["version"]

    def reception(self, shelter_id):
        return self.query("sheltering/queries/shelter-reception", {"shelterId": shelter_id, "at": AT})["shelters"][0]

    def configure(self, shelter_id, command_id, **changes):
        site = next(item for item in SITES["shelters"] if item["shelterId"] == shelter_id)
        current = self.shelters()[shelter_id]
        body = {key: site[key] for key in ("shelterId", "name", "place", "stepFree", "operatingWindow", "evidence")}
        body.update(status=current["status"], resources=current["resources"], commandId=command_id,
                    expectedVersion=current["version"])
        body.update(changes)
        return self.command("configure-shelter", body)["item"]


def walker_query(case_id):
    return {"mode": "walking", "at": AT, "node": WALKERS[case_id], "clearBy": CLEAR_BY}


def target(assignment, route):
    return {"incidentId": INCIDENT, "caseId": assignment["caseId"], "legId": LEG,
            "expectedVersion": assignment["version"], "route": route}


def reserve(client, command_id, shelter_id, version, targets, plan=None, expected=200, code=None):
    body = {"commandId": command_id, "correlationId": "corr-sh-t04", "shelterId": shelter_id,
            "expectedShelterVersion": version, "groupPlanRef": plan, "holdsExpireAt": EXPIRES,
            "approvalRef": "synthetic-coordinator", "assignments": targets}
    return body, client.command("reserve-shelter-places", body, expected, code)


def setup_group(client):
    """Approve group-household plan revision 2: case-15 needs case-16 after a confirmed handover (SH-W2)."""
    household = read("contracts/http/grouping/household-split.json")
    care = read("contracts/http/grouping/care-handover.json")
    for path, body in (("register-evacuation-group", household["register"]), ("approve-travel-plan", household["approve"]),
                       ("update-group-composition", care["updateComposition"]),
                       ("record-care-handover", care["handover"]),
                       ("approve-travel-plan", {**care["approve"], "expectedPlanRevision": 1})):
        client.call("management/commands/" + path, body)
    plan = client.call("management/queries/approved-travel-plans/group-household?incidentId=" + INCIDENT)
    assert (plan["planRevision"], plan["status"]) == (2, "Approved"), plan
    return {"incidentId": INCIDENT, "groupId": "group-household", "planRevision": 2}


def exercise(client, peer=None):
    """Replay SH-A04 and SH-A06. With `peer`, the final-place race runs against a second Sheltering instance."""
    plan = setup_group(client)
    baseline = client.shelters()
    occupied_a = baseline["shelter-a"]["commitments"]["occupiedPlaces"]

    # OpenShelterAssignment: one pending assignment per case and leg; a retry returns the original.
    def open_assignment(case_id, accessible=0, group=None):
        body = {"commandId": f"open-{case_id}", "incidentId": INCIDENT, "caseId": case_id, "legId": LEG,
                "needs": {"places": 1, "accessiblePlaces": accessible}, "groupPlanRef": group}
        result = client.command("open-shelter-assignment", body)
        assert result["assignment"]["status"] == "Pending" and result["assignment"]["version"] == 1, result
        assert client.command("open-shelter-assignment", body) == result
        return body

    open_assignment("case-31")
    opened = open_assignment("case-32")
    open_assignment("case-14", group=plan)
    open_assignment("case-15", accessible=1, group=plan)
    open_assignment("case-16", accessible=1, group=plan)
    client.command("open-shelter-assignment", {**opened, "commandId": "reopen-32",
                                              "needs": {"places": 1, "accessiblePlaces": 1}}, 409, "AssignmentAlreadyOpen")
    client.command("open-shelter-assignment", {**opened, "commandId": "open-x", "caseId": "case-99",
                                              "groupPlanRef": {**plan, "planRevision": 1}}, 409, "GroupPlanChanged")

    # SH-A04: shelter-a has exactly one free place (4 usable, 3 committed in the baseline).
    client.configure("shelter-a", "a-last-place", resources={"places": 4, "accessiblePlaces": 2, "intakePerHour": 6})
    assert client.shelters()["shelter-a"]["available"]["places"] == 1
    ranked = client.candidates(["case-31"], mode="walking", node="walk-start")
    assert [item["shelterId"] for item in ranked["candidates"]] == ["shelter-a", "shelter-b"], ranked
    assert ranked["candidates"][0]["arrivalWindow"]["earliest"] < ranked["candidates"][1]["arrivalWindow"]["earliest"]
    racers = {}
    for case_id in WALKERS:
        best = client.candidates([case_id], mode="walking", node="walk-start")["candidates"][0]
        assert best["shelterId"] == "shelter-a"
        racers[case_id] = {"commandId": f"race-{case_id}", "correlationId": "corr-race", "shelterId": "shelter-a",
                           "expectedShelterVersion": best["shelterVersion"], "groupPlanRef": None,
                           "holdsExpireAt": EXPIRES, "approvalRef": "synthetic-coordinator",
                           "assignments": [target(client.assignment(case_id), best["route"])]}
    with ThreadPoolExecutor(max_workers=2) as pool:
        outcomes = dict(zip(racers, pool.map(
            lambda sender, body: sender.raw("sheltering/commands/reserve-shelter-places", body),
            [client, peer or client], racers.values())))
    statuses = sorted(status for status, _ in outcomes.values())
    assert statuses == [200, 422], outcomes
    winner = next(case for case, (status, _) in outcomes.items() if status == 200)
    loser = next(case for case in WALKERS if case != winner)
    assert outcomes[loser][1]["code"] == "InsufficientCapacity", outcomes
    assert client.assignment(loser)["status"] == "Pending" and client.assignment(loser)["version"] == 1
    assert client.command("reserve-shelter-places", racers[winner]) == outcomes[winner][1], "retry returns the receipt"
    client.command("reserve-shelter-places", {**racers[winner], "holdsExpireAt": "2026-11-27T20:05:00Z"},
                   409, "CommandConflict")
    a_row = client.shelters()["shelter-a"]
    assert a_row["available"]["places"] == 0 and a_row["commitments"]["heldPlaces"] == 2, a_row

    retry = client.candidates([loser], mode="walking", node="walk-start")
    assert {item["shelterId"]: item["reason"] for item in retry["rejected"]} == {"shelter-a": "InsufficientCapacity"}
    loser_route = retry["candidates"][0]
    reserve(client, f"loser-{loser}", "shelter-b", loser_route["shelterVersion"],
            [target(client.assignment(loser), loser_route["route"])])

    # Batch rejection: the required-care party needs two accessible places and shelter-b has one.
    party = {"mode": "driving", "groupId": "group-household", "groupRevision": 3, "transportId": "transport-demo-north"}
    party_ids = ["case-15", "case-16"]
    blocked = client.candidates(party_ids, **party)
    assert not blocked["candidates"], blocked
    assert {item["shelterId"]: item["reason"] for item in blocked["rejected"]} == {
        "shelter-a": "InsufficientCapacity", "shelter-b": "InsufficientAccessiblePlaces"}, blocked
    party_route, _ = client.route("shelter-b", {**party, "at": AT, "caseIds": ",".join(party_ids), "clearBy": CLEAR_BY})
    party_targets = lambda: [target(client.assignment(case_id), party_route) for case_id in party_ids]
    before = client.shelters()
    reserve(client, "party-accessible", "shelter-b", 1, party_targets(), plan, 422, "InsufficientAccessiblePlaces")
    assert client.shelters() == before, "a rejected batch leaves no partial hold"
    assert all(client.assignment(case_id)["status"] == "Pending" for case_id in party_ids)
    reserve(client, "party-split", "shelter-b", 1, party_targets()[:1], plan, 422, "RequiredCareSplit")
    reserve(client, "party-unplanned", "shelter-b", 1, party_targets(), None, 409, "GroupPlanRefMismatch")

    client.configure("shelter-b", "b-accessible-intake",
                     resources={"places": 4, "accessiblePlaces": 2, "intakePerHour": 3})
    reserve(client, "party-stale-route", "shelter-b", 2, party_targets(), plan, 422, "RouteIneligible")
    fresh = client.candidates(party_ids, **party)
    assert fresh["candidates"][0]["shelterId"] == "shelter-b", fresh
    party_route = fresh["candidates"][0]["route"]
    _, booked = reserve(client, "party-b", "shelter-b", 2, party_targets(), plan)
    assert [item["status"] for item in booked["assignments"]] == ["Reserved", "Reserved"], booked
    assert client.shelters()["shelter-b"]["commitments"]["heldAccessiblePlaces"] == 2

    # SH-W4 -> SH-T05: the holds sit in shelter-b's reception ledger, so admission converts exactly this hold.
    awaiting = {item["caseId"]: item["reservationId"] for item in client.reception("shelter-b")["awaiting"]}
    assert awaiting == {loser: f"loser-{loser}-{loser}", "case-15": "party-b-case-15", "case-16": "party-b-case-16"}, awaiting
    before_admission = client.shelters()["shelter-b"]
    admitted = client.command("confirm-shelter-admission", {
        "commandId": "admit-16", "expectedVersion": before_admission["version"], "shelterId": "shelter-b",
        "incidentId": INCIDENT, "reportId": "report-admit-16", "observedAt": "2026-11-27T19:30:00Z",
        "arrivals": [{"caseId": "case-16", "legId": LEG, "reservationId": "party-b-case-16"}]})
    assert admitted["entries"][0]["outcome"] == "Admitted", admitted
    after_admission = client.shelters()["shelter-b"]
    assert after_admission["available"] == before_admission["available"], "admission keeps free places unchanged"
    assert after_admission["commitments"]["occupiedPlaces"] == before_admission["commitments"]["occupiedPlaces"] + 1
    admitted_16 = client.assignment("case-16")
    assert admitted_16["status"] == "Admitted", admitted_16
    client.command("invalidate-shelter-assignment", {
        "commandId": "invalidate-admitted", "incidentId": INCIDENT, "caseId": "case-16", "legId": LEG,
        "expectedVersion": admitted_16["version"], "reason": "ShelterClosed"}, 422, "OccupancyRequiresDeparture")
    client.command("release-shelter-reservation", {
        "commandId": "release-admitted", "incidentId": INCIDENT, "caseId": "case-16", "legId": LEG,
        "expectedVersion": admitted_16["version"], "reason": "Cancelled", "stillRequired": False,
        "approvalRef": "synthetic-coordinator"}, 422, "OccupancyRequiresDeparture")

    # Intake window: shelter-b accepts three arrivals in the 19:00 hour and has a free place for case-14.
    late = client.candidates(["case-14"], mode="walking")
    assert {item["shelterId"]: item["reason"] for item in late["rejected"]} == {
        "shelter-a": "InsufficientCapacity", "shelter-b": "IntakeLimitReached"}, late
    walk_route, _ = client.route("shelter-b", {"mode": "walking", "at": AT, "caseIds": "case-14", "clearBy": CLEAR_BY})
    reserve(client, "case-14-b", "shelter-b", client.version("shelter-b"),
            [target(client.assignment("case-14"), walk_route)], plan, 422, "IntakeLimitReached")
    assert client.assignment("case-14")["status"] == "Pending"

    # SH-A06 failed transfer: shelter-b cannot take the winner, so the original hold and route remain current.
    held = client.assignment(winner)
    move_route, _ = client.route("shelter-b", walker_query(winner))
    transfer = {"commandId": "move-winner-early", "correlationId": "corr-transfer", "fromShelterId": "shelter-a",
                "expectedFromShelterVersion": 2, "shelterId": "shelter-b",
                "expectedShelterVersion": client.version("shelter-b"),
                "reason": "CoordinatorTransfer", "groupPlanRef": None, "holdsExpireAt": EXPIRES,
                "approvalRef": "synthetic-coordinator", "assignments": [target(held, move_route)]}
    client.command("reassign-shelter-places", transfer, 422, "IntakeLimitReached")
    assert client.assignment(winner) == held
    hold = held["reservation"]
    _, original = client.route("shelter-a", hold["route"]["query"])
    assert original["status"] == "Eligible" and original["siteVersion"] == hold["shelterVersion"], original
    client.command("invalidate-shelter-assignment", {
        "commandId": "invalidate-eligible", "incidentId": INCIDENT, "caseId": winner, "legId": LEG,
        "expectedVersion": held["version"], "reason": "CoordinatorReview"}, 422, "AssignmentStillEligible")

    # SH-A06 replacement: shelter-a closes while the winner is en route; shelter-b takes the hold atomically.
    client.configure("shelter-a", "close-a", status="closed")
    client.configure("shelter-b", "b-lift-intake", resources={"places": 5, "accessiblePlaces": 2, "intakePerHour": None})
    options = client.candidates([winner], mode="walking", node="walk-start")
    assert {item["shelterId"]: item["reason"] for item in options["rejected"]} == {"shelter-a": "CurrentShelter"}
    replacement = options["candidates"][0]
    moved = client.command("reassign-shelter-places", {
        **transfer, "commandId": "move-winner", "expectedFromShelterVersion": 3,
        "expectedShelterVersion": replacement["shelterVersion"], "reason": "ShelterClosed",
        "assignments": [target(held, replacement["route"])]})
    after = moved["assignments"][0]
    assert (after["status"], after["version"], after["reservation"]["shelterId"]) == ("Reserved", held["version"] + 1, "shelter-b")
    assert after["withdrawn"]["reservationId"] == hold["reservationId"]
    rows = client.shelters()
    assert rows["shelter-a"]["commitments"]["heldPlaces"] == baseline["shelter-a"]["commitments"]["heldPlaces"]
    assert rows["shelter-a"]["commitments"]["occupiedPlaces"] == occupied_a, "occupancy stays until departure"
    assert rows["shelter-b"]["commitments"]["heldPlaces"] == 3
    assert client.assignment("case-16")["status"] == "Admitted"

    # SH-A06 without a replacement: shelter-b also closes; the loser awaits replacement and its hold is released.
    client.configure("shelter-b", "close-b", status="closed")
    none_left = client.candidates([loser], mode="walking", node="walk-start")
    assert not none_left["candidates"], none_left
    current = client.assignment(loser)
    invalidated = client.command("invalidate-shelter-assignment", {
        "commandId": "invalidate-loser", "correlationId": "corr-closure", "incidentId": INCIDENT, "caseId": loser,
        "legId": LEG, "expectedVersion": current["version"], "reason": "ShelterClosed",
        "evidenceRef": "synthetic-closure-report"})["assignment"]
    assert (invalidated["status"], invalidated["reason"], invalidated["reservation"]) == (
        "AwaitingReplacement", "SiteClosed", None), invalidated
    assert client.shelters()["shelter-b"]["commitments"]["heldPlaces"] == 2

    # ReleaseShelterReservation: an agreed expiry needs evidence; a cancellation ends the need.
    release = {"commandId": "release-early", "incidentId": INCIDENT, "caseId": winner, "legId": LEG,
               "expectedVersion": after["version"], "reason": "HoldExpired", "stillRequired": True,
               "approvalRef": "synthetic-coordinator", "observedAt": "2026-11-27T19:50:00Z"}
    client.command("release-shelter-reservation", release, 422, "HoldNotExpired")
    closed = client.command("release-shelter-reservation", {**release, "commandId": "cancel-winner",
                                                             "reason": "Cancelled", "stillRequired": False})
    assert closed["assignment"]["status"] == "Closed"
    assert client.shelters()["shelter-b"]["commitments"]["heldPlaces"] == 1
    client.command("reserve-shelter-places", {"commandId": "empty", "shelterId": "shelter-b", "expectedShelterVersion": 4,
                                              "holdsExpireAt": EXPIRES, "approvalRef": "x", "assignments": []},
                   400, "InvalidBatch")

    outbox = client.query("sheltering/queries/allocation-outbox", {"after": 0})["entries"]
    changes = [entry["event"] for entry in outbox if entry["event"]["type"] == "ShelterAssignmentChanged.v1"]
    assert any(item["payload"]["status"] == "AwaitingReplacement" and item["payload"]["withdrawnShelterId"] == "shelter-b"
               for item in changes), changes
    assert any(item["payload"]["withdrawnShelterId"] == "shelter-a" and item["payload"]["shelterId"] == "shelter-b"
               for item in changes)
    assert [entry["sequence"] for entry in outbox] == list(range(1, len(outbox) + 1))
    return {"race": racers[winner], "raceResult": outcomes[winner][1], "loser": loser,
            "loserVersion": invalidated["version"], "outbox": len(outbox), "winner": winner}


def after_restart(client, saved):
    assert client.command("reserve-shelter-places", saved["race"]) == saved["raceResult"]
    restored = client.assignment(saved["loser"])
    assert (restored["status"], restored["version"]) == ("AwaitingReplacement", saved["loserVersion"]), restored
    assert len(client.query("sheltering/queries/allocation-outbox", {"after": 0})["entries"]) == saved["outbox"]
    assert client.shelters()["shelter-b"]["commitments"]["heldPlaces"] == 1
    assert client.assignment("case-16")["status"] == "Admitted"


def use_fixtures():
    """Synthetic route, transport and grouping evidence that the exercise depends on."""
    os.environ["Routing__DestinationEvidenceFile"] = str(ROOT / "data/demo/sheltering/sample-destination-evidence.json")
    os.environ["Grouping__EvidenceFile"] = str(ROOT / "data/demo/sheltering/sample-grouping.json")
    ARTIFACTS.mkdir(exist_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=5780)
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    if not args.no_build:
        build()
    use_fixtures()
    with TemporaryDirectory(prefix="sheltering-reservations-", dir=ARTIFACTS) as directory:
        with start_services(args.port, Path(directory), sheltering=True) as base:
            saved = exercise(Client(base))
        with start_services(args.port, Path(directory), sheltering=True) as base:
            after_restart(Client(base), saved)
    print(f"SH-T04 reservations passed: {saved['winner']} took the final place, batches stayed all-or-nothing, "
          "an SH-T05 admission converted an SH-T04 hold, a failed transfer kept the original hold, closure produced "
          "a transfer and an unresolved assignment, and state survived a restart.")


if __name__ == "__main__":
    main()
