"""SH-T07 rehearsal: the sheltering acceptance matrix (SH-A01-SH-A08) from fresh state, with one result per check.

Each suite starts its own services in a new temporary state directory, so no run reuses another's state. The
runner builds once, runs the suites, maps them to the acceptance checks and writes
artifacts/sheltering-rehearsal.json.

    python tests/sheltering_rehearsal.py                 # local processes
    python tests/sheltering_rehearsal.py --browser       # also the Playwright journey (Stage B)
    python tests/sheltering_rehearsal.py --images-only   # SH-A08 on prebuilt Compose images (CI job verify-sheltering-images)

The image path never builds or pulls (`--no-build --pull never`) and starts from empty volumes. It runs the sample
regression on compose.yaml, then the sheltering exercise on compose.yaml + compose.sheltering.yaml, including a
restart. It also checks that a service on the internal `operations` network cannot reach the internet. Build the
images first:
    docker compose -f compose.yaml -f compose.sheltering.yaml build
"""

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
from urllib.request import ProxyHandler, build_opener

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from offline_demo import ARTIFACTS, ROOT, build  # noqa: E402

PYTHON = sys.executable
SUITES = {
    "sample-regression": [PYTHON, "scripts/offline_demo.py", "--test", "--no-build"],
    "catalog-and-reception": [PYTHON, "scripts/offline_demo.py", "--sheltering", "--test", "--no-build"],
    "destinations": [PYTHON, "tests/destination_routing_integration.py", "--no-build"],
    "grouping": [PYTHON, "tests/grouping_integration.py", "--no-build"],
    "reservations": [PYTHON, "tests/sheltering_reservations_integration.py", "--no-build"],
    "publication": [PYTHON, "tests/sheltering_publication_integration.py", "--no-build"],
}
CHECKS = {
    "SH-A01": (["destinations"], "First leg to a verified point with shelter pending; infeasible leg escalated."),
    "SH-A02": (["grouping"], "Household with separate origins; split recorded when a shared destination fails."),
    "SH-A03": (["grouping"], "Separation from a caregiver rejected without a valid handover."),
    "SH-A04": (["reservations"], "One request takes the last place; exhausted limits reject the batch."),
    "SH-A05": (["catalog-and-reception"], "Partial arrival converts one hold; replay counts once; departure frees once."),
    "SH-A06": (["reservations", "catalog-and-reception", "publication"],
               "Closure en route: transfer or pending reassignment and withdrawn guidance; occupancy kept until departure."),
    "SH-A07": (["publication"], "Publication fails after booking; retried and stale commands never add a route version "
                                "or restore withdrawn guidance. Broker redelivery: sheltering_events_integration.py (RabbitMQ)."),
    "SH-A08": (["sample-regression", "catalog-and-reception", "images"],
               "Sample regression and sheltering exercise from fresh state on prebuilt images without internet."),
}
SAMPLE_OVERRIDE = """# Written by tests/sheltering_rehearsal.py: the sample regression expects manual replanning (as offline_demo.py --test).
services:
  dispatch:
    environment:
      Dispatch__AutoReplan: "false"
"""


def run(name, command, env=None):
    started = time.monotonic()
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace",
                            env={**os.environ, "PYTHONIOENCODING": "utf-8", **(env or {})})
    lines = [line for line in result.stdout.splitlines() if "passed" in line or "survived" in line or "Shelter" in line]
    outcome = {"suite": name, "passed": result.returncode == 0, "seconds": round(time.monotonic() - started, 1),
               "summary": lines[-4:] if result.returncode == 0 else (result.stdout + result.stderr).splitlines()[-20:]}
    print(f"{'PASS' if outcome['passed'] else 'FAIL'} {name} ({outcome['seconds']} s)", flush=True)
    if not outcome["passed"]:
        print("\n".join("    " + line for line in outcome["summary"]), flush=True)
    return outcome


class Stack:
    """One Compose project on prebuilt images with empty volumes, removed again on exit."""

    def __init__(self, docker, project, files, port):
        self.base = [docker, "compose", "-p", project, *sum((["-f", str(name)] for name in files), [])]
        self.env = {**os.environ, "CASSINI_PORT": str(port)}
        self.url = f"http://127.0.0.1:{port}"

    def compose(self, *args, check=True):
        result = subprocess.run(self.base + list(args), cwd=ROOT, env=self.env, capture_output=True, text=True)
        if check and result.returncode != 0:
            raise RuntimeError(f"docker compose {' '.join(args)} failed:\n{result.stderr[-3000:]}")
        return result

    def __enter__(self):
        self.compose("down", "-v", check=False)
        self.compose("up", "-d", "--no-build", "--pull", "never", "--wait")
        return self

    def __exit__(self, *_):
        logs = self.compose("logs", "--no-color", "--tail", "200", check=False).stdout
        (ARTIFACTS / f"rehearsal-{self.base[3]}.log").write_text(logs, encoding="utf-8")
        self.compose("down", "-v", check=False)

    def restart(self, *services):
        self.compose("stop", *services)
        self.compose("up", "-d", "--no-build", "--pull", "never", "--wait", *services)


