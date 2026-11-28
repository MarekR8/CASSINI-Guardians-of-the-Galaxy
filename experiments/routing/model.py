"""Task 03 reference model: who leaves when, and how people split across shelters.

1. Groups. Each populated hazard node is split into mobility profiles (params.json). The 'assisted' share is
   counted and handed over; it is not routed here.
2. Eligibility. Pedestrian arcs exclude closed roads, classes a profile cannot use and re-entry into the flood
   (an arc from a dry node onto a hazard road). Wheelchair users only go to step-free shelters.
3. Priority. Free-flow reserve = deadline - time to leave the flood. Groups with the smallest reserve leave first,
   in waves of similar size that start a fixed number of minutes apart.
4. Each wave is assigned by a logit stochastic user equilibrium solved with MSA. Road cost follows BPR on the
   pedestrian capacity, shared by both directions; a shelter adds an intake delay. Earlier waves stay on the
   roads as background flow and use up shelter places.
5. Status per group: Ok, DeadlineInfeasible (leaves the flood after its deadline under congestion) or
   NoEligibleExit (no shelter reachable for the profile). The baseline formula p = 1/time is evaluated on the
   same network for comparison.
"""

import argparse
from collections import defaultdict
import json
import math
from pathlib import Path

import numpy as np
from scipy.sparse import csr_matrix
from scipy.sparse.csgraph import dijkstra

import deadlines as deadline_rules
from graph import DEFAULT_GRAPH, EvacuationGraph
import shelters as shelter_file

OUT = Path(__file__).resolve().parent / "out"


class ProfileNetwork:
    """Directed pedestrian arcs usable by one profile, with one arc per ordered node pair."""

    def __init__(self, graph, profile, closed):
        excluded = set(profile["excludedClasses"])
        speed = profile["speedFactor"]
        best = {}
        for road_number, road in enumerate(graph.roads):
            if road["closed"] or road["id"] in closed or road["class"] in excluded:
                continue
            a, b = graph.index[road["from"]], graph.index[road["to"]]
            for (start, end), seconds in (((a, b), road["walk"][0]), ((b, a), road["walk"][1])):
                if seconds is None:
                    continue
                if graph.zone[start] == "safe" and road["zone"] == "hazard":
                    continue  # no re-entry into the flood once on dry ground
                cost = seconds / speed
                if (start, end) not in best or cost < best[(start, end)][0]:
                    best[(start, end)] = (cost, road_number)
        size = len(graph.nodes)
        pairs = np.array(sorted(best), dtype=np.int64).reshape(-1, 2)
        self.size = size
        self.start, self.end = pairs[:, 0], pairs[:, 1]
        self.base = np.array([best[(a, b)][0] for a, b in pairs.tolist()])
        self.road = np.array([best[(a, b)][1] for a, b in pairs.tolist()], dtype=np.int64)
        self.keys = self.start * size + self.end  # sorted because pairs are sorted

    def arc_of(self, starts, ends):
        return np.searchsorted(self.keys, starts.astype(np.int64) * self.size + ends)

    def trees(self, road_factor, shelter_nodes):
        """Shortest paths from every node to each shelter: (seconds, next node towards the shelter)."""
        cost = self.base * road_factor[self.road]
        reverse = csr_matrix((cost, (self.end, self.start)), shape=(self.size, self.size))
        return dijkstra(reverse, directed=True, indices=shelter_nodes, return_predecessors=True)


def congestion(ratio, alpha, beta):
    """BPR term alpha * r^beta up to capacity, continued along its tangent above it (queueing, not divergence)."""
    ratio = np.asarray(ratio, dtype=float)
    below = alpha * np.minimum(ratio, 1) ** beta
    return np.where(ratio <= 1, below, alpha + alpha * beta * (ratio - 1))


