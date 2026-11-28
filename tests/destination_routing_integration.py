"""Verify named destinations and SH-A01 through Gateway, Routing and the site owners."""

import argparse
import json
import os
from pathlib import Path
from statistics import median
import sys
from tempfile import TemporaryDirectory
from time import perf_counter
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from offline_demo import ARTIFACTS, LOCAL_HTTP, ROOT, build, start_services


def read(path):
    return json.loads((ROOT / path).read_text(encoding="utf-8"))


def exercise(base):
    example = read("contracts/http/routing/destination-route.example.json")
    sites = read("data/demo/sheltering/sample-sites.json")
    at = sites["exerciseAt"]
    travel = {"incidentId": "incident-42", "legId": "leg-exit", "node": "walk-start", "mode": "walking",
              "at": at, "clearBy": "2026-11-27T19:58:00Z"}

    def request(path, body=None, expected=200):
        data = json.dumps(body).encode("utf-8") if body is not None else None
        req = Request(base + "/api/" + path, data, {"Content-Type": "application/json"})
        try:
            response = LOCAL_HTTP.open(req, timeout=10)
        except HTTPError as error:
            response = error
        with response:
            payload = json.load(response)
            assert response.status == expected, (path, response.status, payload)
            return payload

    def query(operation, args, expected=200):
        return request(f"routing/queries/{operation}?" + urlencode(args), expected=expected)

    def destination(kind, identifier, **args):
        return query("destination-route", {**travel, "destinationKind": kind, "destinationId": identifier, **args})

    def first(**args):
        return query("first-leg", {**travel, **args})

    def configure(shelter, places, accessible, version, command_id):
        payload = {key: value for key, value in shelter.items() if key not in ("version", "commitments")}
        payload.update(commandId=command_id, expectedVersion=version,
                       resources={**shelter["resources"], "places": places, "accessiblePlaces": accessible})
        return request("sheltering/commands/configure-shelter", payload)

    def closure(segment, revision):
        return request("situation/commands/closure",
                       {"commandId": f"close-{revision}", "expectedRevision": revision, "segmentId": segment, "closed": True})

    recording = query("destination-route", example["query"])
    assert all(recording[key] == value for key, value in example["eligible"].items()), recording
    assert recording["evidence"]["mode"] == "synthetic"
    expected = {**example["query"], "expectedEvidenceId": recording["evidence"]["evidenceId"]}
    assert query("destination-route", expected) == recording
    assert query("destination-route", {**example["query"], "recordedRouteId": "unknown"})["reason"] == "RecordedRouteUnavailable"

    east = destination("shelter", "shelter-a")
    assert east["status"] == "Eligible" and east["evidence"]["siteVersion"] == 1
    assert destination("shelter", "missing")["reason"] == "DestinationNotFound"
    assert destination("shelter", "shelter-a", stepFree="true")["reason"] == "RouteAccessibilityUnverified"
    north = destination("assemblyPoint", "assembly-north", stepFree="true")
    assert north["status"] == "Eligible" and north["clearsHazardAt"] == "2026-11-27T19:40:00+00:00"
    for kind, identifier in (("exit", "exit-north"), ("assemblyPoint", "assembly-north"), ("shelter", "shelter-b")):
        tight = destination(kind, identifier, clearBy="2026-11-27T19:39:59Z")
        assert tight["reason"] == "ClearanceDeadlineMissed", tight

    direct = first(caseIds="case-14", reservationId="reservation-demo-east")
    assert (direct["status"], direct["shelterStatus"]) == ("DirectShelter", "Reserved"), direct
    assert direct["fixtureMode"] == "synthetic"
    assert direct["route"]["destination"] == {"kind": "shelter", "id": "shelter-a"}
    assert first(caseIds="case-14", reservationId="unverified")["status"] == "ShelterPending"
    assert first(caseIds="case-14", reservationId="reservation-demo-east", legId="leg-shelter")["status"] == "ShelterPending"

    request("management/commands/register-evacuation-group", {
        "commandId": "assisted-party", "incidentId": "incident-42", "groupId": "care-party",
        "caseIds": ["case-15", "case-16"], "evidenceRef": "synthetic-group-report",
        "relationships": [{"kind": "required-care", "caseId": "case-15", "relatedCaseId": "case-16",
                           "evidenceRef": "synthetic-care"}],
    })
    assisted = {**travel, "node": "pickup", "mode": "driving", "groupId": "care-party", "groupRevision": 1}
    split = query("destination-route", {**assisted, "caseIds": "case-15", "destinationKind": "exit",
                                       "destinationId": "exit-north"}, expected=422)
    assert split["code"] == "RequiredCareSplit"
    assisted["caseIds"] = "case-16,case-15"
    assert query("first-leg", assisted)["reasons"][0]["code"] == "TransportEvidenceMissing"
    assisted["transportId"] = "transport-demo-north"
    supported = query("first-leg", {**assisted, "stepFree": "true"})
    assert supported["status"] == "ShelterPending", supported
    assert supported["route"]["departsAt"] == "2026-11-27T19:22:00+00:00"
    assert supported["route"]["evidence"]["transportId"] == assisted["transportId"]
    assert supported["fixtureMode"] == "synthetic"
    late_arrival = query("first-leg", {**assisted, "stepFree": "true", "at": "2026-11-27T19:24:30Z"})
    assert late_arrival["status"] == "EscalationRequired", late_arrival
    assert any(reason["code"] == "TransportWindowUnavailable" for reason in late_arrival["reasons"])
    assert query("first-leg", {**assisted, "at": "2026-11-27T19:26:00Z"})["status"] == "EscalationRequired"

    timings = {name: [] for name in ("graph", "recorded")}
    for _ in range(10):
        for name, args in (("recorded", example["query"]),
                           ("graph", {key: value for key, value in example["query"].items() if key != "recordedRouteId"})):
            start = perf_counter()
            assert query("destination-route", args)["status"] == "Eligible"
            timings[name].append(1000 * (perf_counter() - start))

    a, b = sites["shelters"]
    configure(a, 3, 1, 1, "fill-a")
    configure(b, 0, 0, 1, "fill-b")
    assert destination("shelter", "shelter-a")["availabilityReason"] == "NoAvailablePlaces"
    assert destination("shelter", "shelter-b")["availabilityReason"] == "NoAvailablePlaces"
    assert destination("shelter", "shelter-a", expectedEvidenceId=east["evidence"]["evidenceId"])["reason"] == "RouteEvidenceChanged"
    pending = first(caseIds="case-14", stepFree="true", reservationId="reservation-demo-east")
    assert (pending["status"], pending["shelterStatus"]) == ("ShelterPending", "Pending"), pending
    assert pending["route"]["destination"] == {"kind": "assemblyPoint", "id": "assembly-north"}
    assert pending["reservationId"] is None

    request("management/commands/progress", {"commandId": "arrived-north", "caseId": "case-14", "step": "position",
            "expectedVersion": 1, "node": "exit-north", "observedAt": "2026-11-27T19:40:00Z"})
    configure(a, 4, 1, 2, "open-a-place")
    later = {"incidentId": "incident-42", "legId": "leg-shelter", "mode": "walking", "caseIds": "case-14",
             "at": "2026-11-27T19:41:00Z"}
    earlier = query("first-leg", {**later, "at": at}, expected=422)
    assert earlier["code"] == "PositionNotYetObserved"
    assert query("first-leg", later)["status"] == "AlreadyOutsideHazard"
    subsequent = query("destination-route", {**later, "mode": "driving", "destinationKind": "shelter", "destinationId": "shelter-a"})
    assert subsequent["status"] == "Eligible" and subsequent["segments"] == ["outside-connector"]
    assert subsequent["travel"]["origin"] == "exit-north" and subsequent["travel"]["cases"][0]["version"] == 2
    assert query("destination-route", {**later, "destinationKind": "shelter", "destinationId": "shelter-a"})["reason"] == "NoEligibleRoute"

    assert closure("bridge-2", 0)["revision"] == 1
    blocked = query("destination-route", expected)
    assert all(blocked[key] == value for key, value in example["afterBridgeClosure"].items()), blocked
    stale = destination("assemblyPoint", "assembly-north", at="2026-11-27T19:30:00Z")
    assert stale["reason"] == "StaleSituationEvidence", stale
    safe_exit = first(at="2026-11-27T19:30:00Z")
    assert safe_exit["route"]["destination"] == {"kind": "exit", "id": "exit-north"}
    assert closure("north-link", 1)["revision"] == 2
    unresolved = first(at="2026-11-27T19:33:00Z")
    assert unresolved["status"] == "EscalationRequired" and unresolved["route"] is None, unresolved
    assert unresolved["reasons"][-1]["code"] == "NoEligibleFirstLeg"
    print("SH-W3 destination routing passed. Local median HTTP latency (10 each) "
          + ", ".join(f"{name} {median(samples):.1f} ms" for name, samples in timings.items()) + ".")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=5580)
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    if not args.no_build:
        build()
    os.environ["Routing__DestinationEvidenceFile"] = str(ROOT / "data/demo/sheltering/sample-destination-evidence.json")
    os.environ["Grouping__EvidenceFile"] = str(ROOT / "data/demo/sheltering/sample-grouping.json")
    ARTIFACTS.mkdir(exist_ok=True)
    with TemporaryDirectory(prefix="destination-routing-", dir=ARTIFACTS) as directory:
        with start_services(args.port, Path(directory), sheltering=True) as base:
            exercise(base)


if __name__ == "__main__":
    main()
