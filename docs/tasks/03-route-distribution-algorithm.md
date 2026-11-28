# Task 3 — Route distribution algorithm at a node (mathematics)

**Owner:** __________
**Directories:** `experiments/routing/`, `data/demo/routing/`, `src/Routing/` (algorithm: `Network.cs`, `RoutingQueries.cs`)
**Technologies:** Python 3 + NetworkX (reference model), C# (implementation), GloFAS forecast (exit validity window)

## What the demo shows

The node does not send everyone down the same road. The probability distribution accounts for travel time, capacity, and whether an exit will still be dry on arrival. The dashboard shows route shares and expected exit load; after the bridge closes, the distribution is recalculated.

## Mathematical problem

Current model: $w_i = 1/(\text{wait}_i + \text{travel}_i)$, $p_i = w_i/\sum w$. It ignores congestion and forecasts.

Proposed model (to verify and change if needed):

1. **Eligibility**: route $i$ is excluded if it crosses a closed segment during its traversal window, or if its arrival time $t^{arr}_i$ exceeds $T^{safe}_e$ of exit $e$ (from the GloFAS forecast / hazard extent).
2. **Congested cost** (BPR function): $c_i(x) = \sum_{s \in i} t_s\,(1 + \alpha (x_s / C_s)^\beta)$, where $x_s$ is the expected flow on segment $s$ and $C_s$ its capacity.
3. **Logit distribution**: $p_i = \dfrac{e^{-\theta c_i(x)}}{\sum_j e^{-\theta c_j(x)}}$.
4. **Equilibrium** (stochastic user equilibrium): $x = N \cdot \sum_i p_i(x)\,\delta_{i}$, solved with MSA (method of successive averages) until $\lVert x^{k+1}-x^k \rVert < \varepsilon$.

To prove or investigate: MSA convergence on the demo graph, the effect of $\theta$ (limits: $\theta \to \infty$ = shortest route, $\theta \to 0$ = uniform), comparison with the current formula (evacuation time of the last person, maximum exit load). An empty set of eligible routes yields the `NoEligibleExit` state, never a distribution.

## Scope

### Stage A (demo)
- [x] `experiments/routing/model.py`: reference model on the evacuation input, closures, GHSL population and parameters; output distribution and loads. Uses SciPy shortest paths instead of NetworkX for speed.
- [x] Fixtures `data/demo/routing/cases/*.json` and `expected.json` for: before closure, after the bridge closure, all exits unavailable, exit flooded before arrival, congestion at large $N$, wheelchair and steps, no re-entry into the flood. You provide **W4**.
- [x] Port to C# in Routing: `GET /queries/distribution` also returns `expectedLoad` per exit and `model: "logit-sue"`. The draw (`POST /commands/draw`) uses the new $p_i$; the seed still works. Valencia scenario only; Kłodzko and Warsaw keep the previous formula.
- [x] C# test that reads `expected.json` (tolerance $10^{-6}$).

### Stage B (completion)
- [ ] `IHazardForecast`: `MockGlofasForecast` (file) and `LiveGlofasForecast` (Open-Meteo Flood API) → $T^{safe}_e$.
- [ ] A short math note (1–2 pages) for the presentation: formulas, a plot of $p_i(\theta)$, comparison with the baseline.

## Dependencies

The Valencia radar-area W1 package includes OSM node and edge IDs, observed flood zones, directional costs, nominal capacities and a Situation closure revision. Get the [Valencia dataset ZIP](../../data/demo/routing/valencia-task03.zip) and see the [export and input contract](../../data/local/valencia-task03.md).

- You wait for: **W1** (graph from person 2). Until then, work on the current graph from `Network.cs` and rewrite it into `graph.json`.
- Waiting on you: person 4 (ranking unit candidates, W4), person 7 (`expectedLoad` field on the dashboard).

## How to test

```sh
python -m venv .venv && .venv/Scripts/pip install networkx
python experiments/routing/model.py --case data/demo/routing/cases/baseline.json
python experiments/routing/model.py --all --write-expected     # generates expected.json
dotnet run --project src/Routing/tests/Cassini.Routing.Tests.csproj -p:NuGetAudit=false
python scripts/offline_demo.py --test
```
- Properties to cover with tests: $\sum p_i = 1$; a route through a closure has $p=0$; the same seed → the same draw; convergence in < 100 iterations.

## Where to save results

- Parameters ($\alpha, \beta, \theta, C_s$) and their justification: `data/demo/routing/params.json` + section below.
- Presentation plots: `experiments/routing/out/` (PNG, outside Git or small).

## Results

The Python reference model runs on the Valencia W1 package. Inputs, sources and labels are described in [data/demo/routing/valencia](../../data/demo/routing/valencia/README.md); parameters are in [params.json](../../data/demo/routing/params.json).

