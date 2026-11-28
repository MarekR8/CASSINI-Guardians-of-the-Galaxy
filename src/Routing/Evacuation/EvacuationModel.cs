namespace Cassini.Routing.Evacuation;

// C# port of experiments/routing/model.py. Keep the two in step: data/demo/routing/expected.json is produced by the
// Python model and checked here with a tolerance of 1e-6.

internal sealed record EvacNode(long Id, string Zone);
internal sealed record EvacRoad(string Id, long From, long To, string Class, double?[] Walk, double? PedestriansPerHour,
    string Zone, bool Closed);
internal sealed record EvacShelter(string Id, string Name, long NodeId, int Capacity, double? IntakePerHour, bool StepFree);
internal sealed record EvacProfile(string Name, double Share, double SpeedFactor, string[] ExcludedClasses, bool StepFreeOnly);
internal sealed record EvacSettings(
    double ThetaPerMinute, double CapacityFactor, double BprAlpha, double BprBeta, double ShelterDelayMinutes,
    double ShelterDelayBeta, double CapacityPenaltyMinutes, int Waves, double WaveStaggerMinutes,
    double WaveWindowMinutes, int MaxIterations, double Tolerance);
internal sealed record EvacParams(EvacSettings Model, EvacProfile[] Profiles, double HandedOverShare);
internal sealed record EvacGraph(EvacNode[] Nodes, EvacRoad[] Roads);

internal sealed record GroupResult(
    double People, int Wave, double StartMinute, double DeadlineMinutes, string Status, double? LeaveFloodMinutes,
    double[]? Shares, double[] RouteSeconds);
internal sealed record ShelterLoad(string Id, string Name, int Capacity, double ExpectedLoad, double Overflow);
internal sealed record WaveSummary(int Wave, double StartMinute, double People, int Iterations, double? Gap);
internal sealed record EvacuationResult(
    Dictionary<string, double> Status, ShelterLoad[] Shelters, Dictionary<long, Dictionary<string, GroupResult>> Nodes,
    WaveSummary[] Waves, double PeopleEstimated, double PeopleHandedOver);

internal static class Congestion
{
    // BPR alpha * r^beta up to capacity, continued along its tangent above it (queueing, not divergence).
    internal static double Term(double ratio, double alpha, double beta) =>
        ratio <= 1 ? alpha * Math.Pow(ratio, beta) : alpha + alpha * beta * (ratio - 1);
}

/// <summary>Pedestrian arcs usable by one profile, one arc per ordered node pair, sorted by (start, end).</summary>
internal sealed class ProfileNetwork
{
    internal readonly int[] Start, End, Road;
    internal readonly double[] Base;
    private readonly int[][] incoming;
    private readonly Dictionary<(int, int), int> arcOf = new();

    internal ProfileNetwork(EvacGraph graph, Dictionary<long, int> index, EvacProfile profile, IReadOnlySet<string> closed)
    {
        var excluded = profile.ExcludedClasses.ToHashSet(StringComparer.Ordinal);
        var best = new Dictionary<(int, int), (double Cost, int Road)>();
        for (var number = 0; number < graph.Roads.Length; number++)
        {
            var road = graph.Roads[number];
            if (road.Closed || closed.Contains(road.Id) || excluded.Contains(road.Class))
                continue;
            int a = index[road.From], b = index[road.To];
            foreach (var (start, end, seconds) in new[] { (a, b, road.Walk[0]), (b, a, road.Walk[1]) })
            {
                if (seconds is null)
                    continue;
                if (graph.Nodes[start].Zone == "safe" && road.Zone == "hazard")
                    continue; // no re-entry into the flood once on dry ground
                var cost = seconds.Value / profile.SpeedFactor;
                if (!best.TryGetValue((start, end), out var current) || cost < current.Cost)
                    best[(start, end)] = (cost, number);
            }
        }
        var pairs = best.Keys.OrderBy(pair => pair.Item1).ThenBy(pair => pair.Item2).ToArray();
        Start = pairs.Select(pair => pair.Item1).ToArray();
        End = pairs.Select(pair => pair.Item2).ToArray();
        Base = pairs.Select(pair => best[pair].Cost).ToArray();
        Road = pairs.Select(pair => best[pair].Road).ToArray();
        var lists = Enumerable.Range(0, graph.Nodes.Length).Select(_ => new List<int>()).ToArray();
        for (var arc = 0; arc < pairs.Length; arc++)
        {
            lists[End[arc]].Add(arc);
            arcOf[(Start[arc], End[arc])] = arc;
        }
        incoming = lists.Select(list => list.ToArray()).ToArray();
    }

