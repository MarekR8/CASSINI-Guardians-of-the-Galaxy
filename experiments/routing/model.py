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
6. Hazard time weight. Route choice weighs every second on a flooded road by lambda (model.hazardTimeWeight), so
   guided routes leave the water first and then walk on dry ground to the shelter. Leave times, deadlines and
   congestion use real seconds along the chosen path.
7. Mixed compliance. Only a share rho (model.complianceShare) receives guidance. The uninformed rest walks the
   free-flow shortest path to the nearest dry node: it ignores congestion and shelters, so its route does not
   depend on our decisions. Its flow is fixed background for the guided waves. Afterwards the uninformed leave
   times are evaluated on their fixed paths under the final congestion of both classes. rho = 1 is the model above.
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
import forecast as hazard_forecast
from graph import DEFAULT_GRAPH, EvacuationGraph
import shelters as shelter_file

OUT = Path(__file__).resolve().parent / "out"
STALL = 200  # price iterations without a 1 % lower excess before the capacity step gives up


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
        self.hazard = np.array([graph.roads[road]["zone"] == "hazard" for road in self.road.tolist()], dtype=bool)
        self.keys = self.start * size + self.end  # sorted because pairs are sorted

    def arc_of(self, starts, ends):
        return np.searchsorted(self.keys, starts.astype(np.int64) * self.size + ends)

    def trees(self, road_factor, shelter_nodes, hazard_weight=1.0):
        """Shortest paths from every node to each shelter under the perceived cost.

        Returns (perceived cost, next node towards the shelter, real seconds along that path), each shelter x node.
        The perceived cost multiplies time on flooded roads by hazard_weight, so routes leave the water early.
        """
        real_cost = self.base * road_factor[self.road]
        cost = real_cost * np.where(self.hazard, hazard_weight, 1.0)
        reverse = csr_matrix((cost, (self.end, self.start)), shape=(self.size, self.size))
        distance, successor = dijkstra(reverse, directed=True, indices=shelter_nodes, return_predecessors=True)
        if hazard_weight == 1.0:
            return distance, successor, distance
        real = np.empty_like(distance)
        for row in range(distance.shape[0]):
            moving = np.flatnonzero(np.isfinite(distance[row]) & (successor[row] >= 0))
            step = np.zeros(self.size)
            step[moving] = real_cost[self.arc_of(moving, successor[row][moving])]
            real[row] = along_tree(distance[row], successor[row], step)
        return distance, successor, real

    def to_nearest(self, road_factor, targets):
        """Shortest path from every node to whichever target is nearest: (seconds, next node towards it)."""
        cost = self.base * road_factor[self.road]
        reverse = csr_matrix((cost, (self.end, self.start)), shape=(self.size, self.size))
        distance, successor, _ = dijkstra(reverse, directed=True, indices=targets, min_only=True,
                                          return_predecessors=True)
        return distance, successor


def congestion(ratio, alpha, beta):
    """BPR term alpha * r^beta up to capacity, continued along its tangent above it (queueing, not divergence)."""
    ratio = np.asarray(ratio, dtype=float)
    below = alpha * np.minimum(ratio, 1) ** beta
    return np.where(ratio <= 1, below, alpha + alpha * beta * (ratio - 1))


