"""Exercise the full Warsaw OSM extract and city segment closure through the local API."""

import json
import sys
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import ProxyHandler, Request, build_opener


BASE = sys.argv[1]
HTTP = build_opener(ProxyHandler({}))


def call(path, body=None, expected=200):
    data = json.dumps(body).encode() if body is not None else None
    headers = {"Content-Type": "application/json"} if body is not None else {}
    try:
        response = HTTP.open(Request(BASE + path, data, headers), timeout=25)
    except HTTPError as error:
        response = error
    with response:
        result = response.read()
        assert response.status == expected, (path, response.status, result[:500])
        return json.loads(result) if "json" in response.headers["Content-Type"] else result


def city_route(start, end, mode):
    query = urlencode({"from": start, "to": end, "mode": mode})
    return call("/api/routing/queries/city-route?" + query)


network = call("/api/routing/queries/network")
city = network["city"]
assert city["bounds"] == [20.830, 52.080, 21.300, 52.390]
assert city["roadCount"] > 10_000 and city["osmTimestamp"]
svg = call("/maps/warsaw.svg")
assert svg.startswith(b'<svg') and len(svg) > 100_000
assert b'id="city-tools"' in call("/")

# Both trips are well outside the original bridge demonstration.
west_trip = ("21.004,52.230", "21.010,52.232")
east_trip = ("21.140,52.250", "21.151,52.253")
west = city_route(*west_trip, "walking")
east = city_route(*east_trip, "driving")
assert west["route"] and east["route"], (west, east)
assert west["route"]["seconds"] > 60 and east["route"]["seconds"] > 60
assert all(item.startswith("osm-") for item in west["route"]["segments"])
assert call("/api/routing/queries/city-route?from=19,51&to=21,52.2", expected=400)["code"] == "InvalidPoint"

points = east["route"]["geometry"]
lon, lat = points[len(points) // 2]
span = .00016
ring = [[lon-span, lat-span], [lon+span, lat-span], [lon+span, lat+span],
        [lon-span, lat+span], [lon-span, lat-span]]
payload = {
    "commandId": "city-detection", "expectedRevision": 0, "layerId": "city-fixture",
    "imageProductId": "city-test-raster", "observedAt": "2026-11-27T18:00:00Z", "mode": "synthetic",
    "geoJson": {"type": "FeatureCollection", "features": [
        {"type": "Feature", "geometry": {"type": "Polygon", "coordinates": [ring]},
         "properties": {"confidence": .85}}
    ]},
}
imported = call("/api/situation/commands/import-flypix", payload)
review = next(feature for feature in imported["reviewFeatures"] if feature["layerId"] == "city-fixture")
common = set(review["segmentIds"]).intersection(east["route"]["segments"])
assert common, review["segmentIds"]
assert not imported["closedSegments"]
assert city_route(*east_trip, "driving")["route"] == east["route"]
approved = call("/api/situation/commands/review-decision", {
    "commandId": "city-decision", "expectedRevision": imported["revision"],
    "reviewFeatureId": review["id"], "decision": "approve",
})
assert common <= set(approved["closedSegments"])
revised = city_route(*east_trip, "driving")
assert revised["revision"] == approved["revision"]
if revised["route"]:
    assert not set(revised["route"]["segments"]).intersection(approved["closedSegments"])
assert city_route(*west_trip, "walking")["route"] == west["route"]
validation = call("/api/routing/queries/validate-route?" + urlencode({"segments": sorted(common)[0]}))
assert validation["reason"] == "ClosedSegment"
print(f"Warsaw city integration passed: {city['roadCount']:,} OSM sections, west and east routes, city closure and revision.")
