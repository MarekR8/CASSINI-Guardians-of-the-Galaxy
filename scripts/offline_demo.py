"""Build and run all local CQRS demo processes with the Python standard library."""

import argparse
from contextlib import contextmanager
import os
from pathlib import Path
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
def start_services(port, data_dir, scenario="sample", city_directory=None):
    data_dir.mkdir(parents=True, exist_ok=True)
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
           for service, _, offset in SERVICES if service != "Gateway"},
    })
    children = []
    try:
        for directory, assembly, offset in SERVICES:
            dll = ROOT / "src" / directory / "bin" / "Debug" / "net10.0" / (assembly + ".dll")
            log = data_dir / (directory.lower() + ".log")
            with log.open("w", encoding="utf-8") as stream:
                process = subprocess.Popen(
                    ["dotnet", str(dll), "--urls", local_url(port + offset)],
                    cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=stream, stderr=subprocess.STDOUT,
                )
            children.append((process, log))
        for (directory, _, offset), (process, log) in zip(SERVICES, children):
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
    parser.add_argument("--port", type=int, default=5080, help="Gateway port (uses this port and the next four)")
    parser.add_argument("--data", type=Path, help="Local state/log directory")
    parser.add_argument("--scenario", choices=("valencia", "sample"),
                        help="Defaults to Valencia, or to the sample when running --test")
    parser.add_argument("--city-directory", type=Path, help="Directory with the generated offline map and graph")
    parser.add_argument("--no-build", action="store_true", help="Use existing compiled services")
    parser.add_argument("--test", action="store_true", help="Run integration tests in fresh temporary state")
    args = parser.parse_args()
    if not 1024 <= args.port <= 65531:
        parser.error("port must be between 1024 and 65531")
    args.scenario = args.scenario or ("sample" if args.test else "valencia")
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
            with start_services(args.port, Path(directory), args.scenario,
                                city.resolve() if city else None) as url:
                scripts = (["valencia_integration.py"] if args.scenario == "valencia" else
                           ["offline_integration.py", "connectivity_integration.py"])
                for script in scripts:
                    subprocess.run([os.sys.executable, str(ROOT / "tests" / script), url], cwd=ROOT, check=True)
        return
    data = args.data or ARTIFACTS / ("offline-demo" if args.scenario == "sample" else args.scenario + "-demo")
    with start_services(args.port, data.resolve(), args.scenario, city.resolve() if city else None) as url:
        print("CASSINI Offline: " + url, flush=True)
        print("Ctrl+C zatrzymuje lokalne mikroserwisy.", flush=True)
        try:
            while True:
                time.sleep(1)
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
