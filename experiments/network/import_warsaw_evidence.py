"""Import a clipped Warsaw Copernicus package and FlyPix evidence into the local demo."""

import argparse
import base64
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import time
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode, urlsplit
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener


FLYPIX = "https://api.flypix.ai"
LIMIT = 8 * 1024 * 1024
ROOT = Path(__file__).resolve().parents[2]
AOI = json.loads((ROOT / "data/demo/scenario.json").read_text(encoding="utf-8"))["evidenceAoi"]


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


LOCAL = build_opener(ProxyHandler({}), NoRedirect())


def setting(name):
    if name in os.environ:
        return os.environ[name].strip()
    env_file = ROOT / ".env"
    if env_file.is_file():
        for line in env_file.read_text(encoding="utf-8-sig").splitlines():
            prefix, separator, value = line.strip().removeprefix("export ").partition("=")
            if separator and prefix.strip() == name:
                value = value.split(" #", 1)[0].strip()
                if len(value) >= 2 and value[0] in "\"'" and value[-1] == value[0]:
                    return value[1:-1]
                return value
    return ""


class FlyPixHttpError(ValueError):
    def __init__(self, status, path):
        self.status = status
        details = {
            401: "The API rejected FLYPIX_API_KEY. Check that the key is valid for this FlyPix tenant.",
            403: "FlyPix denied access. Check tenant permissions and FlyPix API availability.",
            402: "FlyPix rejected the operation because of the account balance.",
        }
        super().__init__(f"FlyPix HTTP {status} at {path}. {details.get(status, '')}".rstrip())


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
        if opener is LOCAL:
            message = error.reason
            if error.headers.get_content_type() == "application/problem+json":
                message = json.loads(error.read(LIMIT)).get("detail", message)
            raise ValueError(f"Local import returned HTTP {error.code}: {message}") from None
        raise ValueError(f"FlyPix returned HTTP {error.code} for {urlsplit(url).path}") from None
    if len(data) > LIMIT:
        raise ValueError("The API response exceeds 8 MiB.")
    return json.loads(data)


def flypix(path, payload=None, headers=None, timeout=25):
    import requests

    key = setting("FLYPIX_API_KEY")
    if not key:
        raise ValueError("Set FLYPIX_API_KEY in .env or the current shell to use the FlyPix API.")
    try:
        with requests.request("POST" if payload is not None else "GET", FLYPIX + path,
                              json=payload if isinstance(payload, dict) else None,
                              data=None if isinstance(payload, dict) else payload,
                              headers={"Accept": "application/json", "ApiKey": key, **(headers or {})},
                              timeout=timeout, allow_redirects=False, stream=True) as response:
            if response.status_code not in range(200, 300):
                raise FlyPixHttpError(response.status_code, urlsplit(path).path)
            data = response.raw.read(8 * LIMIT + 1, decode_content=True)
            if len(data) > 8 * LIMIT:
                raise ValueError("The FlyPix response exceeds 64 MiB. Use a smaller imagery tile.")
            return json.loads(data)
    except requests.RequestException:
        raise ValueError("Could not connect to the FlyPix API.") from None


def api_id(value):
    if not isinstance(value, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,120}", value):
        raise ValueError("Invalid FlyPix resource ID.")
    return quote(value, safe="")


def upload_raster(config, directory):
    path = directory / config["geoTiff"]
    if path.suffix.lower() not in {".tif", ".tiff"}:
        raise ValueError("Provide a georeferenced .tif or .tiff image.")
    query = urlencode({"tenant_id": api_id(config["tenantId"]), "project_id": api_id(config["projectId"]),
                       "filename": path.name})
    with path.open("rb") as stream:
        result = flypix("/v2/files/upload?" + query, stream,
                        {"Content-Type": "application/octet-stream",
                         "Content-Length": str(os.fstat(stream.fileno()).st_size)}, timeout=180)
    print(f"FlyPix raster ID: {result['raster_id']} (status {result['status']}). "
          "Set flypix.rasterId after processing.", flush=True)
    return result["raster_id"]


