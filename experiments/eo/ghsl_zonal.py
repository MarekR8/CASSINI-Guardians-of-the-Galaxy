"""Estimate residential population inside a WGS 84 flood polygon from GHS-POP."""

import argparse
import json
import math
from pathlib import Path

import numpy as np
import rasterio
from rasterio.errors import RasterioError
from rasterio.features import geometry_window
from rasterio.mask import raster_geometry_mask
from rasterio.warp import transform_geom


def estimate_population(raster, flood):
    collection = json.loads(flood.read_text(encoding="utf-8-sig"))
    if collection["type"] != "FeatureCollection":
        raise ValueError("Provide a WGS 84 GeoJSON FeatureCollection.")
    polygons = [
        feature["geometry"] for feature in collection["features"]
        if feature.get("geometry") and feature["geometry"]["type"] in {"Polygon", "MultiPolygon"}
    ]
    if not polygons:
        raise ValueError("The flood layer contains no polygons.")

    with rasterio.open(raster) as image:
        if image.crs is None:
            raise ValueError("The population raster must have a CRS.")
        projected = [transform_geom("EPSG:4326", image.crs, polygon) for polygon in polygons]
        window = geometry_window(image, projected, boundless=True)
        if (window.col_off < 0 or window.row_off < 0 or
                window.col_off + window.width > image.width or window.row_off + window.height > image.height):
            raise ValueError("The population raster must cover the entire flood layer.")
        outside, _, window = raster_geometry_mask(image, projected, crop=True)
        values = image.read(1, window=window, masked=True)
        selected = values.data[(~outside) & (~np.ma.getmaskarray(values))]
        selected = selected[np.isfinite(selected) & (selected >= 0)]
        if selected.size == 0:
            raise ValueError("No valid GHSL cells have their centres inside the flood layer.")
        return math.floor(selected.sum(dtype=np.float64) + 0.5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("raster", type=Path)
    parser.add_argument("flood", type=Path)
    parser.add_argument("--year", type=int, required=True, choices=range(1975, 2031, 5))
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        result = {
            "value": estimate_population(args.raster, args.flood),
            "method": "sum of GHS-POP cells with centres inside the flood polygon",
            "source": "Copernicus GHSL GHS-POP R2023A",
            "year": args.year,
        }
        serialized = json.dumps(result, indent=2) + "\n"
        if args.output:
            args.output.write_text(serialized, encoding="utf-8")
        else:
            print(serialized, end="")
    except (KeyError, OSError, TypeError, ValueError, RasterioError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
