"""Verify Dispatch against recorded routes and an ORS-compatible local HTTP stub."""

from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from hashlib import sha256
from pathlib import Path
from subprocess import run
from tempfile import TemporaryDirectory
from threading import Thread
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import Request
import argparse
import json
import os
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
from offline_demo import LOCAL_HTTP, start_services

FIXTURES = ROOT / "data/demo/ors/recorded"


def api(base, path, body=None, expected=200):
    request = Request(base + "/api/" + path,
                      data=None if body is None else json.dumps(body).encode(),
                      headers={"Content-Type": "application/json"})
    try:
        result = LOCAL_HTTP.open(request, timeout=10)
    except HTTPError as error:
        result = error
    with result:
        payload = json.load(result)
        assert result.status == expected, (path, result.status, payload)
        return payload


def book(base, **extra):
    return api(base, "dispatch/commands/assign", {
        "commandId": "book", "requestId": "request-15", "unitId": "unit-3",
        "expectedVersion": 1, **extra,
    })


def close_bridge(base):
    return api(base, "situation/commands/closure", {
        "commandId": "close-bridge", "closed": True, "segmentId": "bridge-2", "expectedRevision": 0,
    })


@contextmanager
def demo(port, scenario="sample", **config):
    folder = ROOT / "artifacts"
    folder.mkdir(exist_ok=True)
    with TemporaryDirectory(dir=folder) as data, patch.dict(os.environ, config):
        with start_services(port, Path(data), scenario) as base:
            yield base


class OrsStub(BaseHTTPRequestHandler):
    cross_closed_bridge = False

    def do_POST(self):
        assert self.path == "/openrouteservice/v2/directions/driving-car/geojson", self.path
        assert self.headers["Authorization"] == "test-ors-key"
        body = self.rfile.read(int(self.headers["Content-Length"]))
        data = json.loads(body)
        key = sha256(b"driving-car\n" + body).hexdigest()
        response = json.loads((FIXTURES / f"{key}.json").read_text(encoding="utf-8"))["response"]
        if self.cross_closed_bridge and data.get("options"):
            response["features"][0]["geometry"]["coordinates"] = data["coordinates"]
        raw = json.dumps(response).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/geo+json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def log_message(self, format, *args):
        pass


def check_replay(port, scenario, directory=None, mode="synthetic", expected_exit=None):
    config = {"Integrations__Ors__Mode": "mock"}
    if directory is not None:
        config["Integrations__Ors__FixtureDirectory"] = str(directory)
    with demo(port, scenario, **config) as base:
        view = api(base, "dispatch/queries/assignments")
        assert view["units"][0]["positionEvidence"]["mode"] == "synthetic"
        assignment = book(base)
        assert assignment == book(base)
        assert assignment["route"]["provenance"]["mode"] == mode
        assert len(assignment["route"]["approachGeometry"]) >= 2
        if expected_exit is not None:
            assert assignment["route"]["exit"] == expected_exit
        close_bridge(base)
        status = api(base, "dispatch/queries/assignments")["assignments"][0]["routeStatus"]
        if mode == "synthetic":
            assert status["state"] == "Blocked" and "bridge-2" in status["segments"], status
        else:
            assert status["revision"] == 1 and status["state"] in ("Valid", "Blocked"), status
        updated = api(base, "dispatch/commands/replan", {
            "commandId": "replan", "assignmentId": assignment["assignmentId"],
            "expectedVersion": assignment["version"],
        })
        assert updated["route"]["exit"] == "exit-north"
        assert len(updated["route"]["outboundGeometry"]) >= 2
        assert updated["route"]["provenance"]["mode"] == mode


def check_dispatch_review(port):
    with demo(port, Integrations__Ors__Mode="graph") as base:
        units = api(base, "dispatch/queries/assignments")["units"]
        assert {unit["unitId"]: unit["positionVerified"] for unit in units} == {"unit-3": True, "unit-4": False}
        assignment = book(base, unitId="unit-4", confirmedStartNode="exit-east")
        assert assignment["unitId"] == "unit-4"
        assert [decision["kind"] for decision in assignment["history"]] == ["PositionConfirmed", "Assigned"]
        assert assignment == book(base, unitId="unit-4", confirmedStartNode="exit-east")

        def closure(revision, segment, value=True):
            return api(base, "situation/commands/closure", {
                "commandId": "closure-" + str(revision), "closed": value,
                "segmentId": segment, "expectedRevision": revision,
            })

        def status():
            return api(base, "dispatch/queries/assignments")["assignments"][0]["routeStatus"]

        closure(0, "walk-to-pickup")
        assert status()["state"] == "Valid" and status()["revision"] == 1
        closure(1, "bridge-2")
        blocked = status()
        assert blocked["state"] == "Blocked" and blocked["segments"] == ["bridge-2"] and blocked["observedAt"]
        closure(2, "bridge-2", False)
        assert status()["state"] == "ReviewRequired"
        replanned = api(base, "dispatch/commands/replan", {
            "commandId": "replan", "assignmentId": assignment["assignmentId"],
            "expectedVersion": assignment["version"],
        })
        assert [decision["kind"] for decision in replanned["history"]][-2:] == ["RouteWithdrawn", "Replanned"]
        assert replanned["history"][-2]["reason"] == "RoadChanged"
        assert status()["state"] == "Valid"


