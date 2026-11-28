"""Read local evidence and submit it to the Situation service."""

from datetime import datetime
import hashlib
import json
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener


LIMIT = 8 * 1024 * 1024


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


LOCAL = build_opener(ProxyHandler({}), NoRedirect())


def request(opener, url, payload=None, headers=None, timeout=15):
    headers = {"Accept": "application/json", **(headers or {})}
    if isinstance(payload, dict):
        payload = json.dumps(payload, ensure_ascii=False, allow_nan=False).encode()
        if len(payload) >= LIMIT:
            raise ValueError("The import exceeds the local 8 MiB limit. Clip or simplify the GeoJSON.")
        headers["Content-Type"] = "application/json"
    try:
        with opener.open(Request(url, payload, headers), timeout=timeout) as response:
            data = response.read(LIMIT + 1)
    except HTTPError as error:
        message = error.reason
        if error.headers.get_content_type() == "application/problem+json":
            message = json.loads(error.read(LIMIT)).get("detail", message)
        raise ValueError(f"Local import returned HTTP {error.code}: {message}") from None
    if len(data) > LIMIT:
        raise ValueError("The API response exceeds 8 MiB.")
    return json.loads(data)


def load_json(path):
    with path.open(encoding="utf-8-sig") as stream:
        return json.load(stream)


def timestamp(value):
    observed = datetime.fromisoformat(value)
    if observed.utcoffset() is None:
        raise ValueError("observedAt requires a timezone, preferably UTC with a Z suffix.")
    return observed.isoformat()


def ensure_area(geojson, area, label, empty_ok=False):
    features = geojson["features"]
    if geojson["type"] != "FeatureCollection" or not isinstance(features, list) or (not features and not empty_ok):
        raise ValueError(f"Provide a GeoJSON FeatureCollection of polygons clipped to the {label} AOI.")
    west, south, east, north = area
    for feature in features:
        geom = feature["geometry"]
        if geom["type"] not in ("Polygon", "MultiPolygon"):
            raise ValueError("Only Polygon and MultiPolygon are supported.")
        polygons = [geom["coordinates"]] if geom["type"] == "Polygon" else geom["coordinates"]
        for polygon in polygons:
            for ring in polygon:
                for x, y, *_ in ring:
                    if not (west - 0.000001 <= x <= east + 0.000001 and south - 0.000001 <= y <= north + 0.000001):
                        raise ValueError(f"Coordinates are outside the {label} AOI. Clip to the AOI in WGS 84 (lon, lat).")


def import_layer(base, operation, payload):
    snapshot = request(LOCAL, base + "/api/situation/queries/snapshot")
    body = {**payload, "expectedRevision": snapshot["revision"]}
    encoded = json.dumps(body, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode()
    body["commandId"] = operation + "-" + hashlib.sha256(encoded).hexdigest()
    result = request(LOCAL, base + "/api/situation/commands/" + operation, body)
    print(f"{operation}: situation revision {result['revision']}", flush=True)


def local_base(value):
    parsed = urlsplit(value)
    if (parsed.scheme != "http" or parsed.hostname not in {"localhost", "127.0.0.1", "::1"}
            or parsed.username or parsed.password or parsed.path not in {"", "/"} or parsed.query or parsed.fragment):
        raise ValueError("The demo URL must be local HTTP, for example http://127.0.0.1:5080.")
    return value.rstrip("/")
