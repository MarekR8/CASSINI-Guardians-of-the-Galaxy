"""Sync the Warsaw CDSE/file imagery, FlyPix inference and the local Situation service."""

import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import time
from urllib.error import URLError

from copernicus import hazard_geometry, imagery_tiles
from import_warsaw_evidence import (
    AOI, LOCAL, LIMIT, FlyPixHttpError, api_id, ensure_warsaw, flypix, import_layer, load_json,
    local_base, request, setting, timestamp, upload_raster, vector_geojson, wait_for_inference,
)


def check_account():
    tenants = flypix("/v2/users/tenants/for-user/")
    print("FlyPix API: account lookup succeeded.", flush=True)
    return tenants


def apply_config(config, key, environment):
    return config.get(key) or setting(environment)


def get_vectors(raster_id):
    return {item["vector_id"] for item in flypix("/v2/vectors/for-raster/" + api_id(raster_id))}


def wait_raster(raster_id, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        status = flypix("/v2/rasters/" + api_id(raster_id))["status"].upper()
        if status == "PROCESSED":
            return
        if status in {"FAILED", "ERROR", "CANCELED", "CANCELLED"}:
            raise ValueError(f"FlyPix raster processing failed for {raster_id}: {status}.")
        time.sleep(3)
    raise ValueError(f"FlyPix raster {raster_id} is still processing. Run the same command to resume.")


def result_vectors(result, raster_id, previous, timeout):
    data = result.get("data") or {}
    direct = data.get("vector_id")
    vector_ids = [direct] if isinstance(direct, str) else data.get("vector_ids")
    if isinstance(vector_ids, list) and vector_ids and all(isinstance(value, str) for value in vector_ids):
        return list(dict.fromkeys(vector_ids))

    deadline = time.monotonic() + min(timeout, 90)
    while time.monotonic() < deadline:
        added = get_vectors(raster_id) - set(previous)
        if len(added) == 1:
            return sorted(added)
        if len(added) > 1:
            break
        time.sleep(3)
    raise ValueError("Could not link a unique result layer to the inference. "
                     "Set vectorIds in the job checkpoint after checking the result in FlyPix.")


def write_json(path, data):
    partial = path.with_suffix(path.suffix + ".tmp")
    partial.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    partial.replace(path)


@contextmanager
def checkpoint(directory):
    lock = directory / ".sync.lock"
    try:
        descriptor = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        raise ValueError(f"Sync is already running. If it ended unexpectedly, remove {lock} after checking FlyPix.") from None
    os.close(descriptor)
    try:
        yield
    finally:
        lock.unlink()


def get_job_key(img, cfg):
    with Path(img["geoTiff"]).open("rb") as raster:
        file_hash = hashlib.file_digest(raster, "sha256").hexdigest()
    identity = {key: cfg[key] for key in ("tenantId", "projectId", "modelId")}
    identity.update(imageProductId=img["imageProductId"], observedAt=img["observedAt"], digest=file_hash)
    return hashlib.sha256(json.dumps(identity, sort_keys=True).encode()).hexdigest()


def submit_once(job, field, operation, persist):
    pending = field + "Pending"
    if field in job:
        return job[field]
    if job.get(pending):
        raise ValueError(f"{field} request outcome is unknown. Check FlyPix and record the resource ID in the checkpoint.")
    job[pending] = True
    persist()
    try:
        job[field] = operation()
    except FlyPixHttpError as error:
        if error.status in {400, 401, 402, 403, 404, 413, 415, 422, 429}:
            job.pop(pending)
            persist()
        raise
    job.pop(pending)
    persist()
    return job[field]


def run_inference(img, cfg, job, persist, timeout):
    raster_id = submit_once(job, "rasterId", lambda: upload_raster({**cfg, **img}, Path()), persist)
    if "vectorIds" in job:
        return job["vectorIds"]
    wait_raster(raster_id, timeout)
    if "previousVectors" not in job:
        job["previousVectors"] = sorted(get_vectors(raster_id))
        persist()
    inference_id = submit_once(job, "inferenceId", lambda: flypix(
        "/v2/models/" + api_id(cfg["modelId"]) + "/apply", {"raster_id": raster_id}
    )["inference_id"], persist)
    print(f"FlyPix inference {inference_id}", flush=True)
    result = wait_for_inference(inference_id, timeout)
    job["vectorIds"] = result_vectors(result, raster_id, job["previousVectors"], timeout)
    persist()
    return job["vectorIds"]


def clip_detections(collection, minimum):
    from shapely import make_valid
    from shapely.geometry import box, mapping, shape

    bbox = box(*AOI)
    clipped = []
    for feature in collection["features"]:
        score = feature["properties"].get("confidence")
        if minimum and (score is None or score < minimum):
            continue
        geom = shape(feature["geometry"])
        if geom.is_empty or not geom.intersects(bbox):
            continue
        geom = make_valid(geom).intersection(bbox).simplify(.000003, preserve_topology=True)
        if geom.geom_type in {"Polygon", "MultiPolygon"} and not geom.is_empty:
            clipped.append({**feature, "geometry": mapping(geom)})
    return {"type": "FeatureCollection", "features": clipped}


def position_count(geometry):
    points = geometry["coordinates"]
    return sum(len(ring) for polygon in (points if geometry["type"] == "MultiPolygon" else [points])
               for ring in polygon)


def batches(features):
    group, size, points = [], 0, 0
    for feature in features:
        length = len(json.dumps(feature, ensure_ascii=False).encode())
        vertices = position_count(feature["geometry"])
        if length >= LIMIT - 16384 or vertices > 100_000:
            raise ValueError("A single geometry exceeds the import limit. Simplify it before importing.")
        if group and (len(group) == 1000 or size + length >= LIMIT - 16384 or points + vertices > 100_000):
            yield {"type": "FeatureCollection", "features": group}
            group, size, points = [], 0, 0
        group.append(feature)
        size += length + 2
        points += vertices
    if group:
        yield {"type": "FeatureCollection", "features": group}


def import_hazards(base, manifest, directory):
    for hazard in manifest.get("hazards", []):
        geojson = hazard_geometry(hazard, directory)
        ensure_warsaw(geojson)
        import_layer(base, "import-copernicus", {
            "source": hazard["source"], "sourceUrl": hazard["sourceUrl"], "productId": hazard["productId"],
            "observedAt": timestamp(hazard["observedAt"]), "geoJson": geojson,
        })


def synchronize(manifest_path, base, timeout, hazards_only):
    manifest = load_json(manifest_path)
    directory = manifest_path.parent
    network = request(LOCAL, base + "/api/routing/queries/network")
    if not network.get("city") or network["city"]["bounds"] != AOI:
        raise ValueError("Prepare the full Warsaw OSM extract, then restart the Warsaw demo.")
    if hazards_only:
        import_hazards(base, manifest, directory)
        return
    config = manifest["flypix"]
    cfg = {key: apply_config(config, key, name) for key, name in {
        "tenantId": "FLYPIX_TENANT_ID", "projectId": "FLYPIX_PROJECT_ID", "modelId": "FLYPIX_MODEL_ID",
    }.items()}
    check_account()
    missing = [key for key, value in cfg.items() if not value]
    if missing:
        raise ValueError("Set the FlyPix " + ", ".join(missing) + " in the local manifest or .env.")
    for value in cfg.values():
        api_id(value)
    minimum = config.get("minConfidence", 0)
    if not isinstance(minimum, (int, float)) or not 0 <= minimum <= 1:
        raise ValueError("flypix.minConfidence must be between 0 and 1.")
    class_ids = {str(item) for item in config.get("classIds", [])}
    with checkpoint(directory):
        images = list(imagery_tiles(manifest["imagery"], directory))
        if not images:
            raise ValueError("No GeoTIFF input was provided.")
        state_path = directory / "flypix-state.json"
        state = load_json(state_path) if state_path.is_file() else {}
        import_hazards(base, manifest, directory)
        for img in images:
            job_id = get_job_key(img, cfg)
            job = state.setdefault(job_id, {"imageProductId": img["imageProductId"], "file": Path(img["geoTiff"]).name})
            persist = lambda: write_json(state_path, state)
            for vector_id in run_inference(img, cfg, job, persist, timeout):
                geojson = clip_detections(vector_geojson(vector_id, class_ids), minimum)
                path = directory / "flypix" / (api_id(vector_id) + ".geojson")
                path.parent.mkdir(exist_ok=True)
                write_json(path, geojson)
                for batch in batches(geojson["features"]):
                    import_layer(base, "import-flypix", {
                        "layerId": vector_id, "imageProductId": img["imageProductId"], "observedAt": img["observedAt"],
                        "mode": "live", "modelId": cfg["modelId"], "geoJson": batch,
                    })
                print(f"FlyPix {vector_id}: {len(geojson['features'])} detections for operator review.", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest", nargs="?", type=Path)
    parser.add_argument("--check-api", action="store_true", help="Check the FlyPix API key without uploading or using credits")
    parser.add_argument("--hazards-only", action="store_true", help="Import the supplied Copernicus polygons without FlyPix")
    parser.add_argument("--prepare-imagery", action="store_true", help="Validate and prepare GeoTIFFs without FlyPix or the demo")
    parser.add_argument("--demo", default="http://127.0.0.1:5080")
    parser.add_argument("--timeout", type=int, default=1800)
    args = parser.parse_args()
    try:
        if args.check_api:
            check_account()
        elif args.manifest and args.prepare_imagery:
            path = args.manifest.resolve()
            images = list(imagery_tiles(load_json(path)["imagery"], path.parent))
            print(f"Copernicus: {len(images)} GeoTIFF tile(s) prepared.")
        elif args.manifest:
            synchronize(args.manifest.resolve(), local_base(args.demo), args.timeout, args.hazards_only)
        else:
            parser.error("provide a manifest or --check-api")
    except (ValueError, KeyError, TypeError, URLError, OSError, ImportError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
