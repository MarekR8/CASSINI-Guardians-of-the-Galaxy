"""SH-T01 catalog events on the SH-T06 RabbitMQ bus, with the broker unreachable at commit."""

import argparse
import json
import os
from pathlib import Path
from tempfile import TemporaryDirectory
from time import monotonic, sleep
from unittest.mock import patch
from uuid import uuid4

from sheltering_events_integration import EXCHANGE, eventually, rabbit
from sheltering_stage_b import FIXTURE, POINT, SHELTER, command, write
from offline_demo import ARTIFACTS, start_services

KEYS = {
    "situation.assembly-point-changed.v1": ("AssemblyPointChanged.v1", "situation-data", "assembly-north", "close-point"),
    "sheltering.shelter-availability-changed.v1":
        ("ShelterAvailabilityChanged.v1", "sheltering", "shelter-a", "close-shelter"),
}


def deliveries(queue, requeue=False):
    return rabbit(f"queues/%2F/{queue}/get", {
        "count": 10, "ackmode": "ack_requeue_true" if requeue else "ack_requeue_false", "encoding": "auto",
    })


def assert_deliveries(messages):
    assert len(messages) == 2, messages
    identifiers = {}
    for delivery in messages:
        key = delivery["routing_key"]
        event_type, owner, subject, causation = KEYS[key]
        event = json.loads(delivery["payload"])
        assert event["type"] == event_type and event["producer"] == owner
        assert event["subjectId"] == subject and event["subjectVersion"] == 2, event
        assert event["causationId"] == causation and event["payload"]["status"] == "closed", event
        assert delivery["properties"]["message_id"] == event["eventId"], delivery
        assert delivery["properties"]["delivery_mode"] == 2, delivery
        identifiers[key] = event["eventId"]
    assert identifiers.keys() == KEYS.keys() and len(set(identifiers.values())) == 2
    return identifiers


def recorded(path):
    return json.loads(path.read_text(encoding="utf-8"))["outbox"]


def exercise(queue, port, folder, broker):
    point, = FIXTURE["assemblyPoints"]
    shelter = FIXTURE["shelters"][0]
    point_write = command(point, "close-point", status="closed")
    shelter_write = command(shelter, "close-shelter", status="closed")
    local_outboxes = [folder / "assembly-points.json"]
    if not os.environ.get("ConnectionStrings__Sheltering"):
        local_outboxes.append(folder / "shelters.json")

    with patch.dict(os.environ, {"Integrations__Bus__Mode": "live",
                                 "Integrations__Bus__Uri": "amqp://guest:guest@127.0.0.1:1/"}):
        with start_services(port, folder, sheltering=True) as base:
            point_receipt = write(base, POINT, point_write)
            shelter_receipt = write(base, SHELTER, shelter_write)
            assert not deliveries(queue), "a disconnected publisher cannot confirm or deliver a fact"
            assert all(recorded(path)[0]["publishedAt"] is None for path in local_outboxes)

    with patch.dict(os.environ, {"Integrations__Bus__Mode": "live", "Integrations__Bus__Uri": broker}):
        with start_services(port, folder, sheltering=True) as base:
            assert write(base, POINT, point_write) == point_receipt
            assert write(base, SHELTER, shelter_write) == shelter_receipt

            first = []

            def has_both():
                first[:] = deliveries(queue, requeue=True)
                return len(first) >= 2

            eventually(has_both, timeout=30)
            original_ids = assert_deliveries(first)
            redelivery = deliveries(queue)
            assert assert_deliveries(redelivery) == original_ids
            assert all(message["redelivered"] for message in redelivery), redelivery
            eventually(lambda: all(recorded(path)[0]["publishedAt"] for path in local_outboxes), timeout=15)

        with start_services(port, folder, sheltering=True):
            deadline = monotonic() + 4
            while monotonic() < deadline:
                assert not deliveries(queue), "confirmed events must stay out of the pending queue after restart"
                sleep(0.3)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=16990)
    args = parser.parse_args()
    broker = os.environ["RABBITMQ_URI"]
    queue = "site-catalog-test-" + uuid4().hex
    rabbit(f"exchanges/%2F/{EXCHANGE}", {"type": "topic", "durable": True, "auto_delete": False, "arguments": {}}, "PUT")
    rabbit(f"queues/%2F/{queue}", {"durable": True, "auto_delete": False, "arguments": {}}, "PUT")
    try:
        for key in KEYS:
            rabbit(f"bindings/%2F/e/{EXCHANGE}/q/{queue}", {"routing_key": key, "arguments": {}})
        ARTIFACTS.mkdir(exist_ok=True)
        with TemporaryDirectory(prefix="site-catalog-events-", dir=ARTIFACTS) as directory:
            exercise(queue, args.port, Path(directory), broker)
    finally:
        rabbit(f"queues/%2F/{queue}", method="DELETE")
    print("SH-T01 site events passed: recovery after offline commit, RabbitMQ redelivery, and durable confirms.")


if __name__ == "__main__":
    main()
