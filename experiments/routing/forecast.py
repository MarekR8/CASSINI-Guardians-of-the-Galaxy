"""T_safe per shelter from a hazard forecast (task 03 Stage B), the reference for src/Routing/Evacuation/HazardForecast.cs.

A forecast file (cassini.hazard-forecast.v1) gives discharge series at river points and, per shelter, the point and
threshold at which the site or its last approach floods. T_safe is the first time the discharge reaches the threshold,
interpolated linearly between samples, in minutes after the plan start. A group may only choose a shelter it reaches
by T_safe.

    python experiments/routing/forecast.py                  # T_safe of the bundled synthetic Valencia forecast
"""

import argparse
from datetime import datetime
import json
from pathlib import Path

from graph import ROOT

MOCK = ROOT / "data/demo/routing/valencia/glofas-forecast.mock.json"
VERSION = "cassini.hazard-forecast.v1"


def parse_time(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def load(path=MOCK):
    document = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if document.get("forecastVersion") != VERSION:
        raise ValueError(f"forecastVersion must be {VERSION}")
    points = {point["pointId"] for point in document["points"]}
    for item in document["exposure"]:
        if item["pointId"] not in points or item["thresholdM3s"] <= 0:
            raise ValueError("Each exposure needs a known pointId and a positive thresholdM3s.")
    return document


def crossing(series, threshold):
    """First time the discharge reaches the threshold, or None within the series."""
    samples = sorted(((parse_time(item["time"]), item["dischargeM3s"]) for item in series), key=lambda item: item[0])
    for index, (time, discharge) in enumerate(samples):
        if discharge >= threshold:
            if index == 0:
                return time
            before_time, before = samples[index - 1]
            return before_time + (time - before_time) * ((threshold - before) / (discharge - before))
    return None


def evaluate(document, shelter_ids, plan_start=None):
    """Per shelter: status, threshold, peak, unsafeFrom and safeUntilMinutes (None = no limit)."""
    start = parse_time(plan_start or document["planStart"])
    points = {point["pointId"]: point for point in document["points"]}
    exposure = {item["shelterId"]: item for item in document["exposure"]}
    result = {}
    for shelter in shelter_ids:
        item = exposure.get(shelter)
        if item is None:
            result[shelter] = {"status": "NotAssessed", "safeUntilMinutes": None}
            continue
        series = points[item["pointId"]]["series"]
        peak = max((sample["dischargeM3s"] for sample in series), default=None)
        when = crossing(series, item["thresholdM3s"])
        entry = {"pointId": item["pointId"], "thresholdM3s": item["thresholdM3s"], "peakM3s": peak}
        if when is None:
            result[shelter] = {**entry, "status": "SafeThroughHorizon", "safeUntilMinutes": None}
            continue
        minutes = max(0.0, (when - start).total_seconds() / 60)
        result[shelter] = {**entry, "status": "SafeUntil" if minutes > 0 else "Unsafe",
                           "unsafeFrom": when.isoformat().replace("+00:00", "Z"), "safeUntilMinutes": round(minutes, 6)}
    return result


def apply(shelters, safety):
    """Shelter dicts with safeUntilMinutes from the forecast, as read by model.Model."""
    return [{**shelter, "safeUntilMinutes": safety.get(shelter["id"], {}).get("safeUntilMinutes")} for shelter in shelters]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--forecast", type=Path, default=MOCK)
    parser.add_argument("--shelters", type=Path, default=ROOT / "data/demo/routing/valencia/shelters.synthetic.json")
    args = parser.parse_args()
    document = load(args.forecast)
    ids = [item["id"] for item in json.loads(args.shelters.read_text(encoding="utf-8-sig"))["shelters"]]
    print(f"{document['source']} ({document['label']}), plan start {document['planStart']}")
    for shelter, item in evaluate(document, ids).items():
        limit = "no limit" if item["safeUntilMinutes"] is None else f"T_safe {item['safeUntilMinutes']:.1f} min"
        print(f"  {shelter}: {item['status']}, {limit}")


if __name__ == "__main__":
    main()
