# Sample road network

[`network.json`](network.json) is a synthetic directed graph for initial integration. Node positions use WGS 84 latitude and longitude in decimal degrees. Edges refer to node IDs and have a travel mode, length in metres, free-flow travel time in seconds and a status.

`capacity_per_min` counts people per minute on pedestrian links and vehicles per minute on vehicle links. Agree on vehicle occupancy and shared capacity explicitly before combining flows. See [domain invariants](../../docs/domain/domain-invariants.md) for operational rules to apply when the planning model is developed.
