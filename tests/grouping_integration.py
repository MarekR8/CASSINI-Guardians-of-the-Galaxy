"""Replay SH-A02 and SH-A03 through Management behind the existing Gateway."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import sys
from tempfile import TemporaryDirectory
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import Request


sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from offline_demo import ARTIFACTS, LOCAL_HTTP, ROOT, build, start_services


def example(name):
    return json.loads((ROOT / "contracts/http/grouping" / name).read_text(encoding="utf-8"))


def call(url, path, body=None, expected_status=200, code=None, token=None, service="management"):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    headers = {"Content-Type": "application/json"}
    if token is not None:
        headers["Authorization"] = "Bearer " + token
    req = Request(f"{url}/api/{service}/{path}", data, headers)
    try:
        with LOCAL_HTTP.open(req, timeout=10) as response:
            status, payload = response.status, json.load(response)
    except HTTPError as error:
        status, payload = error.code, json.load(error)
        if status == expected_status == 401:
            assert error.headers.get("WWW-Authenticate") == "Bearer", (path, error.headers)
    assert status == expected_status, (path, status, payload)
    if code is not None:
        assert payload["code"] == code, payload
    return payload


def replay(url):
    household = example("household-split.json")
    care = example("care-handover.json")
    register = household["register"]
    approve = household["approve"]
    scope = {key: register[key] for key in ("incidentId", "groupId")}

    def request(path, body=None, expected_status=200, code=None):
        return call(url, path, body, expected_status, code)

    def query(operation, **params):
        args = urlencode({"incidentId": scope["incidentId"], **params})
        return f"queries/{operation}/{scope['groupId']}?{args}"

    def proposal(evidence):
        return request(query("travel-party-proposals", legId="leg-shelter", evidenceId=evidence))

    def verify_plan(plan, expected):
        assert plan["planRevision"] == expected["planRevision"], plan
        assert plan["status"] == expected["status"], plan
        assert plan["proposal"]["reason"] == expected["reason"], plan
        assert {party["routeId"]: party["destinationId"] for party in plan["proposal"]["parties"]} == expected["routes"], plan

    assert request("commands/register-evacuation-group", register)["groupRevision"] == 1
    request(query("evacuation-groups", incidentId="another-incident"), expected_status=404, code="GroupNotFound")
    assert proposal("household-shared")["reason"] == "IndependentDeparturesSharedShelter"
    plan_path = query("approved-travel-plans")
    request(plan_path, expected_status=404, code="PlanNotFound")
    approve_path = "commands/approve-travel-plan"
    original = request(approve_path, approve)
    verify_plan(original, household["expected"])
    assert request(approve_path, approve) == original
    request(approve_path, {**approve, "evidenceId": "household-separated"}, 409, "CommandIdConflict")

    split = proposal("household-separated")
    assert split["reason"] == "SharedShelterNotFeasible"
    assert {party["destinationId"] for party in split["parties"]} == {"shelter-a", "shelter-b"}
    review = {**scope, "commandId": "review-1", "planRevision": 1,
              "reason": "ApproachClosed", "evidenceRef": "household-separated"}
    review_path = "commands/flag-travel-plan-for-review"
    assert request(review_path, review)["status"] == "ReviewRequired"
    assert proposal("household-shared")["reason"] == "RouteEvidenceInvalidated"
    request(approve_path, {**approve, "commandId": "outdated-routes", "expectedPlanRevision": 1},
            422, "RouteEvidenceInvalidated")
    replacement = {**approve, "commandId": "group-approve-2", "expectedPlanRevision": 1,
                   "evidenceId": "household-separated"}
    assert request(approve_path, replacement)["planRevision"] == 2
    request(review_path, {**review, "commandId": "late-review-1"}, 409, "PlanRevisionConflict")
    assert request(plan_path)["status"] == "Approved"

    update = care["updateComposition"]
    assert request("commands/update-group-composition", update)["groupRevision"] == 2
    assert request(plan_path)["reviewReason"] == "CompositionChanged"
    request("commands/update-group-composition",
            {**update, "commandId": "stale-update"}, 409, "GroupRevisionConflict")
    request("commands/update-group-composition",
            {**update, "commandId": "drop-care", "expectedGroupRevision": 2,
             "relationships": update["relationships"][:1]}, 422, "RequiredCareMustBePreserved")
    assert proposal("care-handover")["reason"] == "RequiredCareSplit"
    request(approve_path, {**care["approve"], "commandId": "blocked-care", "expectedGroupRevision": 2},
            422, "RequiredCareSplit")

    transfer = care["handover"]
    handover_path = "commands/record-care-handover"
    request(handover_path, {**transfer, "commandId": "unauthorized-care",
                           "handover": {**transfer["handover"], "role": "resident"}}, 422, "HandoverNotConfirmed")
    assert request(handover_path, transfer)["groupRevision"] == 3
    assert proposal("care-handover")["requiredCare"][0]["relatedCaseId"] == "case-16"
    verify_plan(request(approve_path, care["approve"]), care["expected"])
    assert request(plan_path)["proposal"]["parties"][0]["caseIds"] == ["case-15", "case-16"]

    request("commands/progress", {"commandId": "group-position-1", "caseId": "case-14", "step": "position",
                                  "expectedVersion": 1, "node": "east-junction",
                                  "observedAt": "2026-11-27T19:20:00Z"})
    assert proposal("care-handover")["reason"] == "StaleCaseOrRouteEvidence"
    assert request(plan_path)["reviewReason"] == "CaseVersionChanged"
    return plan_path


def scoped_replay(port, state):
    state.mkdir()
    users = {"coordinator": ("incident-42", "coordinator"), "planner": ("incident-42", "reader"),
             "field": ("incident-42", "field-operator"), "other": ("incident-other", "coordinator")}
    tokens = {user: secrets.token_urlsafe(32) for user in users}
    operators = state / "operators.json"
    operators.write_text(json.dumps({"operators": [
        {"operatorId": user, "incidentId": incident, "role": role,
         "tokenSha256": hashlib.sha256(tokens[user].encode()).hexdigest()}
        for user, (incident, role) in users.items()
    ]}), encoding="utf-8")
    os.environ["Grouping__OperatorsFile"] = str(operators.resolve())
    os.environ["Grouping__ReadToken"] = tokens["planner"]
    register = example("household-split.json")["register"]
    care = example("care-handover.json")
    group_path = "queries/evacuation-groups/group-household?incidentId=incident-42"
    register_path = "commands/register-evacuation-group"
    with start_services(port, state, sheltering=True) as url:
        call(url, group_path, expected_status=401, code="AuthenticationRequired")
        call(url, register_path, register, 401, "AuthenticationRequired", "invalid-token")
        call(url, register_path, register, 403, "IncidentNotAssigned", tokens["other"])
        call(url, register_path, register, 403, "RoleNotPermitted", tokens["planner"])
        call(url, register_path, register, token=tokens["coordinator"])
        call(url, register_path, {**register, "incidentId": "incident-other"}, 404, "IncidentNotFound", tokens["other"])
        call(url, group_path, expected_status=403, code="IncidentNotAssigned", token=tokens["other"])
        assert call(url, group_path, token=tokens["planner"])["groupRevision"] == 1
        call(url, "commands/update-group-composition", care["updateComposition"], token=tokens["coordinator"])
        handover = {**care["handover"], "handover": {**care["handover"]["handover"], "role": "coordinator",
                                                    "authorizedBy": "forged-actor"}}
        call(url, "commands/record-care-handover", handover, 403, "RoleNotPermitted", tokens["planner"])
        call(url, "commands/record-care-handover", handover, token=tokens["field"])
        public_handover, = call(url, group_path, token=tokens["planner"])["handovers"]
        assert "authorizedBy" not in public_handover and "role" not in public_handover and public_handover["evidenceRef"]
        routing_query = urlencode({"incidentId": "incident-42", "legId": "leg-shelter", "groupId": "group-household",
                                   "groupRevision": 3, "caseIds": "case-15", "at": "2026-11-27T19:20:00Z",
                                   "node": "pickup", "mode": "driving", "destinationKind": "exit", "destinationId": "exit-north"})
        call(url, "queries/destination-route?" + routing_query, expected_status=422, code="RequiredCareSplit", service="routing")
        plan = call(url, "commands/approve-travel-plan", {**care["approve"], "expectedPlanRevision": 0}, token=tokens["coordinator"])
        assert plan["planRevision"] == 1
        assignment = call(url, "commands/open-shelter-assignment", {
            "commandId": "scoped-open", "incidentId": "incident-42", "caseId": "case-14", "legId": "leg-shelter",
            "needs": {"places": 1, "accessiblePlaces": 0},
            "groupPlanRef": {"incidentId": "incident-42", "groupId": "group-household", "planRevision": 1},
        }, service="sheltering")
        assert assignment["assignment"]["status"] == "Pending"
    stored = json.loads((state / "management.json").read_text(encoding="utf-8"))
    saved_handover, = stored["groups"]["group-household"]["handovers"]
    assert (saved_handover["authorizedBy"], saved_handover["role"]) == ("field", "field-operator")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=5180)
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    if not args.no_build:
        build()
    os.environ["Grouping__EvidenceFile"] = str(ROOT / "data/demo/sheltering/sample-grouping.json")
    ARTIFACTS.mkdir(exist_ok=True)
    with TemporaryDirectory(prefix="grouping-integration-", dir=ARTIFACTS) as directory:
        state = Path(directory)
        with start_services(args.port, state) as url:
            plan_path = replay(url)
        with start_services(args.port, state) as url:
            with LOCAL_HTTP.open(url + "/api/management/" + plan_path, timeout=10) as response:
                plan = json.load(response)
            assert (plan["planRevision"], plan["reviewReason"]) == (3, "CaseVersionChanged"), plan
        scoped_replay(args.port, state / "scoped")
    print("Grouping HTTP integration passed (SH-A02, SH-A03, revisions, replay, restart and incident scope).")


if __name__ == "__main__":
    main()
