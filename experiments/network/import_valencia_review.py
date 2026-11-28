"""Send a manual FlyPix browser export to the local Valencia review queue."""

import argparse
from pathlib import Path
from urllib.error import URLError

from local_evidence import LOCAL, ensure_area, import_layer, load_json, local_base, request, timestamp


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    default = Path(__file__).resolve().parents[2] / "artifacts/valencia-evidence/manifest.json"
    parser.add_argument("manifest", type=Path, nargs="?", default=default)
    parser.add_argument("--demo", default="http://127.0.0.1:5080")
    args = parser.parse_args()

    try:
        config = load_json(args.manifest)["flypix"]
        mode = config.get("mode", "manual")
        if mode not in {"manual", "recorded", "synthetic"}:
            raise ValueError("Use manual, recorded or synthetic for a browser export.")
        geojson = load_json(args.manifest.resolve().parent / config["geoJson"])
        payload = {
            "layerId": config["layerId"], "imageProductId": config["imageProductId"],
            "observedAt": timestamp(config["observedAt"]), "mode": mode,
            "modelId": config.get("modelId"), "geoJson": geojson,
        }
        base = local_base(args.demo)
        network = request(LOCAL, base + "/api/routing/queries/network")
        if network["scenario"] != "valencia":
            raise ValueError("Launch the Valencia demo first.")
        ensure_area(geojson, network["city"]["bounds"], "Valencia")
        import_layer(base, "import-flypix", payload)
        print(f"{len(geojson['features'])} polygon(s) submitted for operator review.")
    except (ValueError, URLError, OSError, KeyError, TypeError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
