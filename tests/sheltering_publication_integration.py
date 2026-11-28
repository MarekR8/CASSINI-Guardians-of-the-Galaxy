"""SH-T06 Stage A: publish a committed shelter booking through Routing as a separate step (SH-A06, SH-A07).

A booking stays pending publication until Routing commits an instruction for exactly that hold. A failed publication
leaves it pending, a replacement is published with a newer route version, a closure withdraws the guidance, and
retries or stale commands never add a route version or restore withdrawn guidance."""

import argparse
from pathlib import Path
import sys
from tempfile import TemporaryDirectory

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheltering_reservations_integration import (EXPIRES, INCIDENT, LEG, Client, reserve, target, use_fixtures)
from offline_demo import ARTIFACTS, build, start_services

CASE = "case-31"
SUBJECT = f"{INCIDENT}/{CASE}/{LEG}"


def instruction(client):
    return client.query("sheltering/queries/shelter-instruction", {"incidentId": INCIDENT, "caseId": CASE, "legId": LEG})


def reconcile(client, command_id, version, expected=200, code=None):
    return client.command("reconcile-shelter-instruction", {
        "commandId": command_id, "correlationId": "corr-sh-t06", "incidentId": INCIDENT, "caseId": CASE, "legId": LEG,
        "expectedAssignmentVersion": version}, expected, code)


def route_outbox(client):
    return client.query("routing/queries/route-outbox", {"after": 0})["entries"]


def route_events(client):
    return [(entry["event"]["type"], entry["event"]["subjectVersion"], entry["event"]["payload"]["routeVersion"])
            for entry in route_outbox(client)]


def best(client, shelter_id):
    option = client.candidates([CASE], mode="walking", node="walk-start")["candidates"][0]
    assert option["shelterId"] == shelter_id, option
    return option


def book(client, command_id, shelter_id):
    option = best(client, shelter_id)
    _, result = reserve(client, command_id, shelter_id, option["shelterVersion"],
                        [target(client.assignment(CASE), option["route"])])
    return result["assignments"][0]


def invalidate(client, command_id):
    return client.command("invalidate-shelter-assignment", {
        "commandId": command_id, "correlationId": "corr-sh-t06", "incidentId": INCIDENT, "caseId": CASE, "legId": LEG,
        "expectedVersion": client.assignment(CASE)["version"], "reason": "ShelterClosed",
        "evidenceRef": "synthetic-closure-report"})["assignment"]


def withdraw_route(client, expected_version, route_version, expected, code):
    client.call("routing/commands/withdraw-route", {
        "commandId": f"direct-withdraw-{expected_version}-{route_version}", "correlationId": "corr-sh-t06",
        "subject": {"kind": "shelter-assignment", "id": SUBJECT}, "expectedVersion": expected_version,
        "routeVersion": route_version, "reasonCode": "CoordinatorReview"}, expected, code)