def wait_for_inference(inference_id, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = flypix("/v2/models/inference/" + api_id(inference_id))
        state = result["status"].lower()
        if state in {"completed", "success", "succeeded", "finished", "done", "ready"}:
            return result
        if state in {"failed", "error", "cancelled", "canceled", "aborted"}:
            raise ValueError(f"FlyPix inference {inference_id} ended with status {state}.")
        time.sleep(3)
    raise ValueError(f"FlyPix inference {inference_id} timed out. Resume with --inference-id {inference_id}.")


def feature_geometry(text):
    if text.lstrip().startswith("{"):
        return json.loads(text)

    try:
        from shapely import from_wkb, from_wkt, get_srid
        from shapely.geometry import mapping
    except ImportError:
        raise ValueError("Install Shapely for FlyPix WKB/WKT polygons, or use a GeoJSON export.") from None

    if text.upper().lstrip().startswith(("POLYGON", "MULTIPOLYGON")):
        geometry = from_wkt(text)
    else:
        try:
            raw = bytes.fromhex(text.removeprefix("0x").removeprefix("\\x"))
        except ValueError:
            raw = base64.b64decode(text, validate=True)
        geometry = from_wkb(raw)
    if get_srid(geometry) not in {0, 4326}:
        raise ValueError("FlyPix geometry must use EPSG:4326. Export or reproject it as GeoJSON.")
    return mapping(geometry)


def vector_geojson(vector_id, class_ids):
    result = flypix("/v2/vectors/" + api_id(vector_id) + "/features")
    data = result["data"]
    polygons = data["polygons"] or []
    ids = data.get("ids")
    scores = data.get("confidences")
    classes = data.get("class_ids")
    if any(values is not None and len(values) != len(polygons) for values in (ids, scores, classes)):
        raise ValueError("FlyPix returned inconsistent vector feature arrays.")
    if class_ids and classes is None and polygons:
        raise ValueError("The FlyPix response has no class IDs to filter.")
    features = []
    for index, polygon in enumerate(polygons):
        class_id = classes[index] if classes is not None else None
        if class_ids and str(class_id) not in class_ids:
            continue
        features.append({
            "type": "Feature", "id": ids[index] if ids is not None else index,
            "properties": {"confidence": scores[index] if scores is not None else None, "classId": class_id},
            "geometry": feature_geometry(polygon),
        })
    return {"type": "FeatureCollection", "features": features}


def load_json(path):
    with path.open(encoding="utf-8-sig") as stream:
        return json.load(stream)


def timestamp(value):
    observed = datetime.fromisoformat(value)
    if observed.utcoffset() is None:
        raise ValueError("observedAt requires a timezone, preferably UTC with a Z suffix.")
    return observed.isoformat()


def ensure_warsaw(geojson, empty_ok=False):
    features = geojson["features"]
    if geojson["type"] != "FeatureCollection" or not isinstance(features, list) or (not features and not empty_ok):
        raise ValueError("Provide a GeoJSON FeatureCollection of polygons clipped to the Warsaw AOI.")
    west, south, east, north = AOI
    for feature in features:
        geom = feature["geometry"]
        if geom["type"] not in ("Polygon", "MultiPolygon"):
            raise ValueError("Only Polygon and MultiPolygon are supported.")
        polygons = [geom["coordinates"]] if geom["type"] == "Polygon" else geom["coordinates"]
        for polygon in polygons:
            for ring in polygon:
                for x, y, *_ in ring:
                    if not (west - 0.000001 <= x <= east + 0.000001 and south - 0.000001 <= y <= north + 0.000001):
                        raise ValueError("Coordinates are outside the Warsaw AOI. Clip to the AOI in WGS 84 (lon, lat).")


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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", type=Path)
    parser.add_argument("--demo", default="http://127.0.0.1:5080")
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--vector-id", help="Read an existing FlyPix result without running a model")
    source.add_argument("--inference-id", help="Resume polling an existing inference")
    source.add_argument("--submit", action="store_true", help="Apply the model to the uploaded raster (uses credits)")
    source.add_argument("--upload-raster", action="store_true", help="Upload the manifest's GeoTIFF to the FlyPix project")
    parser.add_argument("--save", type=Path, help="Save the fetched FlyPix FeatureCollection for offline replay")
    parser.add_argument("--timeout", type=int, default=600)
    args = parser.parse_args()
    try:
        manifest = load_json(args.manifest)
        directory = args.manifest.resolve().parent
        config = manifest.get("flypix") or {}
        if args.upload_raster:
            upload_raster(config, directory)
            return
        base = local_base(args.demo)
        network = request(LOCAL, base + "/api/routing/queries/network")
        if network["scenario"] != "warsaw":
            raise ValueError("Start the Warsaw scenario with --scenario warsaw.")
        hazards = []
        for hazard in manifest.get("hazards", []):
            geojson = load_json(directory / hazard["geoJson"])
            ensure_warsaw(geojson)
            hazards.append({
                "source": hazard["source"], "sourceUrl": hazard["sourceUrl"], "productId": hazard["productId"],
                "observedAt": timestamp(hazard["observedAt"]), "geoJson": geojson,
            })

        review = None
        if args.vector_id or args.inference_id or args.submit or config.get("geoJson"):
            observed_at = timestamp(config["observedAt"])
            image_product = config["imageProductId"]
            model = config.get("modelId")
            vector = args.vector_id
            inference = args.inference_id
            if args.submit:
                result = flypix("/v2/models/" + api_id(model) + "/apply",
                                {"raster_id": api_id(config["rasterId"])})
                inference = result["inference_id"]
                print(f"FlyPix inference: {inference}. Keep this ID to resume without submitting again.", flush=True)
            if inference:
                result = wait_for_inference(inference, args.timeout)
                data = result.get("data") or {}
                vector = data.get("vector_id") or config.get("vectorId")
                if not vector:
                    raise ValueError("Inference finished. Copy its result layer ID and rerun with --vector-id.")
            if vector:
                geojson = vector_geojson(vector, {str(item) for item in config.get("classIds", [])})
                layer_id, mode = vector, "live"
            else:
                geojson = load_json(directory / config["geoJson"])
                layer_id, mode = config["layerId"], config.get("mode", "recorded")
            ensure_warsaw(geojson, empty_ok=True)
            if args.save:
                args.save.write_text(json.dumps(geojson, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            review = {"layerId": layer_id, "imageProductId": image_product, "observedAt": observed_at,
                      "mode": mode, "modelId": model, "geoJson": geojson}

        if not hazards and review is None:
            raise ValueError("The manifest contains no layer to import.")
        for payload in hazards:
            import_layer(base, "import-copernicus", payload)
        if review is not None:
            import_layer(base, "import-flypix", review)
            print(f"{len(review['geoJson']['features'])} detection(s) submitted for operator review.")
    except (ValueError, URLError, OSError, KeyError, TypeError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