def first_dry(successor, dry):
    """For every node, the first dry node along its path (pointer jumping); -1 when unreachable."""
    jump = np.where(dry, np.arange(successor.size), successor)
    jump[jump < 0] = -1
    for _ in range(64):
        valid = jump >= 0
        nxt = jump.copy()
        nxt[valid] = jump[jump[valid]]
        nxt[valid & (jump[np.maximum(jump, 0)] < 0)] = -1
        if np.array_equal(nxt, jump):
            return jump
        jump = nxt
    raise RuntimeError("Path chains did not settle.")


def accumulate(distance, successor, demand):
    """Flow on each node's outgoing tree arc: the demand of the node plus everything routed through it."""
    flow = demand.copy()
    order = np.argsort(-np.where(np.isfinite(distance), distance, -1))
    flow_list, next_list = flow.tolist(), successor.tolist()
    for node in order.tolist():
        amount = flow_list[node]
        if amount and next_list[node] >= 0:
            flow_list[next_list[node]] += amount
    return np.array(flow_list)


class Model:
    def __init__(self, graph, people, deadline, shelters, params, closed=()):
        self.graph, self.params = graph, params
        settings = params["model"]
        self.settings = settings
        self.shelters = shelters
        self.shelter_nodes = np.array([graph.index[item["nodeId"]] for item in shelters])
        self.capacity = np.array([item["capacity"] for item in shelters], dtype=float)
        self.intake = np.array([item["intakePerHour"] or math.inf for item in shelters], dtype=float)
        self.step_free = np.array([item["stepFree"] for item in shelters])
        profiles = {name: value for name, value in params["profiles"].items() if isinstance(value, dict)}
        self.handed_over = sum(value["share"] for value in profiles.values() if value.get("handedOver"))
        self.profiles = {name: value for name, value in profiles.items() if not value.get("handedOver")}
        self.networks = {name: ProfileNetwork(graph, value, set(closed)) for name, value in self.profiles.items()}
        self.capacity_per_hour = np.array([road["pedestriansPerHour"] or 1 for road in graph.roads], dtype=float)
        self.road_seconds = np.array([np.mean([value for value in road["walk"] if value is not None] or [0])
                                      for road in graph.roads], dtype=float)
        self.dry = graph.zone == "safe"
        total = sum(people.values())
        self.people_total = total
        self.groups = []  # (node, profile, people, deadline minutes)
        for node, count in sorted(people.items()):
            for name, profile in self.profiles.items():
                self.groups.append((node, name, count * profile["share"], deadline[node][0]))

    # --- costs --------------------------------------------------------------------------------------------
    def road_factor(self, road_rate):
        bpr = self.settings["bpr"]
        ratio = road_rate / (self.capacity_per_hour * self.settings["capacityFactor"])
        return 1 + congestion(ratio, bpr["alpha"], bpr["beta"])

    def shelter_delay(self, shelter_people, residual):
        """Seconds added at a shelter: intake queueing plus a steep penalty for places beyond what is left."""
        delay = self.settings["shelterDelay"]
        rate = shelter_people / (self.settings["waveWindowMinutes"] / 60)
        queue = delay["minutes"] * 60 * congestion(rate / self.intake, 1.0, delay["beta"])
        excess = np.maximum(0, shelter_people / np.maximum(residual, 1) - 1)
        return queue + self.settings["capacityPenaltyMinutes"] * 60 * excess

    def solve_trees(self, road_rate):
        factor = self.road_factor(road_rate)
        return {name: network.trees(factor, self.shelter_nodes) for name, network in self.networks.items()}

    # --- choice -------------------------------------------------------------------------------------------
    def exit_times(self, trees):
        """Seconds from every node until its path to each shelter first reaches dry ground (shelter x node)."""
        exits = {}
        for name, (distance, successor) in trees.items():
            leave = np.full(distance.shape, np.inf)
            for row in range(len(self.shelters)):
                dry = first_dry(successor[row], self.dry)
                ok = (dry >= 0) & np.isfinite(distance[row])
                leave[row][ok] = distance[row][ok] - distance[row][dry[ok]]
            exits[name] = leave
        return exits

    def routable(self, name, distance):
        usable = np.isfinite(distance)
        return usable & self.step_free if self.profiles[name]["stepFreeOnly"] else usable

    def choice(self, groups, trees, shelter_people, residual, start_minutes, deadline_check=True):
        """Logit shares over shelters for each group, plus exit times and the eligibility outcome."""
        theta = self.settings["thetaPerMinute"]
        delay = self.shelter_delay(shelter_people, residual)
        exits = self.exit_times(trees)
        result = []
        for node, name, count, deadline in groups:
            distance = trees[name][0][:, node]
            leave = exits[name][:, node]
            routable = self.routable(name, distance)
            if not routable.any():
                result.append((None, "NoEligibleExit", leave, distance))
                continue
            reachable = routable & (residual > 0)
            if not reachable.any():
                result.append((None, "ShelterCapacityExhausted", leave, distance))
                continue
            in_time = reachable & (start_minutes + leave / 60 <= deadline) if deadline_check else reachable
            usable = in_time if in_time.any() else reachable
            cost = (distance + delay) / 60
            utility = np.where(usable, -theta * (cost - cost[usable].min()), -np.inf)
            share = np.exp(utility)
            share /= share.sum()
            result.append((share, "Ok" if in_time.any() else "DeadlineInfeasible", leave, distance))
        return result

    def load(self, groups, choices, trees):
        """People per road (both directions) and per shelter for the given choices."""
        road_people = np.zeros(len(self.graph.roads))
        shelter_people = np.zeros(len(self.shelters))
        for name, network in self.networks.items():
            distance, successor = trees[name]
            for row in range(len(self.shelters)):
                demand = np.zeros(len(self.graph.nodes))
                for (node, profile, count, _), (share, *_rest) in zip(groups, choices):
                    if profile == name and share is not None:
                        demand[node] += count * share[row]
                if not demand.any():
                    continue
                shelter_people[row] += demand.sum()
                flow = accumulate(distance[row], successor[row], demand)
                carrying = np.flatnonzero((flow > 0) & (successor[row] >= 0))
                arcs = network.arc_of(carrying, successor[row][carrying])
                np.add.at(road_people, network.road[arcs], flow[carrying])
        return road_people, shelter_people

    # --- waves --------------------------------------------------------------------------------------------
    def plan_waves(self):
        free = self.solve_trees(np.zeros(len(self.graph.roads)))
        choices = self.choice(self.groups, free, np.zeros(len(self.shelters)), self.capacity, 0, deadline_check=False)
        reserve = []
        for (node, name, count, deadline), (share, status, leave, distance) in zip(self.groups, choices):
            if share is None:
                reserve.append(-math.inf)
                continue
            usable = self.routable(name, distance) & np.isfinite(leave)
            reserve.append(deadline - leave[usable].min() / 60)
        order = np.argsort(reserve, kind="stable")
        counts = np.array([group[2] for group in self.groups])
        cumulative = np.cumsum(counts[order]) / counts.sum()
        wave = np.empty(len(self.groups), dtype=int)
        wave[order] = np.minimum((cumulative * self.settings["waves"] - 1e-9).astype(int), self.settings["waves"] - 1)
        return wave, np.array(reserve)

    def run(self, baseline=False, exact=False):
        wave_of, reserve = self.plan_waves()
        window_hours = self.settings["waveWindowMinutes"] / 60
        background = np.zeros(len(self.graph.roads))
        residual = self.capacity.copy()
        outcome = [None] * len(self.groups)
        waves = []
        for wave in range(self.settings["waves"]):
            members = [number for number in range(len(self.groups)) if wave_of[number] == wave]
            groups = [self.groups[number] for number in members]
            start = wave * self.settings["waveStaggerMinutes"]
            x_road = np.zeros(len(self.graph.roads))
            x_shelter = np.zeros(len(self.shelters))
            gap, iteration = math.inf, 0
            limit = 1 if baseline else self.settings["maxIterations"]
            while iteration < limit:
                iteration += 1
                rate = background + x_road / window_hours
                trees = self.solve_trees(rate)
                choices = (self.baseline_choice(groups, trees, residual, start) if baseline else
                           self.choice(groups, trees, x_shelter, residual, start))
                y_road, y_shelter = self.load(groups, choices, trees)
                if iteration == 1:
                    x_road, x_shelter = y_road, y_shelter
                    continue
                # Relative gap: time spent on the current flows versus on the current best responses.
                seconds = self.road_seconds * self.road_factor(rate)
                spent = float(x_road @ seconds)
                gap = abs(spent - float(y_road @ seconds)) / max(spent, 1e-9)
                step = 1 / iteration
                x_road = x_road + step * (y_road - x_road)
                x_shelter = x_shelter + step * (y_shelter - x_shelter)
                if gap < self.settings["tolerance"]:
                    break
            # Final evaluation under the converged flows.
            trees = self.solve_trees(background + x_road / window_hours)
            final = (self.baseline_choice(groups, trees, residual, start) if baseline else
                     self.choice(groups, trees, x_shelter, residual, start))
            for number, (share, status, leave, distance) in zip(members, final):
                group = self.groups[number]
                if share is not None:
                    left = float(np.dot(share, np.where(np.isfinite(leave), leave, 0))) / 60
                    status = "Ok" if start + left <= group[3] else "DeadlineInfeasible"
                else:
                    left = None
                outcome[number] = {"share": share, "status": status, "wave": wave, "start": start,
                                   "leaveMinutes": left, "reserveFreeFlow": float(reserve[number])}
            background += x_road / window_hours
            residual -= x_shelter
            waves.append({"wave": wave + 1, "startMinute": start, "people": round(sum(g[2] for g in groups)),
                          "iterations": iteration, "gap": None if math.isinf(gap) else round(gap, 6)})
        return self.report(outcome, waves, residual, baseline, exact)

    def baseline_choice(self, groups, trees, residual, start):
        """The current demo formula: p proportional to 1 / free-flow travel time, ignoring congestion."""
        exits = self.exit_times(trees)
        result = []
        for node, name, count, deadline in groups:
            distance = trees[name][0][:, node]
            leave = exits[name][:, node]
            reachable = self.routable(name, distance)
            if not reachable.any():
                result.append((None, "NoEligibleExit", leave, distance))
                continue
            weight = np.where(reachable, 1 / np.maximum(distance, 1), 0)
            result.append((weight / weight.sum(), "Ok", leave, distance))
        return result

    # --- output -------------------------------------------------------------------------------------------
    def report(self, outcome, waves, residual, baseline, exact=False):
        """Summarise the run. exact=True keeps full precision and every share, for cross-language fixtures."""
        def rnd(value, digits=0):
            return float(value) if exact else (round(value) if digits == 0 else round(value, digits))

        status_people = defaultdict(float)
        shelter_people = np.zeros(len(self.shelters))
        leave_times = []
        per_node = defaultdict(dict)
        for (node, name, count, deadline), item in zip(self.groups, outcome):
            status_people[item["status"]] += count
            if item["share"] is not None:
                shelter_people += count * item["share"]
                leave_times.append((item["start"] + item["leaveMinutes"], count))
            per_node[self.graph.nodes[node]["id"]][name] = {
                "people": rnd(count, 2), "wave": item["wave"] + 1, "startMinute": item["start"],
                "deadlineMinutes": deadline, "status": item["status"],
                "leaveFloodMinutes": None if item["leaveMinutes"] is None else rnd(item["leaveMinutes"], 1),
                "shares": None if item["share"] is None else
                {shelter["id"]: rnd(float(value), 4) for shelter, value in zip(self.shelters, item["share"])
                 if exact or value >= 5e-5},
            }
        leave_times.sort()
        weights = np.cumsum([weight for _, weight in leave_times])

        def percentile(fraction):
            if not leave_times:
                return None
            return rnd(leave_times[int(np.searchsorted(weights, fraction * weights[-1]))][0], 1)

        return {
            "model": "baseline-inverse-time" if baseline else "logit-sue-msa",
            "people": {"estimated": rnd(self.people_total), "handedOver": rnd(self.people_total * self.handed_over),
                       "routed": rnd(sum(group[2] for group in self.groups))},
            "status": {key: rnd(value) for key, value in sorted(status_people.items())},
            "clearanceMinutes": {"p50": percentile(.5), "p95": percentile(.95), "max": percentile(1.0)},
            "waves": waves,
            "shelters": [{"id": shelter["id"], "capacity": shelter["capacity"],
                          "expectedLoad": rnd(float(load)), "overflow": rnd(max(0.0, float(load) - shelter["capacity"]))}
                         for shelter, load in zip(self.shelters, shelter_people)],
            "nodes": per_node,
        }