```sh
python -m pip install -r experiments/routing/requirements.txt
python -m unittest discover -s experiments/routing      # 19 tests
python experiments/routing/model.py --baseline           # about 20 s; writes experiments/routing/out/valencia-result.json
```

**Model.** The GHSL estimate in the observed flood (113,980 people) is split into profiles: independent 75 %, slow 18 %, wheelchair 2 %; the assisted 5 % is counted and handed over. Each group must leave the flood before a deadline: the earlier of an EMS depth rule and 15 min + 10 min/km from the Poyo ravine. Groups are ordered by free-flow reserve into four priority waves. Each wave is a logit assignment over shelters ($\theta = 0.3$/min) on shortest paths, with BPR road costs ($\alpha = 0.15$, $\beta = 4$, linear above capacity) and a shelter intake queue, solved by MSA. Route choice counts a second on a flooded road as $\lambda = 5$ seconds on dry ground, so routes leave the water first; leave times and deadlines use real seconds. Shelter places are a hard limit enforced by a shadow price per shelter. Earlier waves keep their road flow and the places they were given. Arcs that re-enter the flood from dry ground, closed roads and, for wheelchair users, steps, tracks and paths are excluded.

**Valencia, 7 synthetic shelters, no closures, everyone guided ($\rho = 1$).**

| | logit SUE, $\lambda = 5$ | baseline $p = 1/t$ |
| --- | --- | --- |
| Leave the flood before the deadline | 84,359 (77.9 %) | 2,596 (2.4 %) |
| p95 minute of leaving the flood | 39.4 | 96.5 |
| People over shelter capacity | 0 | 26,833 |
| No eligible exit | 433 | 433 |

Without $\lambda$ the model minimised time to the shelter and kept people in the water longer: 63.6 % on time, and 78 % of the guided left the flood over 5 minutes later than they could. $\lambda$ = 3 / 5 / 10 gives 78.7 / 77.9 / 75.9 %; 5 is the only value without shelter overflow under the former soft penalty and has the shorter tail. Staggering the waves hurts (0 / 5 / 10 min apart: 63.6 / 44.9 / 12.3 % before $\lambda$), so priority means first claim on road and shelter capacity, not a later departure.

**Partial reach ($\rho$, `complianceShare`).** Only a share $\rho$ receives guidance; the rest walks the free-flow shortest path to the nearest dry node, ignoring congestion and shelters, and is fixed background for the guided waves. On time for everyone: $\rho$ = 0 / 10 / 30 / 50 / 70 / 100 % gives 63.5 / 66.8 / 73.1 / 75.6 / 77.2 / 77.9 %. Guided walkers also clear streets for the uninformed, whose on-time share rises from 63.5 % to about 79 %. Run `python experiments/routing/model.py --sweep 0,0.3,0.5,1`.

**Limitations.** Deadlines and evacuation road capacity (nominal × 10) are assumptions to calibrate. The assignment is static per wave, not a time-dependent simulation. Only one path per shelter is used, the shortest under current costs. Shelters are synthetic.

### C# port and API (Valencia scenario)

[src/Routing/Evacuation](../../src/Routing/Evacuation/) ports the model. It reproduces `expected.json` for all seven fixtures within $10^{-6}$ and the Python Valencia run to within one person per shelter, with the same iteration counts. A plan is computed in the background for each Situation revision, about 9 s for Valencia. Until it is ready the queries answer `503 PlanCalculating` instead of stale guidance.

| Endpoint (through the gateway, `/api/routing/…`) | Result |
| --- | --- |
| `GET queries/evacuation-plan` | Revision, status totals, shelter loads and overflow, waves and iterations |
| `GET queries/distribution?node=<OSM id>&profile=independent\|slow\|wheelchair` | Group status, wave, deadline, minute of leaving the flood and options with `weight`, `expectedLoad`, `capacity`, `stepFree`, segments and nodes; `model: "logit-sue"` |
| `POST commands/draw` with `node`, `revision`, `seed`, `mode` (profile) | Draws from the distribution. The same `commandId` returns the same draw; other content gives 409. A non-`Ok` group gets 422 with its status |
| `GET queries/draws/{commandId}` | `Withdrawn` once a closure hits the drawn route |

Inputs ship with the service from `data/demo/routing` (`Routing:EvacuationDirectory`). A client shelter file can replace the synthetic one with `Routing:EvacuationShelters`. Per-node deadlines come from `valencia/deadlines.json`, written by `experiments/routing/deadlines.py --output`.

```sh
python experiments/routing/fixtures.py --check                                   # Python still matches expected.json
dotnet run --project src/Routing/tests/Cassini.Routing.Tests.csproj -p:NuGetAudit=false   # C# matches expected.json
```

The dashboard does not show the distribution yet; that is task 7. The Docker image includes the inputs, but the image build was not run because no container engine was available.
