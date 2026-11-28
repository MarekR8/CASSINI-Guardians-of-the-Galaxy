"""SH-T07 rehearsal: the sheltering acceptance matrix (SH-A01-SH-A08) from fresh state, with one result per check.

Each suite starts its own services in a new temporary state directory, so no run reuses another's state. The
runner builds once, runs the suites, maps them to the acceptance checks and writes
artifacts/sheltering-rehearsal.json.

    python tests/sheltering_rehearsal.py                 # local processes (what CI runs)
    python tests/sheltering_rehearsal.py --images        # also the prebuilt-image path with Docker, no pulls or builds
    python tests/sheltering_rehearsal.py --browser       # also the Playwright journey (Stage B)
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
}
CHECKS = {
    "SH-A01": (["destinations"], "First leg to a verified point with shelter pending; infeasible leg escalated."),
    "SH-A02": (["grouping"], "Household with separate origins; split recorded when a shared destination fails."),
    "SH-A03": (["grouping"], "Separation from a caregiver rejected without a valid handover."),
    "SH-A04": (["reservations"], "One request takes the last place; exhausted limits reject the batch."),
    "SH-A05": (["catalog-and-reception"], "Partial arrival converts one hold; replay counts once; departure frees once."),
    "SH-A06": (["reservations", "catalog-and-reception"],
               "Closure en route: transfer or pending reassignment; occupancy kept until departure."),
    "SH-A07": ([], "Route publication failure and event replay. Blocked: SH-T06 publication is not implemented."),
    "SH-A08": (["sample-regression", "catalog-and-reception"],
               "Sample regression and sheltering exercise from fresh state with local fixtures."),
}


def run(name, command):
    started = time.monotonic()
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace",
                            env={**os.environ, "PYTHONIOENCODING": "utf-8"})
    lines = [line for line in result.stdout.splitlines() if "passed" in line or "survived" in line or "Shelter" in line]
    outcome = {"suite": name, "passed": result.returncode == 0, "seconds": round(time.monotonic() - started, 1),
               "summary": lines[-3:] if result.returncode == 0 else (result.stdout + result.stderr).splitlines()[-15:]}
    print(f"{'PASS' if outcome['passed'] else 'FAIL'} {name} ({outcome['seconds']} s)", flush=True)
    return outcome


def images(port):
    """Prebuilt images only: no build, no pull. Starts from empty volumes and removes them afterwards."""
    docker = shutil.which("docker")
    if docker is None:
        return {"suite": "images", "passed": None, "summary": ["Not run: no container engine on this machine."]}
    compose = [docker, "compose", "-p", "cassini-rehearsal", "-f", "compose.yaml", "-f", "compose.sheltering.yaml"]
    env = {**os.environ, "CASSINI_PORT": str(port)}
    subprocess.run(compose + ["down", "-v"], cwd=ROOT, env=env, capture_output=True)
    try:
        up = subprocess.run(compose + ["up", "-d", "--no-build", "--pull", "never", "--wait"], cwd=ROOT, env=env,
                            capture_output=True, text=True)
        if up.returncode != 0:
            return {"suite": "images", "passed": False, "summary": up.stderr.splitlines()[-10:]}
        opener = build_opener(ProxyHandler({}))
        with opener.open(f"http://127.0.0.1:{port}/features", timeout=10) as response:
            assert json.load(response) == {"sheltering": True}
        return run("images", [PYTHON, "tests/sheltering_integration.py", f"http://127.0.0.1:{port}"])
    finally:
        subprocess.run(compose + ["down", "-v"], cwd=ROOT, env=env, capture_output=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--images", action="store_true", help="Also run the catalog test on prebuilt Compose images")
    parser.add_argument("--image-port", type=int, default=5980)
    parser.add_argument("--browser", action="store_true", help="Also run the Playwright journey")
    args = parser.parse_args()
    if not args.no_build:
        build()
    suites = {name: run(name, command) for name, command in SUITES.items()}
    if args.browser:
        suites["browser"] = run("browser", [PYTHON, "tests/sheltering_browser.py", "--no-build"])
    if args.images:
        suites["images"] = images(args.image_port)

    matrix = {}
    for check, (needs, expected) in CHECKS.items():
        if not needs:
            state = "Blocked"
        else:
            results = [suites[name]["passed"] for name in needs]
            state = "Pass" if all(results) else "Fail"
        if check == "SH-A08" and "images" in suites and suites["images"]["passed"] is not True:
            state += " (images: " + ("not run" if suites["images"]["passed"] is None else "fail") + ")"
        matrix[check] = {"state": state, "suites": needs, "expected": expected}

    print(f"\n{'Check':<8} {'State':<26} Suites")
    for check, item in matrix.items():
        print(f"{check:<8} {item['state']:<26} {', '.join(item['suites']) or '-'}")
    report = {"ranAt": datetime.now(timezone.utc).isoformat(timespec="seconds"), "matrix": matrix, "suites": suites}
    ARTIFACTS.mkdir(exist_ok=True)
    (ARTIFACTS / "sheltering-rehearsal.json").write_text(json.dumps(report, indent=1, ensure_ascii=False) + "\n",
                                                          encoding="utf-8")
    if any(item["passed"] is False for item in suites.values()):
        sys.exit(1)


if __name__ == "__main__":
    main()
