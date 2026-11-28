"""SH-T05: admissions, departures, operator scope and two-leg transfers through the Gateway.

Runs on the sample sites plus the SH-W4 hold fixture. argv: Gateway URL, the generated exercise token file
(scripts/offline_demo.py writes it outside Git) and optionally --after-restart.
"""

from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import sys
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import ProxyHandler, Request, build_opener


ROOT = Path(__file__).resolve().parents[1]
BASE = sys.argv[1]
TOKENS = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
OPERATOR = {"shelter-a": "op-shelter-a", "shelter-b": "op-shelter-b"}
HTTP = build_opener(ProxyHandler({}))
INCIDENT = "incident-42"
LEG = "leg-shelter"
ADMIT = "/api/sheltering/commands/confirm-shelter-admission"
DEPART = "/api/sheltering/commands/confirm-shelter-departure"


def request(path, payload=None, account=None):
    """Commands act as the operator of their shelter, queries as the coordinator, unless an account is given."""
    data = json.dumps(payload).encode() if payload is not None else None
    headers = {"Content-Type": "application/json"} if data else {}
    account = account or (OPERATOR[payload["shelterId"]] if payload else "coordinator-42")
    if account != "anonymous":
        headers["Authorization"] = "Bearer " + TOKENS.get(account, account)
    try:
        response = HTTP.open(Request(BASE + path, data, headers), timeout=10)
    except HTTPError as error:
        response = error
    with response:
        body = response.read()
        return response.status, json.loads(body) if body else None


def check(path, payload=None, expected=200, account=None):
    status, result = request(path, payload, account)
    assert status == expected, (path, status, result)
    return result


def reception(shelter, at="2026-11-27T19:40:00Z"):
    query = urlencode({"shelterId": shelter, "incidentId": INCIDENT, "at": at})
    return check("/api/sheltering/queries/shelter-reception?" + query)["shelters"][0]


def admit(shelter, report, arrivals, observed="2026-11-27T19:32:00Z", command=None, version=None):
    return {"commandId": command or "cmd-" + report, "expectedVersion": version or reception(shelter)["version"],
            "shelterId": shelter, "incidentId": INCIDENT, "reportId": report, "observedAt": observed,
            "arrivals": arrivals}


def depart(shelter, report, cases, observed="2026-11-27T19:55:00Z", command=None, leg=LEG):
    return {"commandId": command or "cmd-" + report, "expectedVersion": reception(shelter)["version"],
            "shelterId": shelter, "incidentId": INCIDENT, "reportId": report, "observedAt": observed,
            "departures": [{"caseId": case, "legId": leg} for case in cases]}


def figures(shelter):
    counters = reception(shelter)["counters"]
    return {key: counters[key] for key in ("awaitingAdmission", "present", "admissionsConfirmed",
                                           "departuresConfirmed", "commitments", "available")}


def arrival(case, resources=None, leg=LEG):
    return {"caseId": case, "legId": leg, **({"resources": resources} if resources else {})}


ONE = {"places": 1, "accessiblePlaces": 0}

if "--after-restart" in sys.argv:
    a = reception("shelter-a")
    assert a["counters"]["admissionsConfirmed"] == 3 and a["counters"]["departuresConfirmed"] == 1, a["counters"]
    before = figures("shelter-a")
    replay = admit("shelter-a", "report-arrival-14", [arrival("case-14")], command="retry-after-restart", version=1)
    assert check(ADMIT, replay)["entries"][0]["outcome"] == "Admitted"
    assert figures("shelter-a") == before
    assert check(ADMIT, replay, 401, account="anonymous")["code"] == "AuthenticationRequired"
    assert check(ADMIT, replay, 403, account="op-shelter-b")["code"] == "ShelterNotAssigned"
    events = check("/api/sheltering/queries/shelter-reception-events")["events"]
    assert [event["type"] for event in events].count("ShelterAdmissionConfirmed.v1") == 4
    assert [event["type"] for event in events].count("ShelterDepartureConfirmed.v1") == 1
    print("Shelter reception restart: admissions, departures, report receipts and outbox survived.")
    sys.exit(0)

# Seed: shelter-a has 8 places (2 accessible); untracked 1 held and 2 occupied (1 accessible),
# plus the household holds for case-14 and case-15 (accessible). shelter-b holds a place for case-16.
a = reception("shelter-a")
assert a["counters"]["awaitingAdmission"] == {"people": 2, "places": 2, "accessiblePlaces": 1}
assert a["counters"]["untrackedHeld"] == {"places": 1, "accessiblePlaces": 0}
assert a["counters"]["untrackedOccupied"] == {"places": 2, "accessiblePlaces": 1}
assert a["counters"]["available"] == {"places": 3, "accessiblePlaces": 0}
start_version = a["version"]