    internal int ArcOf(int start, int end) => arcOf[(start, end)];

    /// <summary>Shortest paths from every node to one shelter: seconds and the next node towards the shelter.</summary>
    internal Tree ShortestTo(int shelter, double[] roadFactor)
    {
        var size = incoming.Length;
        var distance = new double[size];
        var next = new int[size];
        Array.Fill(distance, double.PositiveInfinity);
        Array.Fill(next, -1);
        var done = new bool[size];
        distance[shelter] = 0;
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(shelter, 0);
        while (queue.TryDequeue(out var node, out var priority))
        {
            if (done[node] || priority > distance[node])
                continue;
            done[node] = true;
            foreach (var arc in incoming[node])
            {
                var from = Start[arc];
                var candidate = distance[node] + Base[arc] * roadFactor[Road[arc]];
                if (candidate < distance[from])
                {
                    distance[from] = candidate;
                    next[from] = node;
                    queue.Enqueue(from, candidate);
                }
            }
        }
        return new Tree(distance, next);
    }
}

internal sealed class Tree
{
    internal readonly double[] Distance;
    internal readonly int[] Next;
    private int[]? ascending;

    internal Tree(double[] distance, int[] next)
    {
        Distance = distance;
        Next = next;
    }

    /// <summary>Reachable nodes, nearest first. Computed on first use so stored final trees stay small.</summary>
    private int[] Ascending => ascending ??= Enumerable.Range(0, Distance.Length)
        .Where(node => double.IsFinite(Distance[node])).OrderBy(node => Distance[node]).ToArray();

    internal void Compact() => ascending = null;

    /// <summary>Seconds until the path first reaches dry ground; infinity when the node cannot reach the shelter.</summary>
    internal double[] LeaveSeconds(bool[] dry)
    {
        var firstDry = new int[Distance.Length];
        Array.Fill(firstDry, -1);
        var leave = new double[Distance.Length];
        Array.Fill(leave, double.PositiveInfinity);
        foreach (var node in Ascending)
        {
            firstDry[node] = dry[node] ? node : Next[node] >= 0 ? firstDry[Next[node]] : -1;
            if (firstDry[node] >= 0)
                leave[node] = Distance[node] - Distance[firstDry[node]];
        }
        return leave;
    }

    /// <summary>Flow on each node's outgoing tree arc: its own demand plus everything routed through it.</summary>
    internal double[] Accumulate(double[] demand)
    {
        var flow = (double[])demand.Clone();
        for (var position = Ascending.Length - 1; position >= 0; position--)
        {
            var node = Ascending[position];
            if (flow[node] != 0 && Next[node] >= 0)
                flow[Next[node]] += flow[node];
        }
        return flow;
    }
}

internal sealed class EvacuationModel
{
    private sealed record Group(int Node, int Profile, double People, double Deadline);
    private sealed record Choice(double[]? Share, string Status, double[] Leave, double[] Distance);

    private readonly EvacGraph graph;
    private readonly EvacSettings settings;
    private readonly EvacProfile[] profiles;
    private readonly EvacShelter[] shelters;
    private readonly ProfileNetwork[] networks;
    private readonly int[] shelterNodes;
    private readonly double[] capacity, intake, capacityPerHour, roadSeconds;
    private readonly bool[] stepFree, dry;
    private readonly Group[] groups;
    private readonly double peopleTotal, handedOverShare;

    /// <summary>Final shortest-path trees per wave and profile, used to rebuild a group's route to a shelter.</summary>
    internal Tree[][][]? FinalTrees { get; private set; }

