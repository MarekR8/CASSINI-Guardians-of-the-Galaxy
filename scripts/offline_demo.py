"""Build and run all local CQRS demo processes with the Python standard library."""

import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
from urllib.error import URLError
from urllib.request import ProxyHandler, build_opener


ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts"
SERVICES = [
    ("Management", "Cassini.Management", 1),
    ("Situation", "Situation", 2),
    ("Routing", "Cassini.Routing", 3),
    ("Dispatch", "Cassini.Dispatch", 4),
    ("Gateway", "Gateway", 0),
]
LOCAL_HTTP = build_opener(ProxyHandler({}))


def build():
    subprocess.run(
        ["dotnet", "build", "Cassini.Offline.slnx", "-m:1", "-p:NuGetAudit=false"],
        cwd=ROOT,
        check=True,
    )


def local_url(port):
    return f"http://127.0.0.1:{port}"


@contextmanager
def start_services(port, data_dir, scenario="sample", city_directory=None, sheltering=False, *,
                   auto_replan=None, evacuation_enabled=None, shelter_holds=False):
    data_dir.mkdir(parents=True, exist_ok=True)
    services = [*SERVICES, ("Sheltering", "Cassini.Sheltering", 5)] if sheltering else SERVICES
    env = dict(os.environ)
    env.update({
        "DOTNET_ENVIRONMENT": "Production",
        "ASPNETCORE_ENVIRONMENT": "Production",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "HTTP_PROXY": "",
        "HTTPS_PROXY": "",
        "ALL_PROXY": "",
        "Demo__DataDirectory": str(data_dir),
        "Demo__Scenario": scenario,
        "Demo__CityDirectory": str(city_directory) if city_directory else "",
        "Situation__StateFile": str(data_dir / "situation.json"),
        "Routing__StatePath": str(data_dir / "routing-draws.json"),
        "Situation__BaseUrl": local_url(port + 2),
        "Routing__BaseUrl": local_url(port + 3),
        "Management__BaseUrl": local_url(port + 1),
        **{f"Services__{service}": local_url(port + offset)
           for service, _, offset in services if service != "Gateway"},
    })
    if auto_replan is not None:
        env["Dispatch__AutoReplan"] = str(auto_replan).lower()
    if evacuation_enabled is not None:
        env["Routing__EvacuationEnabled"] = str(evacuation_enabled).lower()
    if sheltering:
        fixture = ROOT / "data" / "demo" / "sheltering" / "sample-sites.json"
        env.update({
            "AssemblyPoints__SeedFile": str(fixture),
            "AssemblyPoints__StateFile": str(data_dir / "assembly-points.json"),
            "Sheltering__SeedFile": str(fixture),
            "Sheltering__StateFile": str(data_dir / "shelters.json"),
            "Sheltering__BaseUrl": local_url(port + 5),
        })
        if shelter_holds:
            env["Sheltering__HoldsFile"] = str(ROOT / "data" / "demo" / "sheltering" / "sample-holds.json")
            env["Sheltering__OperatorsFile"] = str(exercise_operators(data_dir))
    children = []
    try:
        for directory, assembly, offset in services:
            dll = ROOT / "src" / directory / "bin" / "Debug" / "net10.0" / (assembly + ".dll")
            log = data_dir / (directory.lower() + ".log")
            with log.open("w", encoding="utf-8") as stream:
                process = subprocess.Popen(
                    ["dotnet", str(dll), "--urls", local_url(port + offset)],
                    cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=stream, stderr=subprocess.STDOUT,
                )
            children.append((process, log))
        for (directory, _, offset), (process, log) in zip(services, children):
            health = "/health" if directory == "Gateway" else "/health/ready"
            wait_ready(local_url(port + offset) + health, process, log)
        yield local_url(port)
    finally:
        for process, _ in reversed(children):
            if process.poll() is None:
                process.terminate()
        for process, _ in reversed(children):
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


# Synthetic exercise accounts for the Stage B reception role boundary (SH-T05).
EXERCISE_OPERATORS = {
    "op-shelter-a": ("shelter-operator", "incident-42", ["shelter-a"]),
    "op-shelter-b": ("shelter-operator", "incident-42", ["shelter-b"]),
    "coordinator-42": ("coordinator", "incident-42", []),
    "op-other-incident": ("shelter-operator", "incident-99", ["shelter-a"]),
}


def exercise_operators(data_dir):
    """Generate random tokens once per state directory. Only their hashes go to the service; nothing is in Git."""
    tokens_file, operators_file = data_dir / "operator-tokens.json", data_dir / "operators.json"
    if not tokens_file.is_file():
        tokens = {name: secrets.token_urlsafe(24) for name in EXERCISE_OPERATORS}
        tokens_file.write_text(json.dumps(tokens), encoding="utf-8")
        operators_file.write_text(json.dumps({"operators": [
            {"operatorId": name, "role": role, "incidentId": incident, "shelterIds": shelters,
             "tokenSha256": hashlib.sha256(tokens[name].encode()).hexdigest()}
            for name, (role, incident, shelters) in EXERCISE_OPERATORS.items()]}), encoding="utf-8")
    return operators_file


