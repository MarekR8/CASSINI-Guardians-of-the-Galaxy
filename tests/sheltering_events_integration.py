"""SH-T06 Stage B: the Sheltering and Routing outboxes on RabbitMQ and the Sheltering instruction board.

Runs the Stage A publication exercise with Integrations:Bus:Mode=live, then checks broker redelivery and replay of
old events, reordering at the same instruction version, and a restart after a commit made while the broker was
unreachable. Requires RabbitMQ with the management plugin:
  RABBITMQ_URI   e.g. amqp://guest:guest@127.0.0.1:5672/
  RABBITMQ_API   e.g. http://127.0.0.1:15672 (guest/guest unless RABBITMQ_API_USER/PASSWORD are set)
"""

import argparse
import base64
import json
import os
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory
import time
from urllib.error import HTTPError
from urllib.request import Request
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheltering_publication_integration import SUBJECT, book, publication, reconcile, route_outbox
from sheltering_reservations_integration import INCIDENT, Client, use_fixtures
from offline_demo import ARTIFACTS, LOCAL_HTTP, ROOT, start_services

EXCHANGE = "evacuation.events"
QUEUE = "sheltering.events"


def rabbit(path, body=None, method=None, expected=(200, 201, 204)):
    """RabbitMQ management API (RABBITMQ_API, RABBITMQ_API_USER and RABBITMQ_API_PASSWORD; guest by default)."""
    login = f"{os.environ.get('RABBITMQ_API_USER', 'guest')}:{os.environ.get('RABBITMQ_API_PASSWORD', 'guest')}"
    headers = {"Authorization": "Basic " + base64.b64encode(login.encode()).decode(), "Content-Type": "application/json"}
    data = json.dumps(body).encode() if body is not None else None
    url = os.environ.get("RABBITMQ_API", "http://127.0.0.1:15672").rstrip("/") + "/api/" + path
    try:
        response = LOCAL_HTTP.open(Request(url, data, headers, method=method), timeout=10)
    except HTTPError as error:
        response = error
    with response:
        content = response.read()
        assert response.status in expected, (url, response.status, content)
        return json.loads(content) if content else None


def eventually(check, timeout):
    deadline = time.monotonic() + timeout
    while not check():
        assert time.monotonic() < deadline, "condition not reached in time"
        time.sleep(0.3)


def board(client):
    return client.query("sheltering/queries/shelter-instruction-board", {"incidentId": INCIDENT})


def entry(client):
    return next(row for row in board(client)["entries"] if row["key"] == SUBJECT)


def settled(client, acks):
    """Routing's outbox is confirmed by the broker and the board acknowledged at least `acks` deliveries."""
    queue = rabbit(f"queues/%2F/{QUEUE}")
    return (all(item["published"] for item in route_outbox(client)) and queue.get("messages") == 0 and
            queue.get("message_stats", {}).get("ack", 0) >= acks)


def acknowledged():
    return rabbit(f"queues/%2F/{QUEUE}").get("message_stats", {}).get("ack", 0)


def publish(event):
    routed = rabbit(f"exchanges/%2F/{EXCHANGE}/publish", {
        "properties": {"message_id": event["eventId"], "delivery_mode": 2, "content_type": "application/json",
                       "type": event["type"]},
        "routing_key": "routing." + ("route-published.v1" if event["type"] == "RoutePublished.v1" else "route-withdrawn.v1"),
        "payload": json.dumps(event), "payload_encoding": "string"})
    assert routed["routed"], "the board queue must be bound"


def live(uri):
    os.environ.update({"Integrations__Bus__Mode": "live", "Integrations__Bus__Uri": uri})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=5980)
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    if not args.no_build:
        subprocess.run(["dotnet", "build", "Cassini.Offline.slnx", "-m:1", "-p:NuGetAudit=false", "-p:EnableRabbitMq=true"],
                       cwd=ROOT, check=True)
    use_fixtures()
    rabbit(f"queues/%2F/{QUEUE}", method="DELETE", expected=(204, 404))
    with TemporaryDirectory(prefix="sheltering-events-", dir=ARTIFACTS) as directory:
        folder = Path(directory)
        live(os.environ["RABBITMQ_URI"])
        with start_services(args.port, folder, sheltering=True) as base:
            client = Client(base)
            saved = publication(client)
            eventually(lambda: settled(client, len(saved["events"])), timeout=60)
            withdrawn = entry(client)
            assert (withdrawn["state"], withdrawn["routeVersion"], withdrawn["instructionVersion"]) == ("Withdrawn", None, 3)
            assert board(client)["counts"] == {"Withdrawn": 1}, board(client)
            delivered = acknowledged()

            # Broker redelivery (same event IDs) and replay of old facts with new IDs in reverse order, including
            # the two events of the transfer at instruction version 2: the board keeps the withdrawal.
            events = [item["event"] for item in route_outbox(client)]
            for event in events + [dict(event, eventId=str(uuid.uuid4())) for event in reversed(events)]:
                publish(event)
            eventually(lambda: settled(client, delivered + 2 * len(events)), timeout=60)
            assert entry(client) == withdrawn and board(client)["counts"] == {"Withdrawn": 1}

        # Commit while the broker is unreachable: the events stay in the outboxes, Stage A keeps working over HTTP.
        live("amqp://guest:guest@127.0.0.1:1/")
        with start_services(args.port, folder, sheltering=True) as base:
            client = Client(base)
            rebooked = book(client, "book-b-again", "shelter-b")
            current = reconcile(client, "publish-b-again", rebooked["version"])
            assert current["state"] == "Current" and current["instruction"]["current"]["routeVersion"] == 3, current
            assert not route_outbox(client)[-1]["published"], "nothing is confirmed without the broker"

        # Restart with the broker: the pending events are published after the restart and the board follows them.
        live(os.environ["RABBITMQ_URI"])
        with start_services(args.port, folder, sheltering=True) as base:
            client = Client(base)
            eventually(lambda: settled(client, delivered + 2 * len(events) + 2), timeout=60)
            restored = entry(client)
            assert (restored["state"], restored["routeVersion"], restored["instructionVersion"]) == ("Current", 3, 4), restored
            assert restored["reservationId"] == rebooked["reservation"]["reservationId"]
            assert board(client)["counts"] == {"Current": 1}
    print("SH-T06 events passed: both outboxes reached RabbitMQ, the board ignored redelivered, replayed and reordered "
          "events, and events committed while the broker was down were published after a restart.")


if __name__ == "__main__":
    main()