# Stage B role boundary: authority comes from the token, never from the payload.
probe = admit("shelter-a", "report-probe", [arrival("case-14")])
assert check(ADMIT, probe, 401, account="anonymous")["code"] == "AuthenticationRequired"
assert check(ADMIT, probe, 401, account="not-a-token")["code"] == "AuthenticationRequired"
assert check(ADMIT, probe, 403, account="op-shelter-b")["code"] == "ShelterNotAssigned"
assert check(ADMIT, probe, 403, account="coordinator-42")["code"] == "RoleNotPermitted"
assert check(ADMIT, probe, 403, account="op-other-incident")["code"] == "IncidentNotAssigned"
assert check(DEPART, depart("shelter-a", "report-probe-leave", ["case-14"]), 403, account="op-shelter-b")["code"] == "ShelterNotAssigned"
own = check("/api/sheltering/queries/shelter-reception?incidentId=" + INCIDENT, account="op-shelter-a")["shelters"]
assert [row["shelterId"] for row in own] == ["shelter-a"]
assert check("/api/sheltering/queries/shelter-reception?shelterId=shelter-b", expected=403,
             account="op-shelter-a")["code"] == "ShelterNotAssigned"
assert check("/api/sheltering/queries/shelter-reception", expected=401, account="anonymous")["code"] == "AuthenticationRequired"
assert figures("shelter-a")["commitments"] == a["counters"]["commitments"] and reception("shelter-a")["version"] == start_version

# Partial arrival: only case-14 arrives. Only that hold becomes occupied; free places do not change.
first = admit("shelter-a", "report-arrival-14", [arrival("case-14")])
result = check(ADMIT, first)
assert [(entry["caseId"], entry["outcome"]) for entry in result["entries"]] == [("case-14", "Admitted")]
after = figures("shelter-a")
assert after["awaitingAdmission"] == {"people": 1, "places": 1, "accessiblePlaces": 1}
assert after["present"] == {"people": 1, "places": 1, "accessiblePlaces": 0}
assert after["commitments"] == {"heldPlaces": 2, "heldAccessiblePlaces": 1, "occupiedPlaces": 3,
                                "occupiedAccessiblePlaces": 1}
assert after["available"] == {"places": 3, "accessiblePlaces": 0}
assert [item["caseId"] for item in reception("shelter-a")["awaiting"]] == ["case-15"]

# Replays count once: same commandId, same report under another commandId and a stale version, another report.
assert check(ADMIT, first) == result
assert check(ADMIT, {**first, "commandId": "transport-retry-2", "expectedVersion": start_version}) == result
assert check(ADMIT, {**first, "commandId": "edited", "observedAt": "2026-11-27T19:33:00Z"}, 409)["code"] == "ReportConflict"
second = check(ADMIT, admit("shelter-a", "report-arrival-14-again", [arrival("case-14")]))
assert second["entries"][0]["outcome"] == "AlreadyAdmitted"
assert check(ADMIT, admit("shelter-b", "report-14-at-b", [arrival("case-14", ONE)]), 409)["code"] == "AdmittedElsewhere"
assert figures("shelter-a") == after

# A walk-in with a hold elsewhere: the hold at shelter-b is released in the same transaction.
b_before = reception("shelter-b")["counters"]
walk = check(ADMIT, admit("shelter-a", "report-walkin-16", [arrival("case-16", ONE)], observed="2026-11-27T19:36:00Z"))
assert walk["entries"][0]["outcome"] == "WalkInAdmitted"
assert walk["releasedHolds"] == [{"shelterId": "shelter-b", "reservationId": "reservation-case-16",
                                  "caseId": "case-16", "legId": LEG, "resources": ONE}]
b_after = reception("shelter-b")["counters"]
assert [item["caseId"] for item in reception("shelter-b")["awaiting"]] == ["case-14"]  # the transfer leg
assert b_after["available"]["places"] == b_before["available"]["places"] + 1
assert figures("shelter-a")["available"] == {"places": 2, "accessiblePlaces": 0}