    internal EvacuationModel(EvacGraph graph, IReadOnlyDictionary<long, double> people,
        IReadOnlyDictionary<long, double> deadlines, EvacShelter[] shelters, EvacParams parameters,
        IEnumerable<string>? closed = null)
    {
        this.graph = graph;
        settings = parameters.Model;
        profiles = parameters.Profiles;
        handedOverShare = parameters.HandedOverShare;
        this.shelters = shelters;
        var index = new Dictionary<long, int>();
        for (var position = 0; position < graph.Nodes.Length; position++)
            index[graph.Nodes[position].Id] = position;
        Index = index;
        var closedSet = (closed ?? []).ToHashSet(StringComparer.Ordinal);
        networks = profiles.Select(profile => new ProfileNetwork(graph, index, profile, closedSet)).ToArray();
        shelterNodes = shelters.Select(shelter => index[shelter.NodeId]).ToArray();
        capacity = shelters.Select(shelter => (double)shelter.Capacity).ToArray();
        intake = shelters.Select(shelter => shelter.IntakePerHour ?? double.PositiveInfinity).ToArray();
        stepFree = shelters.Select(shelter => shelter.StepFree).ToArray();
        // Same rule as Python's `pedestriansPerHour or 1`: a missing or zero capacity counts as 1 person per hour.
        capacityPerHour = graph.Roads.Select(road => road.PedestriansPerHour is { } value && value != 0 ? value : 1).ToArray();
        roadSeconds = graph.Roads.Select(road =>
        {
            var values = road.Walk.Where(value => value is not null).Select(value => value!.Value).ToArray();
            return values.Length == 0 ? 0 : values.Average();
        }).ToArray();
        dry = graph.Nodes.Select(node => node.Zone == "safe").ToArray();
        peopleTotal = people.Values.Sum();
        groups = people.Select(pair => (Position: index[pair.Key], Count: pair.Value)).OrderBy(item => item.Position)
            .SelectMany(item => profiles.Select((profile, number) =>
                new Group(item.Position, number, item.Count * profile.Share, deadlines[graph.Nodes[item.Position].Id])))
            .ToArray();
    }

    internal IReadOnlyDictionary<long, int> Index { get; }
    internal EvacShelter[] Shelters => shelters;
    internal EvacProfile[] Profiles => profiles;

    // --- costs ---------------------------------------------------------------------------------------------------
    private double[] RoadFactor(double[] rate)
    {
        var factor = new double[rate.Length];
        for (var road = 0; road < rate.Length; road++)
            factor[road] = 1 + Congestion.Term(rate[road] / (capacityPerHour[road] * settings.CapacityFactor),
                settings.BprAlpha, settings.BprBeta);
        return factor;
    }

    private double[] ShelterDelay(double[] people, double[] residual)
    {
        var delay = new double[people.Length];
        for (var row = 0; row < people.Length; row++)
        {
            var rate = people[row] / (settings.WaveWindowMinutes / 60);
            var queue = settings.ShelterDelayMinutes * 60 * Congestion.Term(rate / intake[row], 1.0, settings.ShelterDelayBeta);
            var excess = Math.Max(0, people[row] / Math.Max(residual[row], 1) - 1);
            delay[row] = queue + settings.CapacityPenaltyMinutes * 60 * excess;
        }
        return delay;
    }

    private Tree[][] SolveTrees(double[] roadRate)
    {
        var factor = RoadFactor(roadRate);
        return networks.Select(network => shelterNodes.Select(node => network.ShortestTo(node, factor)).ToArray()).ToArray();
    }

    // --- choice ----------------------------------------------------------------------------------------------------
    private bool[] Routable(int profile, double[] distance) => distance.Select((value, row) =>
        double.IsFinite(value) && (!profiles[profile].StepFreeOnly || stepFree[row])).ToArray();

    private Choice[] Choose(IReadOnlyList<Group> members, Tree[][] trees, double[] shelterPeople, double[] residual,
        double startMinutes, bool deadlineCheck = true)
    {
        var theta = settings.ThetaPerMinute;
        var delay = ShelterDelay(shelterPeople, residual);
        var exits = trees.Select(perShelter => perShelter.Select(tree => tree.LeaveSeconds(dry)).ToArray()).ToArray();
        var result = new Choice[members.Count];
        for (var number = 0; number < members.Count; number++)
        {
            var group = members[number];
            var distance = trees[group.Profile].Select(tree => tree.Distance[group.Node]).ToArray();
            var leave = exits[group.Profile].Select(row => row[group.Node]).ToArray();
            var routable = Routable(group.Profile, distance);
            if (!routable.Any(value => value))
            {
                result[number] = new(null, "NoEligibleExit", leave, distance);
                continue;
            }
            var reachable = routable.Select((value, row) => value && residual[row] > 0).ToArray();
            if (!reachable.Any(value => value))
            {
                result[number] = new(null, "ShelterCapacityExhausted", leave, distance);
                continue;
            }
            var inTime = reachable.Select((value, row) =>
                value && (!deadlineCheck || startMinutes + leave[row] / 60 <= group.Deadline)).ToArray();
            var anyInTime = inTime.Any(value => value);
            var usable = anyInTime ? inTime : reachable;
            var cost = distance.Select((value, row) => (value + delay[row]) / 60).ToArray();
            var minimum = cost.Where((_, row) => usable[row]).Min();
            var share = cost.Select((value, row) => usable[row] ? Math.Exp(-theta * (value - minimum)) : 0).ToArray();
            var total = share.Sum();
            for (var row = 0; row < share.Length; row++)
                share[row] /= total;
            result[number] = new(share, anyInTime ? "Ok" : "DeadlineInfeasible", leave, distance);
        }
        return result;
    }

