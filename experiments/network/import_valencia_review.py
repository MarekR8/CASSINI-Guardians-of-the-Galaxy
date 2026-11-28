"""Send a FlyPix browser export or the offline API mock to Valencia's review queue."""

import argparse
from pathlib import Path
from urllib.error import URLError

from local_evidence import LOCAL, ensure_area, import_layer, load_json, local_base, request, timestamp


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    default = Path(__file__).resolve().parents[2] / "artifacts/valencia-evidence/manifest.json"
    source = parser.add_mutually_exclusive_group()
    source.add_argument("manifest", type=Path, nargs="?")
    source.add_argument("--mock", action="store_true", help="Fetch the synthetic FlyPix response from the local API")
    parser.add_argument("--demo", default="http://127.0.0.1:5080")
    args = parser.parse_args()

    try:
        base = local_base(args.demo)
        network = request(LOCAL, base + "/api/routing/queries/network")
        if network["scenario"] != "valencia":
            raise ValueError("Launch the Valencia demo first.")
        if args.mock:
            config = request(LOCAL, base + "/api/situation/queries/integration-flypix")
            if config["mode"] != "mock":
                raise ValueError("The local preview did not return a mock FlyPix result.")
            mode, geojson = "synthetic", config["geoJson"]
        else:
            manifest = args.manifest or default
            config = load_json(manifest)["flypix"]
            mode = config.get("mode", "manual")
            if mode not in {"manual", "recorded", "synthetic"}:
                raise ValueError("Use manual, recorded or synthetic for a browser export.")
            geojson = load_json(manifest.resolve().parent / config["geoJson"])
        ensure_area(geojson, network["city"]["bounds"], "Valencia")
        payload = {
            "layerId": config["layerId"], "imageProductId": config["imageProductId"],
            "observedAt": timestamp(config["observedAt"]), "mode": mode,
            "modelId": config.get("modelId"), "geoJson": geojson,
        }
        import_layer(base, "import-flypix", payload)
        print(f"{len(geojson['features'])} polygon(s) submitted for operator review.")
    except (ValueError, URLError, OSError, KeyError, TypeError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