def images(port):
    """SH-A08 on prebuilt images: the sample regression, then the sheltering exercise with a restart."""
    docker = shutil.which("docker")
    if docker is None:
        return {"suite": "images", "passed": None, "summary": ["Not run: no container engine on this machine."]}
    ARTIFACTS.mkdir(exist_ok=True)
    override = ARTIFACTS / "rehearsal-sample.override.yaml"
    override.write_text(SAMPLE_OVERRIDE, encoding="utf-8")
    steps = []
    try:
        with Stack(docker, "cassini-rehearsal-sample", ["compose.yaml", override], port) as stack:
            for script in ("offline_integration.py", "connectivity_integration.py"):
                steps.append(run(f"images:{script}", [PYTHON, f"tests/{script}", stack.url]))
        with Stack(docker, "cassini-rehearsal-sheltering", ["compose.yaml", "compose.sheltering.yaml"], port) as stack:
            with build_opener(ProxyHandler({})).open(stack.url + "/features", timeout=10) as response:
                assert json.load(response) == {"sheltering": True}
            # The owners sit on the internal network: an outbound connection from Sheltering must fail.
            probe = stack.compose("exec", "-T", "sheltering", "bash", "-c",
                                  "timeout 5 bash -c 'exec 3<>/dev/tcp/1.1.1.1/443' && echo online || echo offline",
                                  check=False)
            offline = probe.stdout.strip().endswith("offline")
            steps.append({"suite": "images:no-internet", "passed": offline, "summary": [probe.stdout.strip()[-200:]]})
            print(f"{'PASS' if offline else 'FAIL'} images:no-internet", flush=True)
            steps.append(run("images:sheltering_integration.py", [PYTHON, "tests/sheltering_integration.py", stack.url]))
            stack.restart("situation", "sheltering")
            steps.append(run("images:sheltering_integration.py --after-restart",
                             [PYTHON, "tests/sheltering_integration.py", stack.url, "--after-restart"]))
    except (RuntimeError, AssertionError, OSError) as error:
        steps.append({"suite": "images:compose", "passed": False, "summary": str(error).splitlines()[-20:]})
        print("FAIL images:compose\n    " + str(error)[-2000:], flush=True)
    passed = all(step["passed"] for step in steps)
    return {"suite": "images", "passed": passed, "steps": steps,
            "summary": [f"{step['suite']}: {'pass' if step['passed'] else 'fail'}" for step in steps]}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--images", action="store_true", help="Also run SH-A08 on prebuilt Compose images")
    parser.add_argument("--images-only", action="store_true", help="Run only the prebuilt-image path")
    parser.add_argument("--image-port", type=int, default=5980)
    parser.add_argument("--browser", action="store_true", help="Also run the Playwright journey")
    args = parser.parse_args()
    suites = {}
    if not args.images_only:
        if not args.no_build:
            build()
        suites = {name: run(name, command) for name, command in SUITES.items()}
        if os.environ.get("RABBITMQ_URI"):
            suites["events"] = run("events", [PYTHON, "tests/sheltering_events_integration.py", "--no-build"])
            CHECKS["SH-A07"][0].append("events")
        if args.browser:
            suites["browser"] = run("browser", [PYTHON, "tests/sheltering_browser.py", "--no-build"])
    if args.images or args.images_only:
        suites["images"] = images(args.image_port)

    matrix = {}
    for check, (needs, expected) in CHECKS.items():
        results = [suites[name]["passed"] for name in needs if name in suites]
        missing = [name for name in needs if name not in suites or suites[name]["passed"] is None]
        state = ("Fail" if False in results else "Not run" if not results or all(value is None for value in results)
                 else "Pass" if not missing else "Pass (not run: " + ", ".join(missing) + ")")
        matrix[check] = {"state": state, "suites": needs, "expected": expected}

    print(f"\n{'Check':<8} {'State':<34} Suites")
    for check, item in matrix.items():
        print(f"{check:<8} {item['state']:<34} {', '.join(item['suites'])}")
    report = {"ranAt": datetime.now(timezone.utc).isoformat(timespec="seconds"), "matrix": matrix, "suites": suites}
    ARTIFACTS.mkdir(exist_ok=True)
    (ARTIFACTS / "sheltering-rehearsal.json").write_text(json.dumps(report, indent=1, ensure_ascii=False) + "\n",
                                                          encoding="utf-8")
    if any(item["passed"] is False for item in suites.values()) or \
            (args.images_only and suites["images"]["passed"] is not True):
        sys.exit(1)


if __name__ == "__main__":
    main()