def publication(client):
    client.command("open-shelter-assignment", {
        "commandId": "open-case-31", "incidentId": INCIDENT, "caseId": CASE, "legId": LEG,
        "needs": {"places": 1, "accessiblePlaces": 0}, "groupPlanRef": None})
    held = book(client, "book-a", "shelter-a")
    pending = instruction(client)
    assert pending["state"] == "PendingPublication" and pending["instruction"]["aggregateVersion"] == 0, pending

    hold = held["reservation"]
    assignment_ref = {"incidentId": INCIDENT, "caseId": CASE, "legId": LEG, "shelterId": hold["shelterId"],
                      "reservationId": hold["reservationId"], "assignmentVersion": held["version"]}
    publish = {"subject": {"kind": "shelter-assignment", "id": SUBJECT}, "expectedVersion": 0,
               "route": hold["route"]}
    for suffix, changes in (("unverified", {"reservationId": "unverified"}),
                            ("stale", {"assignmentVersion": held["version"] - 1})):
        client.call("routing/commands/publish-route", {**publish, "commandId": f"publish-{suffix}",
                    "assignment": {**assignment_ref, **changes}}, 409, "ReservationChanged")
    changed, _ = client.route(hold["shelterId"], {**hold["route"]["query"], "clearBy": "2026-11-27T19:57:00Z"})
    client.call("routing/commands/publish-route", {**publish, "commandId": "publish-different-route",
                "assignment": assignment_ref, "route": changed}, 422, "ReservationDoesNotCoverRoute")
    assert route_events(client) == []

    # SH-A07: publication fails after booking. The shelter closes before Routing approves the instruction.
    client.configure("shelter-a", "close-a", status="closed")
    reconcile(client, "publish-a", held["version"], 422, "RouteIneligible")
    assert instruction(client)["state"] == "PendingPublication" and client.assignment(CASE) == held
    assert route_events(client) == [], "a failed publication commits nothing in Routing"
    invalidate(client, "invalidate-a")
    assert instruction(client)["state"] == "NoInstruction", "nothing was issued, so nothing is withdrawn"
    reconcile(client, "publish-a", held["version"], 409, "AssignmentVersionConflict")

    # SH-A06 replacement: published once, even when the command is retried after an unknown outcome.
    replacement = book(client, "book-b", "shelter-b")
    first = reconcile(client, "publish-b", replacement["version"])
    route = first["instruction"]["current"]
    assert first["state"] == "Current" and route["routeVersion"] == 1, first
    assert route["assignment"]["reservationId"] == replacement["reservation"]["reservationId"], route
    assert reconcile(client, "publish-b", replacement["version"]) == first
    assert instruction(client) == first
    assert route_events(client) == [("RoutePublished.v1", 1, 1)]
    withdraw_route(client, 0, 1, 409, "VersionConflict")
    withdraw_route(client, 1, 2, 409, "StaleRouteVersion")

    # A transfer to the reopened shelter-a supersedes route 1 with route 2: two events at one instruction version.
    client.configure("shelter-a", "reopen-a", status="open")
    option = best(client, "shelter-a")
    moved = client.command("reassign-shelter-places", {
        "commandId": "move-a", "correlationId": "corr-sh-t06", "fromShelterId": "shelter-b",
        "expectedFromShelterVersion": client.version("shelter-b"), "shelterId": "shelter-a",
        "expectedShelterVersion": option["shelterVersion"], "reason": "CoordinatorTransfer", "groupPlanRef": None,
        "holdsExpireAt": EXPIRES, "approvalRef": "synthetic-coordinator",
        "assignments": [target(client.assignment(CASE), option["route"])]})["assignments"][0]
    assert instruction(client)["state"] == "PendingPublication", "the old route is no longer the current hold"
    second = reconcile(client, "publish-move", moved["version"])
    assert second["state"] == "Current" and second["instruction"]["current"]["routeVersion"] == 2, second
    assert second["instruction"]["lastWithdrawal"]["reasonCode"] == "Superseded", second
    assert route_events(client)[1:] == [("RouteWithdrawn.v1", 2, 1), ("RoutePublished.v1", 2, 2)]
    reconcile(client, "publish-b", replacement["version"], 409, "AssignmentVersionConflict")
    assert instruction(client) == second, "an old command cannot restore route 1"

    # SH-A06 without a replacement: the closure withdraws the guidance; the retry keeps one withdrawal.
    client.configure("shelter-a", "close-a-again", status="closed")
    closed = invalidate(client, "invalidate-move")
    assert instruction(client)["state"] == "WithdrawalPending"
    withdrawn = reconcile(client, "withdraw-a", closed["version"])
    assert (withdrawn["state"], withdrawn["reason"], withdrawn["instruction"]["current"]) == ("Withdrawn", "ShelterClosed", None)
    assert reconcile(client, "withdraw-a", closed["version"]) == withdrawn
    events = route_events(client)
    assert events[3:] == [("RouteWithdrawn.v1", 3, 2)], events
    return {"version": closed["version"], "withdrawn": withdrawn, "events": events}


def after_restart(client, saved):
    assert instruction(client) == saved["withdrawn"]
    assert reconcile(client, "withdraw-a", saved["version"]) == saved["withdrawn"]
    assert route_events(client) == saved["events"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=5880)
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    if not args.no_build:
        build()
    use_fixtures()
    with TemporaryDirectory(prefix="sheltering-publication-", dir=ARTIFACTS) as directory:
        with start_services(args.port, Path(directory), sheltering=True) as base:
            saved = publication(Client(base))
        with start_services(args.port, Path(directory), sheltering=True) as base:
            after_restart(Client(base), saved)
    print("SH-T06 publication passed: a booking stayed pending until Routing published it, a failed publication left "
          "it pending, retries kept one route version, a transfer superseded route 1 with route 2, a closure withdrew "
          "the guidance, stale commands were rejected and the state survived a restart.")


if __name__ == "__main__":
    main()