def check_capture(port, ors_url):
    with TemporaryDirectory() as directory:
        output = Path(directory) / "recordings"
        secret_file = Path(directory) / "Microsoft/UserSecrets/Cassini.Routing/secrets.json"
        secret_file.parent.mkdir(parents=True)
        secret_file.write_text(json.dumps({"Integrations:Ors:ApiKey": "test-ors-key"}), encoding="utf-8")
        env = dict(os.environ, Integrations__Ors__BaseUrl=ors_url, Integrations__Ors__ApiKey="test-ors-key",
                   HTTP_PROXY="", HTTPS_PROXY="", ALL_PROXY="")
        secret_env = dict(env, APPDATA=directory)
        del secret_env["Integrations__Ors__ApiKey"]
        command = [sys.executable, str(ROOT / "scripts/record_ors_routes.py"), str(output)]
        run(command, env=secret_env, check=True)
        expected = json.loads((FIXTURES.parent / "index.json").read_text(encoding="utf-8"))
        files = {file.stem: file.read_bytes() for file in output.glob("*.json")}
        assert files.keys() == expected.keys()
        assert all(json.loads(file)["mode"] == "recorded" for file in files.values())
        run(command, env=env, check=True)
        assert files == {file.stem: file.read_bytes() for file in output.glob("*.json")}
        run([sys.executable, str(Path(__file__).resolve()), "--port", str(port), "--recordings", str(output)],
            check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=16880)
    parser.add_argument("--recordings", type=Path, help="Run the booking and replan checks with these ORS recordings")
    args = parser.parse_args()
    if args.recordings is not None:
        check_replay(args.port, "sample", args.recordings.resolve(), "recorded")
        check_replay(args.port, "warsaw", args.recordings.resolve(), "recorded")
        print("Captured ORS routes passed the booking and replan checks for both cities.")
        return
    check_replay(args.port, "sample", expected_exit="exit-east")
    check_replay(args.port, "warsaw", expected_exit="exit-north")
    check_dispatch_review(args.port)

    with TemporaryDirectory() as empty:
        with demo(args.port, Integrations__Ors__Mode="mock", Integrations__Ors__FixtureDirectory=empty) as base:
            missing = api(base, "dispatch/commands/assign", {
                "commandId": "missing", "requestId": "request-15", "unitId": "unit-3", "expectedVersion": 1,
            }, expected=503)
            assert missing["code"] == "FixtureMissing"
            assert not api(base, "dispatch/queries/assignments")["assignments"]

    with ThreadingHTTPServer(("127.0.0.1", 0), OrsStub) as server:
        thread = Thread(target=server.serve_forever, daemon=True)
        thread.start()
        ors_url = f"http://127.0.0.1:{server.server_port}/openrouteservice"
        try:
            check_capture(args.port, ors_url)
            with demo(args.port, Integrations__Ors__Mode="live", Integrations__Ors__ApiKey="test-ors-key",
                      Integrations__Ors__BaseUrl=ors_url,
                      Integrations__Egnos__MaxHpl="8") as base:
                body = {"commandId": "book", "requestId": "request-15", "unitId": "unit-3", "expectedVersion": 1}
                unverified = api(base, "dispatch/commands/assign", body, expected=422)
                assert unverified["code"] == "PositionUnverified"
                wrong_node = api(base, "dispatch/commands/assign",
                                 {**body, "confirmedStartNode": "pickup"}, expected=422)
                assert wrong_node["code"] == "PositionUnverified"
                close_bridge(base)
                OrsStub.cross_closed_bridge = True
                body["confirmedStartNode"] = "exit-east"
                rejected = api(base, "dispatch/commands/assign", body, expected=422)
                assert rejected["code"] == "NoEligibleRoute"
                assert not api(base, "dispatch/queries/assignments")["assignments"]
                OrsStub.cross_closed_bridge = False
                assignment = api(base, "dispatch/commands/assign", body)
                assert assignment["positionEvidence"]["mode"] == "manual"
                assert assignment["route"]["provenance"]["mode"] == "live"
        finally:
            server.shutdown()
            thread.join()
    print("Dispatch ORS checks passed: capture, replay, reroute, missing fixture, manual position and closed bridge rejection.")


if __name__ == "__main__":
    main()