def wait_ready(url, process, log):
    deadline = time.monotonic() + 25
    while time.monotonic() < deadline and process.poll() is None:
        try:
            with LOCAL_HTTP.open(url, timeout=1) as response:
                if response.status == 200:
                    return
        except (OSError, URLError):
            time.sleep(0.15)
    tail = log.read_text(encoding="utf-8", errors="replace")[-3000:]
    raise RuntimeError(f"Service did not start at {url}.\n{tail}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=5080, help="Gateway port (uses the next four, or five with --sheltering)")
    parser.add_argument("--data", type=Path, help="Local state/log directory")
    parser.add_argument("--scenario", choices=("valencia", "sample"),
                        help="Defaults to Valencia, or to the sample when running --test")
    parser.add_argument("--city-directory", type=Path, help="Directory with the generated offline map and graph")
    parser.add_argument("--no-build", action="store_true", help="Use existing compiled services")
    parser.add_argument("--test", action="store_true", help="Run integration tests in fresh temporary state")
    parser.add_argument("--valencia-inputs-only", action="store_true",
                        help="With --scenario valencia --test, verify layers, graph and closures without the evacuation planner")
    parser.add_argument("--sheltering", action="store_true", help="Enable the sample assembly point and shelter catalogs")
    args = parser.parse_args()
    last_port = 65530 if args.sheltering else 65531
    if not 1024 <= args.port <= last_port:
        parser.error(f"port must be between 1024 and {last_port}")
    args.scenario = args.scenario or ("sample" if args.test or args.sheltering else "valencia")
    if args.valencia_inputs_only and not (args.test and args.scenario == "valencia"):
        parser.error("--valencia-inputs-only requires --scenario valencia --test")
    if args.sheltering and args.scenario != "sample":
        parser.error("--sheltering is available in the sample scenario")
    if args.city_directory and args.scenario == "sample":
        parser.error("--city-directory requires a city scenario")
    city = args.city_directory
    default_city = ARTIFACTS / (args.scenario + "-city")
    if city is None and args.scenario == "valencia" and (default_city / "city-network.json.gz").is_file():
        city = default_city
    if args.scenario == "valencia" and (city is None or not all(
        (city / name).is_file() for name in ("city-network.json.gz", "city-map.svg", "hazard-seed.json",
                                          "city-imagery.json", "city-nodes.svg", "sentinel-before.jpg",
                                          "sentinel-event.jpg")
    )):
        parser.error("Prepare Valencia first: python experiments/network/prepare_valencia.py")
    if not args.no_build:
        build()
    if args.test:
        ARTIFACTS.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="integration-", dir=ARTIFACTS) as directory:
            test_file = ("sheltering_integration.py" if args.sheltering else
                         "valencia_integration.py" if args.scenario == "valencia" else "offline_integration.py")
            scripts = [test_file, "connectivity_integration.py"] if test_file == "offline_integration.py" else [test_file]
            test_options = ["--inputs-only"] if args.valencia_inputs_only else []
            with start_services(args.port, Path(directory), args.scenario,
                                city.resolve() if city else None, sheltering=args.sheltering,
                                auto_replan=False, evacuation_enabled=False if args.valencia_inputs_only else None) as url:
                for script in scripts:
                    subprocess.run([os.sys.executable, str(ROOT / "tests" / script), url, *test_options], cwd=ROOT, check=True)
            if args.sheltering:
                with start_services(args.port, Path(directory), sheltering=True) as url:
                    subprocess.run(
                        [os.sys.executable, str(ROOT / "tests" / test_file), url, "--after-restart"],
                        cwd=ROOT,
                        check=True,
                    )
                reception = Path(directory) / "reception"
                for args_tail in ([], ["--after-restart"]):
                    with start_services(args.port, reception, sheltering=True, shelter_holds=True) as url:
                        subprocess.run([os.sys.executable, str(ROOT / "tests" / "shelter_reception_integration.py"),
                                        url, str(reception / "operator-tokens.json"), *args_tail],
                                       cwd=ROOT, check=True)
        return
    state_name = "sheltering-demo" if args.sheltering else "offline-demo" if args.scenario == "sample" else "valencia-demo"
    data = args.data or ARTIFACTS / state_name
    with start_services(args.port, data.resolve(), args.scenario, city.resolve() if city else None,
                        sheltering=args.sheltering) as url:
        print("CASSINI Offline: " + url, flush=True)
        print("Ctrl+C zatrzymuje lokalne mikroserwisy.", flush=True)
        try:
            while True:
                time.sleep(1)
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
