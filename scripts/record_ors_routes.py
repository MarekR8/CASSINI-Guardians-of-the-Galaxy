"""Capture ORS GeoJSON responses for the normalized exercise requests in index.json."""

from argparse import ArgumentParser
from datetime import datetime, timezone
from hashlib import sha256
from pathlib import Path
from tempfile import NamedTemporaryFile
from urllib.error import HTTPError, URLError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
import json
import os
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
ORS_URL = "https://api.openrouteservice.org"


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def api_key():
    key = os.environ.get("Integrations__Ors__ApiKey")
    if key:
        return key
    project = ROOT / "src/Routing/Cassini.Routing.csproj"
    secret_id = ET.parse(project).findtext(".//UserSecretsId")
    appdata = os.environ.get("APPDATA")
    directory = Path(appdata) / "Microsoft/UserSecrets" if appdata else Path.home() / ".microsoft/usersecrets"
    path = directory / secret_id / "secrets.json"
    if not path.is_file():
        return None
    secrets = json.loads(path.read_text(encoding="utf-8-sig"))
    return secrets.get("Integrations:Ors:ApiKey")


def write_recording(path, response):
    temporary = None
    try:
        with NamedTemporaryFile(mode="w", dir=path.parent, prefix=path.stem + ".", suffix=".tmp",
                                delete=False, encoding="utf-8") as stream:
            temporary = Path(stream.name)
            fixture = {"mode": "recorded", "observedAt": datetime.now(timezone.utc).isoformat(), "response": response}
            stream.write(json.dumps(fixture, separators=(",", ":")) + "\n")
        temporary.replace(path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main():
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="Destination for the recorded responses")
    args = parser.parse_args()
    key = api_key()
    if not key:
        parser.error("Set Integrations__Ors__ApiKey or the Routing project's .NET user-secret.")
    base_url = os.environ.get("Integrations__Ors__BaseUrl", ORS_URL).rstrip("/")
    parsed_url = urlsplit(base_url)
    if not parsed_url.hostname or parsed_url.username is not None or parsed_url.query or parsed_url.fragment or not (
        parsed_url.scheme == "https" or parsed_url.scheme == "http" and
        parsed_url.hostname in ("127.0.0.1", "::1", "localhost")
    ):
        parser.error("Use an HTTPS ORS base URL, or an HTTP loopback URL for a local instance.")
    index = ROOT / "data/demo/ors/index.json"
    entries = json.loads(index.read_text(encoding="utf-8"))
    opener = build_opener(NoRedirect)
    args.output.mkdir(parents=True, exist_ok=True)
    for expected, entry in entries.items():
        profile = entry["profile"]
        body = json.dumps(entry["request"], separators=(",", ":")).encode()
        actual = sha256(profile.encode() + b"\n" + body).hexdigest()
        if actual != expected:
            raise ValueError(f"Request {expected} no longer matches its normalized identity.")
        path = args.output / f"{actual}.json"
        if path.exists():
            existing = json.loads(path.read_text(encoding="utf-8"))
            if existing.get("mode") == "recorded":
                continue
            raise SystemExit(f"The destination already contains a non-recorded fixture {actual}. Use a fresh directory.")
        request = Request(f"{base_url}/v2/directions/{profile}/geojson", data=body,
                          headers={"Authorization": key, "Content-Type": "application/json"})
        try:
            with opener.open(request, timeout=20) as response:
                result = json.load(response)
        except HTTPError as error:
            payload = json.load(error)
            if error.code not in (400, 404) or payload.get("error", {}).get("code") not in (2009, 2010):
                raise SystemExit(f"ORS returned HTTP {error.code} for {actual}.") from None
            result = {"type": "FeatureCollection", "features": []}
        except URLError as error:
            raise SystemExit(f"ORS is unavailable: {error.reason}") from None
        if result.get("type") != "FeatureCollection" or not isinstance(result.get("features"), list):
            raise SystemExit(f"ORS returned an unexpected response for {actual}.")
        write_recording(path, result)
        print(actual, entry["scenario"], entry["leg"])


if __name__ == "__main__":
    main()
