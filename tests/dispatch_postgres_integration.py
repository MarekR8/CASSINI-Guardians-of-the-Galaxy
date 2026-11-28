"""Verify booking uniqueness and command receipts across Dispatch processes and a restart."""

from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from pathlib import Path
from subprocess import DEVNULL, STDOUT, Popen, TimeoutExpired
from tempfile import TemporaryDirectory
from urllib.error import HTTPError
from urllib.request import Request
from unittest.mock import patch
import json
import os
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
from offline_demo import LOCAL_HTTP, local_url, start_services, wait_ready


def call(base, endpoint, body=None):
    request = Request(base + endpoint, data=None if body is None else json.dumps(body).encode(),
                      headers={"Content-Type": "application/json"})
    try:
        response = LOCAL_HTTP.open(request, timeout=12)
    except HTTPError as error:
        response = error
    with response:
        return response.status, json.load(response)


@contextmanager
def additional_dispatch(port, gateway_port, folder):
    dll = ROOT / "src/Dispatch/bin/Debug/net10.0/Cassini.Dispatch.dll"
    env = dict(os.environ, ASPNETCORE_ENVIRONMENT="Production", DOTNET_ENVIRONMENT="Production",
               Routing__BaseUrl=local_url(gateway_port + 3), Situation__BaseUrl=local_url(gateway_port + 2),
               Management__BaseUrl=local_url(gateway_port + 1), HTTP_PROXY="", HTTPS_PROXY="", ALL_PROXY="")
    log = folder / "additional-dispatch.log"
    with log.open("w", encoding="utf-8") as output:
        process = Popen(["dotnet", str(dll), "--urls", local_url(port)], cwd=ROOT,
                        env=env, stdin=DEVNULL, stdout=output, stderr=STDOUT)
    try:
        wait_ready(local_url(port) + "/health/ready", process, log)
        yield local_url(port)
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except TimeoutExpired:
            process.kill()
            process.wait()


def main():
    if not os.environ.get("ConnectionStrings__Dispatch"):
        raise SystemExit("Provide ConnectionStrings__Dispatch for a fresh integration-test database.")

    gateway_port = 17880
    other_port = 17885
    (ROOT / "artifacts").mkdir(exist_ok=True)
    with TemporaryDirectory(dir=ROOT / "artifacts") as directory, patch.dict(os.environ, Integrations__Ors__Mode="graph"):
        folder = Path(directory)
        with start_services(gateway_port, folder / "demo") as gateway:
            with additional_dispatch(other_port, gateway_port, folder) as other:
                first = {"commandId": "postgres-request-15", "requestId": "request-15",
                         "unitId": "unit-3", "expectedVersion": 1}
                second = {**first, "commandId": "postgres-request-16", "requestId": "request-16"}
                with ThreadPoolExecutor(max_workers=2) as pool:
                    attempts = [pool.submit(call, gateway, "/api/dispatch/commands/assign", first),
                                pool.submit(call, other, "/commands/assign", second)]
                    results = [attempt.result() for attempt in attempts]
                assert sorted(code for code, _ in results) == [200, 409], results
                winner = next(index for index, (code, _) in enumerate(results) if code == 200)
                receipt = results[winner][1]
                command = (first, second)[winner]
                assert len(receipt["history"]) == 1
                assert call(other, "/commands/assign", command) == (200, receipt)

            with additional_dispatch(other_port, gateway_port, folder) as restarted:
                assert call(restarted, "/commands/assign", command) == (200, receipt)
                code, view = call(restarted, "/queries/assignments")
                assert code == 200 and len(view["assignments"]) == 1 and len(view["assignments"][0]["history"]) == 1
                cancel = {"commandId": "postgres-cancel", "assignmentId": receipt["assignmentId"],
                          "expectedVersion": receipt["version"], "step": "cancel"}
                assert call(restarted, "/commands/progress", cancel)[0] == 200
                loser = (second, first)[winner]
                assert call(gateway, "/api/dispatch/commands/assign", loser)[0] == 200
    print("PostgreSQL Dispatch checks passed: shared booking uniqueness, restart, receipts and released reservation.")


if __name__ == "__main__":
    main()
