"""Management on PostgreSQL with the RabbitMQ bus (Compose profile `full`, task 6 Stage B).

Requires a running PostgreSQL and RabbitMQ with the management plugin:
  ConnectionStrings__Management   e.g. Host=127.0.0.1;Port=5432;Database=management_db;Username=management;Password=…
  RABBITMQ_URI                    e.g. amqp://guest:guest@127.0.0.1:5672/
  RABBITMQ_API                    e.g. http://127.0.0.1:15672 (guest/guest unless RABBITMQ_API_USER/PASSWORD are set)
  PSQL_URL (optional)             e.g. postgresql://management@127.0.0.1:5432/management_db, enables SQL checks
"""

import base64
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import ProxyHandler, Request, build_opener

ROOT = Path(__file__).resolve().parents[1]
LOCAL_HTTP = build_opener(ProxyHandler({}))
PORT = int(os.environ.get("MANAGEMENT_PORT", "5091"))
BASE = f"http://127.0.0.1:{PORT}"
API = os.environ.get("RABBITMQ_API", "http://127.0.0.1:15672").rstrip("/")
API_AUTH = base64.b64encode(
    f"{os.environ.get('RABBITMQ_API_USER', 'guest')}:{os.environ.get('RABBITMQ_API_PASSWORD', 'guest')}".encode()
).decode()
EXCHANGE = "evacuation.events"


def call(url, body=None, method=None, expected=200, auth=None):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"} if data else {}
    if auth:
        headers["Authorization"] = "Basic " + auth
    req = Request(url, data=data, headers=headers, method=method)
    try:
        response = LOCAL_HTTP.open(req, timeout=10)
    except HTTPError as error:
        response = error
    with response:
        content = response.read()
        assert response.status in (expected if isinstance(expected, tuple) else (expected,)), \
            (url, response.status, content)
        return json.loads(content) if content else None


def rabbit(path, body=None, method=None, expected=(200, 201, 204)):
    return call(API + "/api/" + path, body, method, expected, API_AUTH)


def publish(event):
    routed = rabbit(f"exchanges/%2F/{EXCHANGE}/publish", {
        "properties": {"message_id": event["eventId"], "delivery_mode": 2, "content_type": "application/json",
                       "type": event["type"]},
        "routing_key": "dispatch.assignment-changed.v1", "payload": json.dumps(event), "payload_encoding": "string",
    })
    assert routed["routed"], "management.events must be declared before publishing."


def sql(query):
    if not os.environ.get("PSQL_URL") or not shutil.which("psql"):
        return None
    return subprocess.run(["psql", os.environ["PSQL_URL"], "-tAc", query], check=True, capture_output=True,
                          text=True).stdout.strip()


def start(output, log):
    env = {**os.environ, "Integrations__Bus__Mode": "live", "Integrations__Bus__Uri": os.environ["RABBITMQ_URI"],
           "Situation__BaseUrl": "http://127.0.0.1:9", "HTTP_PROXY": "", "HTTPS_PROXY": "",
           "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}
    process = subprocess.Popen(["dotnet", str(output / "Cassini.Management.dll"), "--urls", BASE], cwd=ROOT, env=env,
                               stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline and process.poll() is None:
        try:
            if call(BASE + "/health/ready"):
                return process
        except (OSError, URLError):
            time.sleep(0.2)
    raise RuntimeError("Management did not start; see " + log.name)


def stop(process):
    process.terminate()
    process.wait(timeout=15)


def eventually(check, timeout=15):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = check()
        if value:
            return value
        time.sleep(0.2)
    raise AssertionError("Condition not reached in time.")


def assignment_event(event_id, version, state):
    return {"eventId": event_id, "type": "AssignmentChanged.v1", "producer": "dispatch", "subjectId": "assignment-ci",
            "subjectVersion": version, "occurredAt": "2026-11-27T19:42:00Z", "correlationId": "incident-42",
            "causationId": None, "payload": {"requestId": "request-case-15-1", "assignmentId": "assignment-ci",
                                             "caseId": "case-15", "unitId": "unit-3", "state": state,
                                             "reportedAt": "2026-11-27T19:42:00Z"}}


with tempfile.TemporaryDirectory(prefix="management-full-") as directory:
    output = Path(directory) / "bin"
    subprocess.run(["dotnet", "publish", "src/Management/Cassini.Management.csproj", "-c", "Release", "-o", str(output),
                    "-p:NuGetAudit=false", "-p:EnableManagementPostgres=true", "-p:EnableRabbitMq=true"],
                   cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
    tap = "tests.management-" + uuid.uuid4().hex[:8]
    with open(Path(directory) / "management.log", "w") as log:
        process = start(output, log)
        try:
            # Outbox → RabbitMQ: a milestone reaches a bound queue with the eventId as message ID.
            rabbit(f"queues/%2F/{tap}", {"durable": True, "auto_delete": False}, "PUT")
            rabbit(f"bindings/%2F/e/{EXCHANGE}/q/{tap}", {"routing_key": "management.#"}, "POST")
            command = {"commandId": "full-alert-" + uuid.uuid4().hex}
            alert = call(BASE + "/commands/alert", command)
            messages = eventually(lambda: rabbit(f"queues/%2F/{tap}/get", {
                "count": 10, "ackmode": "ack_requeue_false", "encoding": "auto"}, "POST"))
            events = [json.loads(message["payload"]) for message in messages]
            assert all(message["properties"]["message_id"] == event["eventId"]
                       for message, event in zip(messages, events))
            assert [event["payload"]["alertId"] for event in events
                    if event["type"] == "HazardAlertRecorded.v1"] == [alert["alertId"]], events
            eventually(lambda: sql("SELECT count(*) FROM outbox") in (None, "0"))
            print("Outbox published:", [event["type"] for event in events])

            # Stop the consumer, publish, start again → applied once; a redelivered duplicate changes nothing.
            stop(process)
            first = assignment_event(str(uuid.uuid4()), 2, "PickupConfirmed")
            publish(first)
            publish(first)
            last =assignment_event(str(uuid.uuid4()), 1, "Assigned")
            publish(last)
            process = start(output, log)

            def drained():
                # The inbox receipt commits with the effect. Without SQL access, fall back to the broker's
                # queue depth; the management API omits "messages" until its first statistics sample.
                if os.environ.get("PSQL_URL") and shutil.which("psql"):
                    return sql(f"SELECT count(*) FROM inbox WHERE \"Receipt\" = 'management.events:{last['eventId']}'") == "1"
                return rabbit("queues/%2F/management.events").get("messages", 1) == 0

            eventually(drained, timeout=30)
            case = call(BASE + "/queries/cases/case-15")
            assert case["assistance"]["state"] == "PickupConfirmed" and case["assistance"]["subjectVersion"] == 2, case
            assert case["progress"] != "ExitConfirmed", "Pickup never confirms the exit."
            receipts = sql(f"SELECT count(*) FROM inbox WHERE \"Receipt\" = 'management.events:{first['eventId']}'")
            assert receipts in (None, "1"), receipts

            # PostgreSQL keeps the state across a restart.
            before = call(BASE + "/queries/cases/case-16")
            stop(process)
            process = start(output, log)
            assert call(BASE + "/queries/cases/case-16") == before
            assert call(BASE + "/commands/alert", command) == alert, "The receipt survives the restart."
        finally:
            stop(process)
            rabbit(f"queues/%2F/{tap}", method="DELETE", expected=(204, 404))

print("Management full profile passed: PostgreSQL state and receipts, outbox to RabbitMQ, "
      "stop/publish/start delivered once, pickup is not an exit.")
