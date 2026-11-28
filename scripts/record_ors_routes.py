"""Capture ORS GeoJSON responses for the normalized exercise requests in index.json."""

from argparse import ArgumentParser
from datetime import datetime, timezone
from hashlib import sha256
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import HTTPRedirectHandler, Request, build_opener
import json
import os


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def main():
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="Destination for the recorded responses")
    args = parser.parse_args()
    key = os.environ.get("Integrations__Ors__ApiKey")
    if not key:
        parser.error("Set Integrations__Ors__ApiKey first.")
    index = Path(__file__).resolve().parents[1] / "data/demo/ors/index.json"
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
            continue
        request = Request(f"https://api.openrouteservice.org/v2/directions/{profile}/geojson", data=body,
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
        fixture = {"mode": "recorded", "observedAt": datetime.now(timezone.utc).isoformat(), "response": result}
        temporary = path.with_suffix(".tmp")
        temporary.write_text(json.dumps(fixture, separators=(",", ":")) + "\n", encoding="utf-8")
        temporary.replace(path)
        print(actual, entry["scenario"], entry["leg"])


if __name__ == "__main__":
    main()
