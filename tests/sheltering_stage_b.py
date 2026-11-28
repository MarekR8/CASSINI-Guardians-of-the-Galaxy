"""Exercise authenticated catalog writes and recover committed state and pending events."""

import argparse
from base64 import urlsafe_b64encode
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory
from threading import Thread
from time import time
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import Request

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
from offline_demo import LOCAL_HTTP, start_services

POINT = "/api/situation/commands/set-assembly-point"
SHELTER = "/api/sheltering/commands/configure-shelter"
AUDIENCE = "cassini-site-api-test"
FIXTURE = json.loads((ROOT / "data/demo/sheltering/sample-sites.json").read_text(encoding="utf-8"))


def b64(data):
    return urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


@contextmanager
def identity_provider(key):
    subprocess.run(["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:2048", "-out", str(key)],
                   check=True, capture_output=True)
    modulus = subprocess.check_output(
        ["openssl", "rsa", "-in", str(key), "-noout", "-modulus"], text=True).strip().split("=", 1)[1]

    class IdentityProvider(BaseHTTPRequestHandler):
        def do_GET(self):
            documents = {
                "/.well-known/openid-configuration": {"issuer": issuer, "jwks_uri": issuer + "/keys"},
                "/keys": {"keys": [{"kty": "RSA", "use": "sig", "alg": "RS256", "kid": "exercise",
                                    "n": b64(bytes.fromhex(modulus)), "e": "AQAB"}]},
            }
            if self.path not in documents:
                self.send_error(404)
                return
            payload = json.dumps(documents[self.path]).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def log_message(self, *_args):
            pass

    with ThreadingHTTPServer(("127.0.0.1", 0), IdentityProvider) as server:
        issuer = f"http://127.0.0.1:{server.server_port}"

        def issue(subject, role, *, incident="incident-42", shelter=None, header_type="at+jwt", **overrides):
            claims = {"iss": issuer, "aud": AUDIENCE, "sub": subject, "exp": int(time()) + 180,
                      "site_role": role, "incident_scope": [incident], **overrides}
            if shelter:
                claims["shelter_scope"] = [shelter]
            header = {"alg": "RS256", "kid": "exercise", "typ": header_type}
            content = ".".join(b64(json.dumps(part).encode()) for part in (header, claims))
            signature = subprocess.run(["openssl", "dgst", "-sha256", "-sign", str(key)], input=content.encode(),
                                       check=True, capture_output=True).stdout
            return content + "." + b64(signature)

        thread = Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield issuer, issue
        finally:
            server.shutdown()
            thread.join()


