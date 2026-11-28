"""Verify Dispatch against recorded routes and an ORS-compatible local HTTP stub."""

from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from hashlib import sha256
from pathlib import Path
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
        assert self.path == "/v2/directions/driving-car/geojson", self.path
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


def check_replay(port, scenario):
    with demo(port, scenario, Integrations__Ors__Mode="mock") as base:
        view = api(base, "dispatch/queries/assignments")
        assert view["units"][0]["positionEvidence"]["mode"] == "synthetic"
        assignment = book(base)
        assert assignment == book(base)
        assert assignment["route"]["provenance"]["mode"] == "synthetic"
        assert len(assignment["route"]["approachGeometry"]) >= 2
        assert assignment["route"]["exit"] == ("exit-east" if scenario == "sample" else "exit-north")
        close_bridge(base)
        assert not api(base, "dispatch/queries/assignments")["assignments"][0]["routeCurrent"]
        updated = api(base, "dispatch/commands/replan", {
            "commandId": "replan", "assignmentId": assignment["assignmentId"],
            "expectedVersion": assignment["version"],
        })
        assert updated["route"]["exit"] == "exit-north"
        assert len(updated["route"]["outboundGeometry"]) >= 2


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=16880)
    args = parser.parse_args()
    check_replay(args.port, "sample")
    check_replay(args.port, "warsaw")

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
        try:
            with demo(args.port, Integrations__Ors__Mode="live", Integrations__Ors__ApiKey="test-ors-key",
                      Integrations__Ors__BaseUrl=f"http://127.0.0.1:{server.server_port}",
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
    print("Dispatch ORS checks passed: replay, reroute, missing fixture, manual position and closed bridge rejection.")


if __name__ == "__main__":
    main()