    private (double[] Road, double[] Shelter) Load(IReadOnlyList<Group> members, Choice[] choices, Tree[][] trees)
    {
        var roadPeople = new double[graph.Roads.Length];
        var shelterPeople = new double[shelters.Length];
        for (var profile = 0; profile < profiles.Length; profile++)
        {
            for (var row = 0; row < shelters.Length; row++)
            {
                var demand = new double[graph.Nodes.Length];
                var any = false;
                for (var number = 0; number < members.Count; number++)
                {
                    if (members[number].Profile != profile || choices[number].Share is not { } share)
                        continue;
                    demand[members[number].Node] += members[number].People * share[row];
                    any |= share[row] != 0;
                }
                if (!any)
                    continue;
                shelterPeople[row] += demand.Sum();
                var tree = trees[profile][row];
                var flow = tree.Accumulate(demand);
                for (var node = 0; node < flow.Length; node++)
                    if (flow[node] > 0 && tree.Next[node] >= 0)
                        roadPeople[networks[profile].Road[networks[profile].ArcOf(node, tree.Next[node])]] += flow[node];
            }
        }
        return (roadPeople, shelterPeople);
    }

    // --- waves -----------------------------------------------------------------------------------------------------
    private int[] PlanWaves()
    {
        var free = SolveTrees(new double[graph.Roads.Length]);
        var choices = Choose(groups, free, new double[shelters.Length], capacity, 0, deadlineCheck: false);
        var reserve = new double[groups.Length];
        for (var number = 0; number < groups.Length; number++)
        {
            var choice = choices[number];
            if (choice.Share is null)
            {
                reserve[number] = double.NegativeInfinity;
                continue;
            }
            var routable = Routable(groups[number].Profile, choice.Distance);
            var best = choice.Leave.Where((value, row) => routable[row] && double.IsFinite(value)).Min();
            reserve[number] = groups[number].Deadline - best / 60;
        }
        var order = Enumerable.Range(0, groups.Length).OrderBy(number => reserve[number]).ToArray();
        var total = groups.Sum(group => group.People);
        var wave = new int[groups.Length];
        var cumulative = 0.0;
        foreach (var number in order)
        {
            cumulative += groups[number].People;
            wave[number] = Math.Min((int)(cumulative / total * settings.Waves - 1e-9), settings.Waves - 1);
        }
        return wave;
    }

