"""SH-T04 Stage B: the reservation exercise on PostgreSQL with two Sheltering instances, duplicates, stale writes
and a restart of every process against the persisted state. Needs a fresh database in ConnectionStrings__Sheltering."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
import os
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheltering_reservations_integration import (AT, EXPIRES, INCIDENT, LEG, Client, after_restart, exercise,
                                                 use_fixtures, walker_query)
from offline_demo import ARTIFACTS, ROOT, local_url, start_services, wait_ready

FIXTURE = ROOT / "data" / "demo" / "sheltering" / "sample-sites.json"


@contextmanager
def second_sheltering(port, gateway_port, folder):
    """Another Sheltering process on the same database, reached directly rather than through the Gateway."""
    dll = ROOT / "src/Sheltering/bin/Debug/net10.0/Cassini.Sheltering.dll"
    env = dict(os.environ, ASPNETCORE_ENVIRONMENT="Production", DOTNET_ENVIRONMENT="Production",
               HTTP_PROXY="", HTTPS_PROXY="", ALL_PROXY="", Sheltering__SeedFile=str(FIXTURE),
               Situation__BaseUrl=local_url(gateway_port + 2), Routing__BaseUrl=local_url(gateway_port + 3),
               Management__BaseUrl=local_url(gateway_port + 1))
    log = folder / "sheltering-second.log"
    with log.open("w", encoding="utf-8") as output:
        process = subprocess.Popen(["dotnet", str(dll), "--urls", local_url(port)], cwd=ROOT, env=env,
                                   stdin=subprocess.DEVNULL, stdout=output, stderr=subprocess.STDOUT)
    try:
        wait_ready(local_url(port) + "/health/ready", process, log)
        yield local_url(port)
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


@contextmanager
def deployment(port, folder):
    # Both instances handle synthetic writes in this exercise, including the peer started outside the launcher.
    with patch.dict(os.environ, {"SiteCatalog__AccessMode": "demo"}):
        with start_services(port, folder, sheltering=True) as base, second_sheltering(port + 10, port, folder) as peer:
            yield Client(base), Client(base, sheltering=peer)


def decisions(client, case_id):
    rows = client.query("sheltering/queries/allocation-evidence",
                        {"incidentId": INCIDENT, "caseId": case_id, "legId": LEG})["evidence"]
    return [(row["decision"], row["status"], row["shelterId"], row["routeEvidenceId"] is not None, row["finding"])
            for row in rows]


def cross_instance(client, peer, saved):
    """Duplicate, stale and read-your-write cases across the two instances."""
    opened = {"commandId": "open-case-41", "incidentId": INCIDENT, "caseId": "case-41", "legId": LEG,
              "needs": {"places": 1, "accessiblePlaces": 0}, "groupPlanRef": None}
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(lambda sender: sender.command("open-shelter-assignment", opened), [client, peer]))
    assert results[0] == results[1] and results[0]["assignment"]["version"] == 1, results
    listed = peer.query("sheltering/queries/shelter-assignments", {"incidentId": INCIDENT})["assignments"]
    assert [item["caseId"] for item in listed].count("case-41") == 1
    peer.command("open-shelter-assignment", {**opened, "needs": {"places": 2, "accessiblePlaces": 0}},
                 409, "CommandConflict")

    # Stale writes: the assignment and the shelter were last written through the other instance.
    route = {"evidenceId": "0" * 64, "query": walker_query("case-31")}
    stale = {"commandId": "stale-41", "shelterId": "shelter-b", "expectedShelterVersion": peer.version("shelter-b"),
             "holdsExpireAt": EXPIRES, "approvalRef": "synthetic-coordinator",
             "assignments": [{"incidentId": INCIDENT, "caseId": "case-41", "legId": LEG, "expectedVersion": 2,
                              "route": route}]}
    peer.command("reserve-shelter-places", stale, 409, "AssignmentVersionConflict")
    shelter = client.shelters()["shelter-b"]
    client.configure("shelter-b", "rename-b", name="Schronienie północne B (PG)")
    stale_config = {key: shelter[key] for key in ("shelterId", "place", "stepFree", "status", "operatingWindow",
                                                  "evidence", "resources")}
    stale_config.update(commandId="stale-rename-b", expectedVersion=shelter["version"], name="Stale name")
    peer.command("configure-shelter", stale_config, 409, "VersionConflict")
    assert peer.shelters()["shelter-b"]["name"] == "Schronienie północne B (PG)"

    # Reconciliation evidence of the exercise is persisted and visible from either instance.
    winner = decisions(peer, saved["winner"])
    assert [row[0] for row in winner] == ["OpenShelterAssignment", "ReserveShelterPlaces", "ReassignShelterPlaces",
                                         "ReleaseShelterReservation"], winner
    assert winner[1][1:4] == ("Reserved", "shelter-a", True) and winner[2][1:4] == ("Reserved", "shelter-b", True)
    loser = decisions(client, saved["loser"])
    assert loser[-1] == ("InvalidateShelterAssignment", "AwaitingReplacement", "shelter-b", False, "SiteClosed"), loser
    outbox = len(client.query("sheltering/queries/allocation-outbox", {"after": 0})["entries"])
    return {"winner": winner, "loser": loser, "open": results[0], "saved": {**saved, "outbox": outbox}}


def after_full_restart(client, peer, saved, extra):
    saved = extra["saved"]
    after_restart(client, saved)
    assert peer.command("reserve-shelter-places", saved["race"]) == saved["raceResult"], "receipt from the database"
    assert peer.command("open-shelter-assignment", {
        "commandId": "open-case-41", "incidentId": INCIDENT, "caseId": "case-41", "legId": LEG,
        "needs": {"places": 1, "accessiblePlaces": 0}, "groupPlanRef": None}) == extra["open"]
    assert decisions(peer, saved["winner"]) == extra["winner"] and decisions(client, saved["loser"]) == extra["loser"]
    assert peer.shelters()["shelter-b"]["name"] == "Schronienie północne B (PG)"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=17980)
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args()
    if not os.environ.get("ConnectionStrings__Sheltering"):
        raise SystemExit("Provide ConnectionStrings__Sheltering for a fresh integration-test database.")
    if not args.no_build:
        subprocess.run(["dotnet", "build", "Cassini.Offline.slnx", "-m:1", "-p:NuGetAudit=false",
                        "-p:EnableShelteringPostgres=true"], cwd=ROOT, check=True)
    use_fixtures()
    with TemporaryDirectory(prefix="sheltering-postgres-", dir=ARTIFACTS) as directory:
        folder = Path(directory)
        with deployment(args.port, folder) as (client, peer):
            saved = exercise(client, peer)
            extra = cross_instance(client, peer, saved)
        with deployment(args.port, folder) as (client, peer):
            after_full_restart(client, peer, saved, extra)
    print(f"SH-T04 PostgreSQL passed: two instances, {saved['winner']} took the final place, duplicates returned one "
          "receipt, stale writes were rejected, and receipts, evidence and holds survived a restart of every process.")


if __name__ == "__main__":
    main()