def write(base, path, command, token=None, status=200, code=None):
    headers = {"Content-Type": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    try:
        response = LOCAL_HTTP.open(Request(base + path, data=json.dumps(command).encode(), headers=headers), timeout=10)
    except HTTPError as error:
        response = error
    with response:
        body = json.load(response)
        assert response.status == status, (path, status, response.status, body)
        if status == 401:
            assert response.headers["WWW-Authenticate"] == "Bearer"
        if code:
            assert body["code"] == code, body
        return body


def command(site, command_id, **updates):
    fields = {key: value for key, value in site.items() if key not in ("version", "commitments")}
    return {**fields, "commandId": command_id, "expectedVersion": site["version"], **updates}


def verify_file(path, owner, expected):
    state = json.loads(path.read_text(encoding="utf-8"))
    assert state["revision"] == len(expected) == len(state["audit"]) == len(state["outbox"]) == len(state["receipts"])
    identifiers = set()
    for (command_id, subject, event_type, route, payload), audit, entry in zip(expected, state["audit"], state["outbox"]):
        event = entry["event"]
        assert entry["routingKey"] == route and entry["publishedAt"] is None
        assert event["type"] == event_type and event["producer"] == owner
        assert event["subjectId"] == audit["subjectId"] and event["subjectVersion"] == audit["subjectVersion"]
        assert event["occurredAt"] == audit["at"] and event["causationId"] == audit["commandId"] == command_id
        assert event["correlationId"] == command_id and audit["actor"]["subject"] == subject
        assert audit["before"]["version"] + 1 == audit["after"]["version"]
        assert event["payload"]["incidentId"] == "incident-42"
        assert all(event["payload"][key] == value for key, value in payload.items())
        assert "actor" not in event and "commitments" not in event["payload"]
        identifiers.add(event["eventId"])
    assert len(identifiers) == len(expected)
    return state


def exercise(port, data, issuer, issue, postgres):
    coordinator = issue("coordinator-a", "coordinator")
    operator = issue("operator-a", "shelter-operator", shelter="shelter-a")
    point, = FIXTURE["assemblyPoints"]
    a, b = FIXTURE["shelters"]
    close_point = command(point, "close-point-b", status="closed")
    resources = {**a["resources"], "places": 7}
    update = command(a, "resources-a-b", resources=resources)
    close_a = command(a, "close-shelter-b", expectedVersion=2, resources=resources, status="closed")

    config = {"SITECATALOG__ACCESSMODE": "oidc", "SiteCatalog__Authority": issuer,
              "SiteCatalog__Audience": AUDIENCE, "SiteCatalog__AllowInsecureLoopbackMetadata": "true",
              "Sheltering__IncidentId": "incident-42"}
    with patch.dict(os.environ, config):
        with start_services(port, data, sheltering=True) as base:
            write(base, POINT, close_point, status=401)
            write(base, SHELTER, update, status=401)
            for invalid in [
                issue("bad-aud", "coordinator", aud="another-api"),
                issue("expired", "coordinator", exp=int(time()) - 60),
                issue("id-token", "coordinator", header_type="JWT"),
                coordinator + "tampered",
            ]:
                write(base, POINT, close_point, invalid, 401)
            write(base, POINT, close_point, operator, 403, "SiteScopeForbidden")
            other_incident = issue("other", "coordinator", incident="incident-99")
            write(base, POINT, close_point, other_incident, 403)
            write(base, SHELTER, update, other_incident, 403)
            write(base, SHELTER, command(b, "wrong-site"), operator, 403)
            write(base, SHELTER, {**update, "status": "closed"}, operator, 403)
            write(base, SHELTER, {**update, "name": "Other name"}, operator, 403)
            write(base, SHELTER, {**update, "evidence": {**a["evidence"], "source": "replacement"}}, operator, 403)
            write(base, SHELTER, {**update, "expectedVersion": 0, "shelterId": "shelter-new"},
                  issue("new-grant", "shelter-operator", shelter="shelter-new"), 403)

            point_result = write(base, POINT, close_point, coordinator)
            assert point_result["item"]["status"] == "closed"
            updated = write(base, SHELTER, update, operator)
            assert updated["item"]["resources"] == resources and updated["item"]["commitments"] == a["commitments"]
            assert write(base, SHELTER, update, operator) == updated
            write(base, SHELTER, update, coordinator, 403)
            close_result = write(base, SHELTER, close_a, coordinator)
            assert close_result["item"]["status"] == "closed"
            write(base, SHELTER, {**update, "commandId": "stale-version"}, operator, 409, "VersionConflict")

        point_path = data / "assembly-points.json"
        shelter_path = data / "shelters.json"
        if not postgres:
            point_state = verify_file(point_path, "situation-data", [
                ("close-point-b", "coordinator-a", "AssemblyPointChanged.v1",
                 "situation.assembly-point-changed.v1", {"pointId": "assembly-north", "status": "closed"}),
            ])
            shelter_state = verify_file(shelter_path, "sheltering", [
                ("resources-a-b", "operator-a", "ShelterAvailabilityChanged.v1",
                 "sheltering.shelter-availability-changed.v1", {"shelterId": "shelter-a", "affectedResources": ["places"]}),
                ("close-shelter-b", "coordinator-a", "ShelterAvailabilityChanged.v1",
                 "sheltering.shelter-availability-changed.v1", {"shelterId": "shelter-a", "status": "closed"}),
            ])

        with start_services(port, data, sheltering=True) as base:
            assert write(base, POINT, close_point, coordinator) == point_result
            assert write(base, SHELTER, update, operator) == updated
            assert write(base, SHELTER, close_a, coordinator) == close_result
        if not postgres:
            assert json.loads(point_path.read_text(encoding="utf-8")) == point_state
            assert json.loads(shelter_path.read_text(encoding="utf-8")) == shelter_state


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=16940)
    parser.add_argument("--postgres", action="store_true", help="Use fresh, migrated databases for the two owners.")
    args = parser.parse_args()
    assembly_db = bool(os.environ.get("ConnectionStrings__AssemblyPointCatalog"))
    shelter_dbs = sum(bool(os.environ.get(key)) for key in (
        "ConnectionStrings__ShelterCatalog", "ConnectionStrings__Sheltering"))
    if (assembly_db, shelter_dbs) != (args.postgres, int(args.postgres)):
        parser.error("For --postgres set the assembly point connection and exactly one Sheltering connection.")
    artifacts = ROOT / "artifacts"
    artifacts.mkdir(exist_ok=True)
    with TemporaryDirectory(dir=artifacts) as directory:
        data = Path(directory)
        with identity_provider(data / "test-private.pem") as (issuer, issue):
            exercise(args.port, data, issuer, issue, args.postgres)
    print("SH-T01 Stage B checks passed: authenticated scopes, resource-only changes, and restart recovery.")


if __name__ == "__main__":
    main()