def along_tree(distance, successor, step):
    """Sum step (cost of each node's outgoing tree arc) along every node's path to the tree target."""
    total = np.full(distance.shape, np.inf)
    finite = np.isfinite(distance)
    total[finite & (successor < 0)] = 0
    order = np.flatnonzero(finite)[np.argsort(distance[finite], kind="stable")]
    values, nxt, cost = total.tolist(), successor.tolist(), step.tolist()
    for node in order.tolist():
        if nxt[node] >= 0:
            values[node] = cost[node] + values[nxt[node]]
    return np.array(values)


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
        # T_safe per shelter (minutes after the plan start) from the hazard forecast; no forecast means no limit.
        self.safe_until = np.array([math.inf if item.get("safeUntilMinutes") is None else float(item["safeUntilMinutes"])
                                    for item in shelters])
        profiles = {name: value for name, value in params["profiles"].items() if isinstance(value, dict)}
        self.handed_over = sum(value["share"] for value in profiles.values() if value.get("handedOver"))
        self.profiles = {name: value for name, value in profiles.items() if not value.get("handedOver")}
        self.networks = {name: ProfileNetwork(graph, value, set(closed)) for name, value in self.profiles.items()}
        self.hazard_weight = float(settings.get("hazardTimeWeight", 1.0))
        if self.hazard_weight < 1:
            raise ValueError("model.hazardTimeWeight must be at least 1.")
        self.road_weight = np.array([self.hazard_weight if road["zone"] == "hazard" else 1.0 for road in graph.roads])
        self.capacity_per_hour = np.array([road["pedestriansPerHour"] or 1 for road in graph.roads], dtype=float)
        self.road_seconds = np.array([np.mean([value for value in road["walk"] if value is not None] or [0])
                                      for road in graph.roads], dtype=float)
        self.dry = graph.zone == "safe"
        self.dry_nodes = np.flatnonzero(self.dry)
        total = sum(people.values())
        self.people_total = total
        self.compliance = float(settings.get("complianceShare", 1.0))
        if not 0 <= self.compliance <= 1:
            raise ValueError("model.complianceShare must be between 0 and 1.")
        self.groups = []      # guided: (node, profile, people, deadline minutes)
        self.uninformed = []  # not reached by the system, same layout
        for node, count in sorted(people.items()):
            for name, profile in self.profiles.items():
                if self.compliance > 0:
                    self.groups.append((node, name, count * profile["share"] * self.compliance, deadline[node][0]))
                if self.compliance < 1:
                    self.uninformed.append((node, name, count * profile["share"] * (1 - self.compliance),
                                            deadline[node][0]))

    # --- costs --------------------------------------------------------------------------------------------
    def road_factor(self, road_rate):
        bpr = self.settings["bpr"]
        ratio = road_rate / (self.capacity_per_hour * self.settings["capacityFactor"])
        return 1 + congestion(ratio, bpr["alpha"], bpr["beta"])

    def shelter_delay(self, shelter_people, price):
        """Seconds added at a shelter: intake queueing plus the capacity shadow price (minutes)."""
        delay = self.settings["shelterDelay"]
        rate = shelter_people / (self.settings["waveWindowMinutes"] / 60)
        queue = delay["minutes"] * 60 * congestion(rate / self.intake, 1.0, delay["beta"])
        return queue + price * 60

    def update_price(self, price, shelter_people, residual):
        """Raise the price of a shelter while its assigned people exceed the places left; let it fall otherwise."""
        excess = shelter_people / np.maximum(residual, 1) - 1
        return np.maximum(0, price + self.settings["capacityPriceStepMinutes"] * excess), float(excess.max(initial=-1))

    def solve_trees(self, road_rate, hazard_weight=None):
        factor = self.road_factor(road_rate)
        return {name: network.trees(factor, self.shelter_nodes, self.hazard_weight if hazard_weight is None else hazard_weight)
                for name, network in self.networks.items()}

    # --- choice -------------------------------------------------------------------------------------------
    def exit_times(self, trees):
        """Real seconds from every node until its path to each shelter first reaches dry ground (shelter x node)."""
        exits = {}
        for name, (distance, successor, real) in trees.items():
            leave = np.full(distance.shape, np.inf)
            for row in range(len(self.shelters)):
                dry = first_dry(successor[row], self.dry)
                ok = (dry >= 0) & np.isfinite(real[row])
                leave[row][ok] = real[row][ok] - real[row][dry[ok]]
            exits[name] = leave
        return exits

    def routable(self, name, distance):
        usable = np.isfinite(distance)
        return usable & self.step_free if self.profiles[name]["stepFreeOnly"] else usable

    def safe_on_arrival(self, real, start_minutes):
        """Shelters still safe when the group arrives (real seconds to each shelter), per the forecast T_safe."""
        return start_minutes + real / 60 <= self.safe_until

    def choice_entries(self, groups, trees, residual, start_minutes, deadline_check=True):
        """Per group: (usable shelters or None, status, leave seconds, perceived cost to each shelter)."""
        exits = self.exit_times(trees)
        entries = []
        for node, name, count, deadline in groups:
            distance = trees[name][0][:, node]
            leave = exits[name][:, node]
            routable = self.routable(name, distance) & self.safe_on_arrival(trees[name][2][:, node], start_minutes)
            if not routable.any():
                entries.append((None, "NoEligibleExit", leave, distance))
                continue
            reachable = routable & (residual > 0)
            if not reachable.any():
                entries.append((None, "ShelterCapacityExhausted", leave, distance))
                continue
            in_time = reachable & (start_minutes + leave / 60 <= deadline) if deadline_check else reachable
            usable = in_time if in_time.any() else reachable
            entries.append((usable, "Ok" if in_time.any() else "DeadlineInfeasible", leave, distance))
        return entries

    def logit(self, entries, delay):
        """Logit shares over the usable shelters of each entry for the given shelter delays (seconds)."""
        theta = self.settings["thetaPerMinute"]
        result = []
        for usable, status, leave, distance in entries:
            if usable is None:
                result.append((None, status, leave, distance))
                continue
            cost = (distance + delay) / 60
            utility = np.where(usable, -theta * (cost - cost[usable].min()), -np.inf)
            share = np.exp(utility)
            share /= share.sum()
            result.append((share, status, leave, distance))
        return result

    def choice(self, groups, trees, shelter_people, residual, start_minutes, deadline_check=True, price=None):
        """Logit shares over shelters for each group, plus exit times and the eligibility outcome."""
        delay = self.shelter_delay(shelter_people, np.zeros(len(self.shelters)) if price is None else price)
        return self.logit(self.choice_entries(groups, trees, residual, start_minutes, deadline_check), delay)

    def enforce_capacity(self, groups, entries, shelter_people, residual, price):
        """Raise shelter prices on the final routes until no shelter takes more people than it has places left.
        Stops early when the excess has not improved by 1 % for STALL iterations; ration() then cuts what is left."""
        queue = self.shelter_delay(shelter_people, np.zeros(len(self.shelters)))
        counts = np.array([group[2] for group in groups])
        best, since = math.inf, 0
        for iteration in range(1, self.settings["maxPriceIterations"] + 1):
            choices = self.logit(entries, queue + price * 60)
            loads = np.zeros(len(self.shelters))
            for count, (share, *_rest) in zip(counts, choices):
                if share is not None:
                    loads += count * share
            price, excess = self.update_price(price, loads, residual)
            if excess <= self.settings["capacityTolerance"]:
                return choices, iteration
            if excess < best * 0.99:
                best, since = excess, 0
            else:
                since += 1
                if since >= STALL:
                    break
        return choices, iteration

    def ration(self, groups, choices, residual):
        """Places are a hard limit. Where a shelter is still over its places (a group's in-time shelters are all
        full), every group sent there is cut proportionally. The placed share keeps a normalised distribution, so the
        group is still guided out of the flood; the rest is counted as ShelterCapacityExhausted."""
        loads = np.zeros(len(self.shelters))
        for (_node, _name, count, _deadline), (share, *_rest) in zip(groups, choices):
            if share is not None:
                loads += count * share
        over = loads > np.maximum(residual, 0) * (1 + self.settings["capacityTolerance"])
        factor = np.where(over, np.maximum(residual, 0) / np.maximum(loads, 1e-12), 1.0)
        result = []
        for share, status, leave, distance in choices:
            if share is None or not over.any():
                result.append((share, status, leave, distance, 1.0 if share is not None else 0.0))
                continue
            kept = share * factor
            placed = float(kept.sum())
            if placed <= 1e-12:
                result.append((None, "ShelterCapacityExhausted", leave, distance, 0.0))
            else:
                result.append((kept / placed, status, leave, distance, placed))
        return result

    def load(self, groups, choices, trees):
        """People per road (both directions) and per shelter for the given choices."""
        road_people = np.zeros(len(self.graph.roads))
        shelter_people = np.zeros(len(self.shelters))
        for name, network in self.networks.items():
            distance, successor, _ = trees[name]
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

    # --- uninformed class ---------------------------------------------------------------------------------
    def uninformed_paths(self):
        """Free-flow shortest paths to the nearest dry node per profile; fixed, whatever the guided groups do."""
        ones = np.ones(len(self.graph.roads))
        return {name: network.to_nearest(ones, self.dry_nodes) for name, network in self.networks.items()}

    def uninformed_rate(self, paths):
        """Road flow (people per hour, both directions) of the uninformed, spread over one wave window."""
        road_people = np.zeros(len(self.graph.roads))
        for name, network in self.networks.items():
            distance, successor = paths[name]
            demand = np.zeros(len(self.graph.nodes))
            for node, profile, count, _ in self.uninformed:
                if profile == name:
                    demand[node] += count
            if not demand.any():
                continue
            flow = accumulate(distance, successor, demand)
            carrying = np.flatnonzero((flow > 0) & (successor >= 0))
            arcs = network.arc_of(carrying, successor[carrying])
            np.add.at(road_people, network.road[arcs], flow[carrying])
        return road_people / (self.settings["waveWindowMinutes"] / 60)

    def uninformed_outcome(self, paths, rate):
        """Minutes until each uninformed group reaches dry ground on its fixed path under the final congestion."""
        factor = self.road_factor(rate)
        leave = {}
        for name, network in self.networks.items():
            distance, successor = paths[name]
            seconds = np.full(len(self.graph.nodes), np.inf)
            seconds[self.dry_nodes] = 0
            moving = np.flatnonzero(np.isfinite(distance) & (successor >= 0))
            arcs = network.arc_of(moving, successor[moving])
            step = dict(zip(moving.tolist(), (network.base[arcs] * factor[network.road[arcs]]).tolist()))
            nxt = successor.tolist()
            values = seconds.tolist()
            for node in moving[np.argsort(distance[moving], kind="stable")].tolist():
                values[node] = step[node] + values[nxt[node]]
            leave[name] = np.array(values)
        outcome = []
        for node, name, count, deadline in self.uninformed:
            minutes = leave[name][node] / 60
            if not np.isfinite(minutes):
                outcome.append(("NoEligibleExit", None))
            else:
                outcome.append(("Ok" if minutes <= deadline else "DeadlineInfeasible", float(minutes)))
        return outcome

    # --- waves --------------------------------------------------------------------------------------------
    def plan_waves(self):
        if not self.groups:
            return np.empty(0, dtype=int), np.empty(0)
        free = self.solve_trees(np.zeros(len(self.graph.roads)))
        choices = self.choice(self.groups, free, np.zeros(len(self.shelters)), self.capacity, 0, deadline_check=False)
        reserve = []
        for (node, name, count, deadline), (share, status, leave, distance) in zip(self.groups, choices):
            if share is None:
                reserve.append(-math.inf)
                continue
            usable = self.routable(name, distance) & self.safe_on_arrival(free[name][2][:, node], 0) & np.isfinite(leave)
            reserve.append(deadline - leave[usable].min() / 60)
        order = np.argsort(reserve, kind="stable")
        counts = np.array([group[2] for group in self.groups])
        cumulative = np.cumsum(counts[order]) / counts.sum()
        wave = np.empty(len(self.groups), dtype=int)
        wave[order] = np.minimum((cumulative * self.settings["waves"] - 1e-9).astype(int), self.settings["waves"] - 1)
        return wave, np.array(reserve)

    def run(self, baseline=False, exact=False):
        if baseline:
            # The baseline is the former demo formula: plain travel time, no hazard weight.
            saved, self.hazard_weight = self.hazard_weight, 1.0
            try:
                return self._run(baseline, exact, 1.0)
            finally:
                self.hazard_weight = saved
        return self._run(baseline, exact, self.hazard_weight)

    def _run(self, baseline, exact, weight):
        wave_of, reserve = self.plan_waves()
        window_hours = self.settings["waveWindowMinutes"] / 60
        paths = self.uninformed_paths() if self.uninformed else None
        background = self.uninformed_rate(paths) if self.uninformed else np.zeros(len(self.graph.roads))
        residual = self.capacity.copy()
        outcome = [None] * len(self.groups)
        waves = []
        for wave in range(self.settings["waves"] if self.groups else 0):
            members = [number for number in range(len(self.groups)) if wave_of[number] == wave]
            groups = [self.groups[number] for number in members]
            start = wave * self.settings["waveStaggerMinutes"]
            x_road = np.zeros(len(self.graph.roads))
            x_shelter = np.zeros(len(self.shelters))
            price = np.zeros(len(self.shelters))  # capacity shadow price per shelter, minutes
            gap, iteration = math.inf, 0
            limit = 1 if baseline else self.settings["maxIterations"]
            while iteration < limit:
                iteration += 1
                rate = background + x_road / window_hours
                trees = self.solve_trees(rate, weight)
                choices = (self.baseline_choice(groups, trees, residual, start) if baseline else
                           self.choice(groups, trees, x_shelter, residual, start, price=price))
                y_road, y_shelter = self.load(groups, choices, trees)
                if iteration == 1:
                    x_road, x_shelter = y_road, y_shelter
                    price, _ = self.update_price(price, x_shelter, residual)
                    continue
                # Relative gap: perceived time on the current flows versus on the current best responses.
                seconds = self.road_seconds * self.road_factor(rate) * self.road_weight
                spent = float(x_road @ seconds)
                gap = abs(spent - float(y_road @ seconds)) / max(spent, 1e-9)
                step = 1 / iteration
                x_road = x_road + step * (y_road - x_road)
                x_shelter = x_shelter + step * (y_shelter - x_shelter)
                price, _ = self.update_price(price, x_shelter, residual)
                if gap < self.settings["tolerance"]:
                    break
            # Final evaluation under the converged flows; shelter prices are then tuned until places suffice.
            trees = self.solve_trees(background + x_road / window_hours, weight)
            price_iterations = 0
            if baseline:
                final = [(*choice, 1.0 if choice[0] is not None else 0.0)
                         for choice in self.baseline_choice(groups, trees, residual, start)]
            else:
                entries = self.choice_entries(groups, trees, residual, start)
                priced, price_iterations = self.enforce_capacity(groups, entries, x_shelter, residual, price)
                final = self.ration(groups, priced, residual)
            assigned = np.zeros(len(self.shelters))
            for number, (share, status, leave, distance, placed) in zip(members, final):
                group = self.groups[number]
                if share is not None:
                    assigned += group[2] * placed * share
                    left = float(np.dot(share, np.where(np.isfinite(leave), leave, 0))) / 60
                    status = "Ok" if start + left <= group[3] else "DeadlineInfeasible"
                else:
                    left = None
                outcome[number] = {"share": share, "status": status, "wave": wave, "start": start, "placed": placed,
                                   "leaveMinutes": left, "reserveFreeFlow": float(reserve[number])}
            background += x_road / window_hours
            residual -= assigned  # the places actually given to this wave, after the capacity prices
            waves.append({"wave": wave + 1, "startMinute": start, "people": round(sum(g[2] for g in groups)),
                          "iterations": iteration, "priceIterations": price_iterations,
                          "gap": None if math.isinf(gap) else round(gap, 6)})
        uninformed = self.uninformed_outcome(paths, background) if self.uninformed else []
        return self.report(outcome, waves, residual, baseline, exact, uninformed)

    def baseline_choice(self, groups, trees, residual, start):
        """The current demo formula: p proportional to 1 / free-flow travel time, ignoring congestion."""
        exits = self.exit_times(trees)
        result = []
        for node, name, count, deadline in groups:
            distance = trees[name][0][:, node]
            leave = exits[name][:, node]
            reachable = self.routable(name, distance) & self.safe_on_arrival(trees[name][2][:, node], start)
            if not reachable.any():
                result.append((None, "NoEligibleExit", leave, distance))
                continue
            weight = np.where(reachable, 1 / np.maximum(distance, 1), 0)
            result.append((weight / weight.sum(), "Ok", leave, distance))
        return result

    # --- output -------------------------------------------------------------------------------------------
    def report(self, outcome, waves, residual, baseline, exact=False, uninformed=()):
        """Summarise the run. exact=True keeps full precision and every share, for cross-language fixtures."""
        def rnd(value, digits=0):
            return float(value) if exact else (round(value) if digits == 0 else round(value, digits))

        uninformed_status = defaultdict(float)
        uninformed_nodes = defaultdict(dict)
        for (node, name, count, deadline), (status, minutes) in zip(self.uninformed, uninformed):
            uninformed_status[status] += count
            uninformed_nodes[self.graph.nodes[node]["id"]][name] = {
                "people": rnd(count, 2), "deadlineMinutes": deadline, "status": status,
                "leaveFloodMinutes": None if minutes is None else rnd(minutes, 1)}

        status_people = defaultdict(float)
        shelter_people = np.zeros(len(self.shelters))
        leave_times = []
        per_node = defaultdict(dict)
        for (node, name, count, deadline), item in zip(self.groups, outcome):
            placed = item["placed"] if item["share"] is not None else 1.0
            status_people[item["status"]] += count * placed
            if placed < 1 - 1e-12:
                status_people["ShelterCapacityExhausted"] += count * (1 - placed)
            if item["share"] is not None:
                shelter_people += count * placed * item["share"]
                leave_times.append((item["start"] + item["leaveMinutes"], count * placed))
            per_node[self.graph.nodes[node]["id"]][name] = {
                "people": rnd(count, 2), "wave": item["wave"] + 1, "startMinute": item["start"],
                "deadlineMinutes": deadline, "status": item["status"],
                "placedShare": rnd(item["placed"], 4) if item["share"] is not None else 0.0,
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

        combined = defaultdict(float)
        for source in (status_people, uninformed_status):
            for key, value in source.items():
                combined[key] += value

        return {
            "model": "baseline-inverse-time" if baseline else "logit-sue-msa",
            "complianceShare": self.compliance,
            "people": {"estimated": rnd(self.people_total), "handedOver": rnd(self.people_total * self.handed_over),
                       "routed": rnd(sum(group[2] for group in self.groups)),
                       "uninformed": rnd(sum(group[2] for group in self.uninformed))},
            "status": {key: rnd(value) for key, value in sorted(status_people.items())},
            "statusAll": {key: rnd(value) for key, value in sorted(combined.items())},
            "uninformed": {"status": {key: rnd(value) for key, value in sorted(uninformed_status.items())},
                           "nodes": uninformed_nodes},
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
    sites = shelters["shelters"]
    if getattr(args, "forecast", None) is not None:
        safety = hazard_forecast.evaluate(hazard_forecast.load(args.forecast), [item["id"] for item in sites])
        sites = hazard_forecast.apply(sites, safety)
    if args.theta is not None:
        params["model"]["thetaPerMinute"] = args.theta
    if getattr(args, "compliance", None) is not None:
        params["model"]["complianceShare"] = args.compliance
    if getattr(args, "hazard_weight", None) is not None:
        params["model"]["hazardTimeWeight"] = args.hazard_weight
    return Model(graph, people, deadline, sites, params, closed=args.close)


def on_time(status):
    total = sum(status.values())
    return status.get("Ok", 0) / total if total else float("nan")


def print_summary(result):
    people = result["people"]
    print(f"[{result['model']}, compliance {result['complianceShare']:.0%}] {people['routed']:,} guided, "
          f"{people['uninformed']:,} uninformed, {people['handedOver']:,} handed over for assisted transport")
    print("  guided:     " + ", ".join(f"{key} {value:,}" for key, value in result["status"].items()))
    if result["uninformed"]["status"]:
        print("  uninformed: " + ", ".join(f"{key} {value:,}" for key, value in result["uninformed"]["status"].items()))
    print(f"  on time: guided {on_time(result['status']):.1%}, uninformed {on_time(result['uninformed']['status']):.1%}, "
          f"everyone {on_time(result['statusAll']):.1%}")
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
    parser.add_argument("--forecast", type=Path, default=hazard_forecast.MOCK,
                        help="Hazard forecast for T_safe per shelter (default: the synthetic Valencia mock)")
    parser.add_argument("--no-forecast", dest="forecast", action="store_const", const=None,
                        help="Ignore the forecast: no T_safe limit")
    parser.add_argument("--close", nargs="*", default=[], help="Extra road IDs to treat as closed")
    parser.add_argument("--theta", type=float, help="Override thetaPerMinute")
    parser.add_argument("--compliance", type=float, help="Override complianceShare: share reached by the system (0-1)")
    parser.add_argument("--hazard-weight", type=float, help="Override hazardTimeWeight (lambda >= 1)")
    parser.add_argument("--sweep", type=str, help="Comma-separated compliance shares to compare, e.g. 0,0.2,0.5,1")
    parser.add_argument("--baseline", action="store_true", help="Also evaluate the current 1/time formula")
    parser.add_argument("--node", type=int, help="Print the distribution for one OSM node")
    parser.add_argument("--output", type=Path, default=OUT / "valencia-result.json")
    args = parser.parse_args()
    try:
        if args.sweep:
            print(f"{'compliance':>10} {'everyone':>9} {'guided':>8} {'uninformed':>11} {'p50':>6} {'p95':>6} {'overflow':>9}")
            for value in [float(item) for item in args.sweep.split(",")]:
                args.compliance = value
                result = build(args).run()
                clearance = result["clearanceMinutes"]
                print(f"{value:>10.0%} {on_time(result['statusAll']):>9.1%} {on_time(result['status']):>8.1%} "
                      f"{on_time(result['uninformed']['status']):>11.1%} {clearance['p50'] or '-':>6} "
                      f"{clearance['p95'] or '-':>6} {sum(item['overflow'] for item in result['shelters']):>9,}")
            return
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