# Walk-ins that cannot be admitted change nothing.
stable = figures("shelter-a")
no_access = admit("shelter-a", "report-walkin-17", [arrival("case-17", {"places": 1, "accessiblePlaces": 1})])
assert check(ADMIT, no_access, 409)["code"] == "NoAvailablePlaces"
assert check(ADMIT, admit("shelter-a", "report-walkin-18", [arrival("case-18")]), 422)["code"] == "WalkInResourcesRequired"
mixed = admit("shelter-a", "report-mixed", [arrival("case-15"), arrival("case-17", {"places": 1, "accessiblePlaces": 1})])
assert check(ADMIT, mixed, 409)["code"] == "NoAvailablePlaces"
assert figures("shelter-a") == stable

# The missed arrival window flags case-15 for follow-up without releasing the hold.
late = reception("shelter-a", at="2026-11-27T19:51:00Z")
assert late["counters"]["overdueArrivals"] == 1 and late["awaiting"][0]["overdue"]

# Two operators report case-15 at once: one admission, the other must reread the shelter.
with ThreadPoolExecutor(max_workers=2) as pool:
    version = reception("shelter-a")["version"]
    outcomes = list(pool.map(lambda index: request(ADMIT, admit("shelter-a", f"report-15-desk-{index}",
                                                                [arrival("case-15")], version=version)), (1, 2)))
assert sorted(status for status, _ in outcomes) == [200, 409], outcomes
assert figures("shelter-a")["present"] == {"people": 3, "places": 3, "accessiblePlaces": 1}

# Transfer as two legs: case-14 reaches shelter-b on leg-transfer while still counted at shelter-a.
a_before_transfer = figures("shelter-a")
moved = check(ADMIT, admit("shelter-b", "report-transfer-14", [arrival("case-14", leg="leg-transfer")],
                           observed="2026-11-27T19:50:00Z"))
origin = reception("shelter-a")["admissions"][0]
assert moved["entries"][0]["outcome"] == "Admitted"
assert moved["entries"][0]["transferFrom"] == {"shelterId": "shelter-a", "admissionId": origin["admissionId"],
                                               "legId": LEG}
assert figures("shelter-a") == a_before_transfer          # original occupancy stays until the actual departure
row = reception("shelter-a")
assert row["counters"]["transferredAwaitingDeparture"] == 1
assert origin["caseId"] == "case-14" and origin["onwardAdmission"]["shelterId"] == "shelter-b"
assert reception("shelter-b")["counters"]["present"]["people"] == 1
assert check(ADMIT, admit("shelter-a", "report-back-14", [arrival("case-14", ONE, leg="leg-transfer")]),
             409)["code"] == "AdmittedElsewhere"

# Departure frees exactly the admitted places once.
before_departure = figures("shelter-a")
assert check(DEPART, depart("shelter-a", "report-leave-14", ["case-14"], observed="2026-11-27T19:31:00Z"),
             422)["code"] == "DepartureBeforeAdmission"
leave = depart("shelter-a", "report-leave-14", ["case-14"])
left = check(DEPART, leave)
assert left["entries"][0]["outcome"] == "Departed"
freed = figures("shelter-a")
assert freed["available"]["places"] == before_departure["available"]["places"] + 1
assert freed["commitments"]["occupiedPlaces"] == before_departure["commitments"]["occupiedPlaces"] - 1
assert check(DEPART, {**leave, "commandId": "leave-retry"}) == left
assert check(DEPART, depart("shelter-a", "report-leave-14-again", ["case-14"]))["entries"][0]["outcome"] == "AlreadyDeparted"
assert check(DEPART, depart("shelter-b", "report-leave-b", ["case-16"]), 404)["code"] == "NotAdmitted"
assert figures("shelter-a") == freed
assert reception("shelter-a")["counters"]["transferredAwaitingDeparture"] == 0
assert reception("shelter-b")["counters"]["present"]["people"] == 1   # still at the new site
counters = reception("shelter-a")["counters"]
assert counters["admissionsConfirmed"] == 3 and counters["departuresConfirmed"] == 1

events = check("/api/sheltering/queries/shelter-reception-events?shelterId=shelter-a")["events"]
assert [event["type"] for event in events] == ["ShelterAdmissionConfirmed.v1"] * 3 + ["ShelterDepartureConfirmed.v1"]
assert {event["payload"]["caseId"] for event in events[:3]} == {"case-14", "case-15", "case-16"}
onward = check("/api/sheltering/queries/shelter-reception-events?shelterId=shelter-b")["events"]
assert [(event["payload"]["caseId"], event["payload"]["transferFromShelterId"]) for event in onward] == [("case-14", "shelter-a")]
print("Shelter reception: partial arrival, duplicate reports, walk-in reconciliation, rejected walk-ins, "
      "concurrent report, operator scope, two-leg transfer and departure.")
