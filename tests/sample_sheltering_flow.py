"""One sample incident through the default stack. All people and position evidence are synthetic."""

from datetime import datetime, timedelta
import json
import math
import sys

from sheltering_reservations_integration import Client, INCIDENT, LEG, reserve, target
from offline_demo import LOCAL_HTTP


def exercise(base):
    client = Client(base)
    with LOCAL_HTTP.open(base + "/features", timeout=5) as response:
        assert json.load(response) == {"sheltering": True}

    case_id = "case-14"
    ref = {"incidentId": INCIDENT, "caseId": case_id, "legId": LEG}
    client.call("management/commands/alert", {"commandId": "flow-alert"})
    distribution = client.query("routing/queries/distribution", {"node": "walk-start"})
    assert distribution["revision"] == 0 and math.isclose(sum(r["weight"] for r in distribution["options"]), 1)
    draw = {"commandId": "flow-draw-east", "node": "walk-start", "revision": 0, "seed": 1}
    assert client.call("routing/commands/draw", draw)["route"]["exit"] == "exit-east"

    # No site has spare places, but the first leg remains possible.
    client.configure("shelter-a", "flow-a-full", resources={"places": 3, "accessiblePlaces": 1, "intakePerHour": 6})
    client.configure("shelter-b", "flow-b-full", resources={"places": 0, "accessiblePlaces": 0, "intakePerHour": None})
    client.command("open-shelter-assignment", {**ref, "commandId": "flow-open", "needs": {"places": 1, "accessiblePlaces": 0}})
    assert client.assignment(case_id)["status"] == "Pending"
    pending = client.candidates([case_id], mode="walking")
    assert not pending["candidates"]
    first = client.query("routing/queries/first-leg", {**ref, "legId": "leg-exit", "caseIds": case_id,
                         "mode": "walking", "at": "2026-11-27T19:20:00Z", "clearBy": "2026-11-27T19:58:00Z"})
    assert (first["status"], first["shelterStatus"]) == ("ShelterPending", "Pending")
    assert first["route"]["status"] == "Eligible"

    # A real owner change invalidates the previous draw and forces the northern exit.
    client.call("situation/commands/closure", {"commandId": "flow-close-bridge", "segmentId": "bridge-2", "closed": True,
                                               "expectedRevision": 0})
    assert client.call("routing/queries/draws/flow-draw-east")["state"] == "Withdrawn"
    north = client.call("routing/commands/draw", {**draw, "commandId": "flow-draw-north", "revision": 1})["route"]
    assert north["exit"] == "exit-north" and "bridge-2" not in north["segments"]
    case = client.call("management/queries/cases/" + case_id)
    arrival = north["arrivesAt"]
    case = client.call("management/commands/progress", {"commandId": "flow-position", "caseId": case_id,
                       "expectedVersion": case["version"], "step": "position", "node": "exit-north", "observedAt": arrival})
    assert case["progress"] != "ExitConfirmed"
    case = client.call("management/commands/progress", {"commandId": "flow-confirm-exit", "caseId": case_id,
                       "expectedVersion": case["version"], "step": "exit-confirmed", "observedAt": arrival,
                       "latitude": 50.441, "longitude": 16.654, "accuracy": 12, "mode": "simulated",
                       "evidence": {"galileoUsed": False, "osnma": "none", "egnos": "unknown"}})
    assert case["progress"] == "ExitConfirmed" and case["exit"]["mode"] == "simulated"
    assert client.assignment(case_id)["status"] == "Pending", "exit is not an admission"

    # Another assisted party in the same incident has required care. No independent route may split it.
    group = {"incidentId": INCIDENT, "groupId": "flow-care-pair", "caseIds": ["case-15", "case-16"]}
    registered = client.call("management/commands/register-evacuation-group", {**group, "commandId": "flow-care-register",
                             "relationships": [{"kind": "required-care", "caseId": "case-15", "relatedCaseId": "case-16",
                                                "evidenceRef": "synthetic-care-report"}], "evidenceRef": "synthetic-care-report"})
    client.query("routing/queries/first-leg", {"incidentId": INCIDENT, "legId": "leg-exit", "caseIds": "case-15",
                 "mode": "driving", "groupId": group["groupId"], "groupRevision": registered["groupRevision"],
                 "at": "2026-11-27T19:30:00Z", "clearBy": "2026-11-27T19:58:00Z"}, 422, "RequiredCareSplit")
    pair = client.query("routing/queries/first-leg", {"incidentId": INCIDENT, "legId": "leg-exit", "caseIds": "case-15,case-16",
                        "mode": "driving", "groupId": group["groupId"], "groupRevision": registered["groupRevision"],
                        "at": "2026-11-27T19:30:00Z", "clearBy": "2026-11-27T19:58:00Z"})
    assert pair["status"] == "EscalationRequired" and pair["route"] is None
    requests = client.call("dispatch/queries/assignments")["requests"]
    assert any(item["caseId"] == "case-15" for item in requests), "assistance is tracked separately from allocation"

    # Reverify site evidence after the road revision and make places available. Use the live case position.
    at = datetime.fromisoformat(arrival.replace("Z", "+00:00")) + timedelta(minutes=1)
    query = {"caseIds": case_id, "mode": "walking", "at": at.isoformat()}
    evidence = {"source": "synthetic-flow-operator", "mode": "synthetic", "verification": "verified",
                "observedAt": "2026-11-27T19:30:00Z", "validUntil": "2026-11-27T21:00:00Z", "situationRevision": 1}
    client.configure("shelter-b", "flow-b-verify", evidence=evidence,
                     resources={"places": 2, "accessiblePlaces": 1, "intakePerHour": None})
    candidate = client.query("sheltering/queries/shelter-candidates", {**ref, **query})["candidates"][0]
    assert candidate["shelterId"] == "shelter-b"
    _, booked = reserve(client, "flow-book-b", "shelter-b", candidate["shelterVersion"],
                        [target(client.assignment(case_id), candidate["route"])])
    held = booked["assignments"][0]
    assert held["status"] == "Reserved"
    instruction = lambda: client.query("sheltering/queries/shelter-instruction", ref)
    publish = lambda command_id, version: client.command("reconcile-shelter-instruction", {**ref, "commandId": command_id,
                                                                                           "expectedAssignmentVersion": version})
    assert instruction()["state"] == "PendingPublication"
    assert publish("flow-publish-b", held["version"])["state"] == "Current"
    assert client.reception("shelter-b")["counters"]["present"]["people"] == 0

    # Shelter closure withdraws the instruction without erasing the independently confirmed exit.
    client.configure("shelter-b", "flow-close-b", status="closed", evidence=evidence)
    invalidated = client.command("invalidate-shelter-assignment", {**ref, "commandId": "flow-invalidate-b",
                                 "expectedVersion": held["version"], "reason": "ShelterClosed"})["assignment"]
    assert invalidated["status"] == "AwaitingReplacement"
    assert publish("flow-withdraw-b", invalidated["version"])["state"] == "Withdrawn"
    assert client.call("management/queries/cases/" + case_id)["progress"] == "ExitConfirmed"

    # A new verified place at A requires the outside-only driving connection. No return through the hazard.
    client.configure("shelter-a", "flow-open-a", evidence=evidence,
                     resources={"places": 5, "accessiblePlaces": 1, "intakePerHour": 6})
    candidate = client.query("sheltering/queries/shelter-candidates", {**ref, **query, "mode": "driving"})["candidates"][0]
    assert candidate["shelterId"] == "shelter-a"
    _, route = client.route("shelter-a", candidate["route"]["query"])
    assert route["segments"] == ["outside-connector"]
    _, replacement = reserve(client, "flow-rebook-a", "shelter-a", candidate["shelterVersion"],
                             [target(client.assignment(case_id), candidate["route"])])
    replaced = replacement["assignments"][0]
    current = publish("flow-republish-a", replaced["version"])
    assert current["state"] == "Current" and current["instruction"]["current"]["routeVersion"] == 2
    before = client.shelters()["shelter-a"]["commitments"]
    admission = {"commandId": "flow-admit", "expectedVersion": client.version("shelter-a"), "shelterId": "shelter-a",
                 "incidentId": INCIDENT, "reportId": "flow-arrival-report", "observedAt": candidate["arrivalWindow"]["earliest"],
                 "arrivals": [{"caseId": case_id, "legId": LEG, "reservationId": replaced["reservation"]["reservationId"]}]}
    admitted = client.command("confirm-shelter-admission", admission)
    assert admitted["entries"][0]["outcome"] == "Admitted" and client.command("confirm-shelter-admission", admission) == admitted
    assert client.assignment(case_id)["status"] == "Admitted"
    after = client.shelters()["shelter-a"]["commitments"]
    assert after["heldPlaces"] == before["heldPlaces"] - 1 and after["occupiedPlaces"] == before["occupiedPlaces"] + 1
    departure = {"commandId": "flow-depart", "expectedVersion": client.version("shelter-a"), "shelterId": "shelter-a",
                 "incidentId": INCIDENT, "reportId": "flow-departure-report", "observedAt": "2026-11-27T20:05:00Z",
                 "departures": [{"caseId": case_id, "legId": LEG}]}
    departed = client.command("confirm-shelter-departure", departure)
    assert departed["entries"][0]["outcome"] == "Departed"
    assert client.command("confirm-shelter-departure", departure) == departed
    assert client.shelters()["shelter-a"]["commitments"]["occupiedPlaces"] == before["occupiedPlaces"]
    print("Default sample flow passed: alert, draw, full shelters, bridge, explicit exit, required care, pending transport, "
          "reservation, publication, closure withdrawal, replacement at another site, admission and departure.")


if __name__ == "__main__":
    exercise(sys.argv[1])
