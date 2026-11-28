# Valencia · task 03 model inputs

These files complement the graph in [valencia-task03.zip](../valencia-task03.zip) and use its window `[-0.4717, 39.3956, -0.3292, 39.4758]` and OSM node IDs. Regenerate them with `python experiments/routing/prepare_inputs.py`. It needs internet for GHSL, the EMSR773 package unpacked as described in [valencia.md](../../../local/valencia.md) and the local OSM extract from `prepare_valencia.py`.

| File | Kind | Content |
| --- | --- | --- |
| `population.json` | **Estimate** | GHS-POP cells whose centres lie in the observed flood, summed on the nearest hazard node of the main pedestrian network. `totals` records the window and flood sums and the snapping distance. |
| `flood-depth.geojson` | Observation | EMSR773 AOI01 `floodDepthA` classes clipped to the window and merged per class. Depth exists only for the *Flooded area* part of the observation, about 5 % of the estimated people. *Flood trace* areas in the towns have no depth. |
| `flood-source.geojson` | Reference geometry | OSM way 41726575 *Barranc de Xiva*, the lower Poyo ravine that overflowed through Paiporta. |
| `shelters.synthetic.json` | **Synthetic** | Exercise shelters in `cassini.shelters.v1`. Not real facilities. Replace with the client file. |

## Deadline to leave the flood

Each hazard node gets the earlier of two deadlines set in [params.json](../params.json). Both are **model assumptions**, not a forecast or a hydraulic simulation.

- **Depth rule.** Inside an EMS depth class: 2 m or deeper 15 min, 1–2 m 30 min, 0.5–1 m 45 min, shallower 60 min.
- **Distance rule.** 15 min at the ravine plus 10 min per kilometre of straight-line distance, at most 90 min.

With the current data the distance rule decides for 99.6 % of the estimated people. About 47 % must leave within 30 minutes.

## Shelters from the client

A client list follows [shelters.v1.schema.json](../shelters.v1.schema.json). Start from the [empty template](../../../local/shelters.example.json). Give each shelter a position, an OSM `nodeId`, or both. The validator snaps positions to the nearest open pedestrian node within 250 m. It rejects shelters outside the map, in the hazard or unassessed zone, or cut off from the main pedestrian network, and gives each rejection a reason code.

## Checks

```sh
python -m pip install -r experiments/routing/requirements.txt
python -m unittest discover -s experiments/routing             # loaders and deadline rules
python experiments/routing/shelters.py data/demo/routing/valencia/shelters.synthetic.json
python experiments/routing/deadlines.py                         # deadline bands weighted by people
python experiments/routing/plot_inputs.py                       # map in experiments/routing/out/inputs.png
```

Sources. GHSL: Schiavina M. et al., *GHS-POP R2023A, epoch 2025*, European Commission JRC, CC BY 4.0. Flood depth: Copernicus Emergency Management Service, © European Union 2024, [EMSR773](https://mapping.emergency.copernicus.eu/activations/EMSR773/). Road graph and flood source: © OpenStreetMap contributors, ODbL.