    internal EvacuationResult Run()
    {
        var waveOf = PlanWaves();
        var windowHours = settings.WaveWindowMinutes / 60;
        var background = new double[graph.Roads.Length];
        var residual = (double[])capacity.Clone();
        var outcome = new (Choice Choice, int Wave, double Start, double? Left, string Status)[groups.Length];
        var waves = new List<WaveSummary>();
        var finalTrees = new Tree[settings.Waves][][];
        for (var wave = 0; wave < settings.Waves; wave++)
        {
            var memberIndex = Enumerable.Range(0, groups.Length).Where(number => waveOf[number] == wave).ToArray();
            var members = memberIndex.Select(number => groups[number]).ToArray();
            var start = wave * settings.WaveStaggerMinutes;
            var xRoad = new double[graph.Roads.Length];
            var xShelter = new double[shelters.Length];
            var gap = double.PositiveInfinity;
            var iteration = 0;
            while (iteration < settings.MaxIterations)
            {
                iteration++;
                var rate = Add(background, xRoad, 1 / windowHours);
                var trees = SolveTrees(rate);
                var choices = Choose(members, trees, xShelter, residual, start);
                var (yRoad, yShelter) = Load(members, choices, trees);
                if (iteration == 1)
                {
                    (xRoad, xShelter) = (yRoad, yShelter);
                    continue;
                }
                // Relative gap: time spent on the current flows versus on the current best responses.
                var factor = RoadFactor(rate);
                double spent = 0, best = 0;
                for (var road = 0; road < xRoad.Length; road++)
                {
                    var seconds = roadSeconds[road] * factor[road];
                    spent += xRoad[road] * seconds;
                    best += yRoad[road] * seconds;
                }
                gap = Math.Abs(spent - best) / Math.Max(spent, 1e-9);
                var step = 1.0 / iteration;
                for (var road = 0; road < xRoad.Length; road++)
                    xRoad[road] += step * (yRoad[road] - xRoad[road]);
                for (var row = 0; row < xShelter.Length; row++)
                    xShelter[row] += step * (yShelter[row] - xShelter[row]);
                if (gap < settings.Tolerance)
                    break;
            }
            var finalRate = Add(background, xRoad, 1 / windowHours);
            var final = SolveTrees(finalRate);
            finalTrees[wave] = final;
            var finalChoices = Choose(members, final, xShelter, residual, start);
            foreach (var tree in final.SelectMany(perShelter => perShelter))
                tree.Compact();
            for (var number = 0; number < members.Length; number++)
            {
                var choice = finalChoices[number];
                var status = choice.Status;
                double? left = null;
                if (choice.Share is { } share)
                {
                    left = share.Select((value, row) => value * (double.IsFinite(choice.Leave[row]) ? choice.Leave[row] : 0)).Sum() / 60;
                    status = start + left <= members[number].Deadline ? "Ok" : "DeadlineInfeasible";
                }
                outcome[memberIndex[number]] = (choice, wave, start, left, status);
            }
            background = finalRate;
            for (var row = 0; row < residual.Length; row++)
                residual[row] -= xShelter[row];
            waves.Add(new(wave + 1, start, members.Sum(member => member.People), iteration,
                double.IsInfinity(gap) ? null : gap));
        }
        FinalTrees = finalTrees;
        return Report(outcome, waves);
    }

    private EvacuationResult Report((Choice Choice, int Wave, double Start, double? Left, string Status)[] outcome,
        List<WaveSummary> waves)
    {
        var status = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var loads = new double[shelters.Length];
        var nodes = new Dictionary<long, Dictionary<string, GroupResult>>();
        for (var number = 0; number < groups.Length; number++)
        {
            var group = groups[number];
            var item = outcome[number];
            status[item.Status] = status.GetValueOrDefault(item.Status) + group.People;
            if (item.Choice.Share is { } share)
                for (var row = 0; row < loads.Length; row++)
                    loads[row] += group.People * share[row];
            var id = graph.Nodes[group.Node].Id;
            if (!nodes.TryGetValue(id, out var perProfile))
                nodes[id] = perProfile = new(StringComparer.Ordinal);
            perProfile[profiles[group.Profile].Name] = new(group.People, item.Wave + 1, item.Start, group.Deadline,
                item.Status, item.Left, item.Choice.Share, item.Choice.Distance);
        }
        return new(new Dictionary<string, double>(status),
            shelters.Select((shelter, row) => new ShelterLoad(shelter.Id, shelter.Name, shelter.Capacity, loads[row],
                Math.Max(0, loads[row] - shelter.Capacity))).ToArray(),
            nodes, waves.ToArray(), peopleTotal, peopleTotal * handedOverShare);
    }

    /// <summary>Road IDs and node IDs of a group's route to a shelter under the final costs of its wave.</summary>
    internal (string[] Segments, long[] Nodes)? Route(long nodeId, string profile, int shelter, int wave)
    {
        if (FinalTrees is null || !Index.TryGetValue(nodeId, out var node))
            return null;
        var profileNumber = Array.FindIndex(profiles, item => item.Name == profile);
        if (profileNumber < 0)
            return null;
        var tree = FinalTrees[wave - 1][profileNumber][shelter];
        if (!double.IsFinite(tree.Distance[node]))
            return null;
        var segments = new List<string>();
        var path = new List<long> { nodeId };
        while (tree.Next[node] >= 0)
        {
            var next = tree.Next[node];
            segments.Add(graph.Roads[networks[profileNumber].Road[networks[profileNumber].ArcOf(node, next)]].Id);
            path.Add(graph.Nodes[next].Id);
            node = next;
        }
        return (segments.ToArray(), path.ToArray());
    }

    private static double[] Add(double[] left, double[] right, double scale)
    {
        var result = new double[left.Length];
        for (var index = 0; index < left.Length; index++)
            result[index] = left[index] + right[index] * scale;
        return result;
    }
}