def build(args):
    load = deadline_rules.load_json
    graph = EvacuationGraph.load(args.graph)
    params = load(args.params)
    population = load(args.inputs / "population.json")
    people = {graph.index[item["nodeId"]]: item["people"] for item in population["nodes"]}
    deadline = deadline_rules.compute(graph, load(args.inputs / "flood-depth.geojson"),
                                      load(args.inputs / "flood-source.geojson"), params)
    shelters = shelter_file.load(args.shelters or args.inputs / "shelters.synthetic.json", graph)
    if shelters["rejected"]:
        raise ValueError(f"Shelter file has rejected entries: {shelters['rejected']}")
    if args.theta is not None:
        params["model"]["thetaPerMinute"] = args.theta
    return Model(graph, people, deadline, shelters["shelters"], params, closed=args.close)


def print_summary(result):
    people = result["people"]
    print(f"[{result['model']}] {people['routed']:,} people routed, {people['handedOver']:,} handed over for assisted transport")
    print("  status: " + ", ".join(f"{key} {value:,}" for key, value in result["status"].items()))
    clearance = result["clearanceMinutes"]
    print(f"  leave the flood by minute: p50 {clearance['p50']}, p95 {clearance['p95']}, max {clearance['max']}")
    for wave in result["waves"]:
        print(f"  wave {wave['wave']} at +{wave['startMinute']} min: {wave['people']:,} people, "
              f"{wave['iterations']} iterations, gap {wave['gap']}")
    for shelter in result["shelters"]:
        flag = f"  OVERFLOW {shelter['overflow']:,}" if shelter["overflow"] else ""
        print(f"  {shelter['id']}: {shelter['expectedLoad']:>7,} / {shelter['capacity']:,}{flag}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--graph", type=Path, default=DEFAULT_GRAPH)
    parser.add_argument("--inputs", type=Path, default=deadline_rules.INPUTS)
    parser.add_argument("--params", type=Path, default=deadline_rules.PARAMS)
    parser.add_argument("--shelters", type=Path, help="Client shelter file (default: synthetic exercise shelters)")
    parser.add_argument("--close", nargs="*", default=[], help="Extra road IDs to treat as closed")
    parser.add_argument("--theta", type=float, help="Override thetaPerMinute")
    parser.add_argument("--baseline", action="store_true", help="Also evaluate the current 1/time formula")
    parser.add_argument("--node", type=int, help="Print the distribution for one OSM node")
    parser.add_argument("--output", type=Path, default=OUT / "valencia-result.json")
    args = parser.parse_args()
    try:
        model = build(args)
        result = model.run()
        print_summary(result)
        if args.baseline:
            print_summary(model.run(baseline=True))
        if args.node is not None:
            print(json.dumps(result["nodes"].get(args.node, {"error": "node has no estimated people"}), indent=1))
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"wrote {args.output}")
    except (OSError, KeyError, ValueError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
