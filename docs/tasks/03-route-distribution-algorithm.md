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
- [ ] `experiments/routing/model.py`: NetworkX model; input `graph.json` + closures + $N$ + parameters; output distribution and loads.
- [ ] Fixtures `data/demo/routing/cases/*.json` and `expected.json` for: before closure, after the bridge closure, all exits unavailable, exit flooded before arrival, congestion at large $N$. You provide **W4**.
- [ ] Port to C# in Routing: `GET /queries/distribution` also returns `expectedLoad` per exit and `model: "logit-sue"`. The draw (`POST /commands/draw`) uses the new $p_i$; the seed still works.
- [ ] C# test that reads `expected.json` (tolerance e.g. $10^{-6}$).

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

_(fill in: chosen parameters, comparison with the baseline, iteration count, model limitations)_
