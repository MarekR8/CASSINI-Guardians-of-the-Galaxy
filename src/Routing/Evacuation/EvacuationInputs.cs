using System.IO.Compression;
using System.Text.Json;

namespace Cassini.Routing.Evacuation;

/// <summary>A complete model input: one graph version, people, deadlines, shelters, parameters and closures.</summary>
internal sealed record EvacuationCase(
    EvacGraph Graph, Dictionary<long, double> People, Dictionary<long, double> Deadlines, EvacShelter[] Shelters,
    EvacParams Params, string[] Closed, double[] Longitude, double[] Latitude);

internal static class EvacuationInputs
{
    internal const string InputVersion = "cassini.evacuation-input.v1";
    private const double SnapMetres = 250;

    internal static JsonDocument ReadJson(string path)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // Files saved by Windows tools may start with a UTF-8 byte order mark; Python reads them as utf-8-sig.
            var bytes = File.ReadAllBytes(path);
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return JsonDocument.Parse(bytes.AsMemory(start));
        }
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(entry => entry.FullName.EndsWith("-graph.json", StringComparison.Ordinal)).ToArray();
        if (entries.Length != 1)
            throw new InvalidDataException($"{Path.GetFileName(path)}: expected exactly one *-graph.json entry.");
        using var stream = entries[0].Open();
        return JsonDocument.Parse(stream);
    }

    internal static (EvacGraph Graph, double[] Longitude, double[] Latitude) ReadGraph(JsonElement document)
    {
        if (document.GetProperty("inputVersion").GetString() != InputVersion)
            throw new InvalidDataException($"inputVersion must be {InputVersion}.");
        var nodes = document.GetProperty("nodes").EnumerateArray().ToArray();
        var roads = document.GetProperty("roads").EnumerateArray().Select(road => new EvacRoad(
            road.GetProperty("id").GetString()!, road.GetProperty("from").GetInt64(), road.GetProperty("to").GetInt64(),
            road.GetProperty("class").GetString()!,
            road.GetProperty("walk").EnumerateArray().Select(NullableDouble).ToArray(),
            NullableDouble(road.GetProperty("pedestriansPerHour")), road.GetProperty("zone").GetString()!,
            road.GetProperty("closed").GetBoolean())).ToArray();
        return (new EvacGraph(nodes.Select(node => new EvacNode(node.GetProperty("id").GetInt64(),
                node.GetProperty("zone").GetString()!)).ToArray(), roads),
            nodes.Select(node => node.GetProperty("longitude").GetDouble()).ToArray(),
            nodes.Select(node => node.GetProperty("latitude").GetDouble()).ToArray());
    }

    internal static EvacParams ReadParams(JsonElement document)
    {
        var model = document.GetProperty("model");
        var bpr = model.GetProperty("bpr");
        var delay = model.GetProperty("shelterDelay");
        var settings = new EvacSettings(
            model.GetProperty("thetaPerMinute").GetDouble(), model.GetProperty("capacityFactor").GetDouble(),
            bpr.GetProperty("alpha").GetDouble(), bpr.GetProperty("beta").GetDouble(),
            delay.GetProperty("minutes").GetDouble(), delay.GetProperty("beta").GetDouble(),
            model.GetProperty("capacityPriceStepMinutes").GetDouble(), model.GetProperty("capacityTolerance").GetDouble(),
            model.GetProperty("maxPriceIterations").GetInt32(), model.GetProperty("waves").GetInt32(),
            model.GetProperty("waveStaggerMinutes").GetDouble(), model.GetProperty("waveWindowMinutes").GetDouble(),
            model.GetProperty("maxIterations").GetInt32(), model.GetProperty("tolerance").GetDouble(),
            model.TryGetProperty("complianceShare", out var compliance) ? compliance.GetDouble() : 1.0,
            model.TryGetProperty("hazardTimeWeight", out var hazardWeight) ? hazardWeight.GetDouble() : 1.0);
        var profiles = new List<EvacProfile>();
        var handedOver = 0.0;
        foreach (var property in document.GetProperty("profiles").EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue; // notes
            var value = property.Value;
            if (value.TryGetProperty("handedOver", out var flag) && flag.GetBoolean())
            {
                handedOver += value.GetProperty("share").GetDouble();
                continue;
            }
            profiles.Add(new(property.Name, value.GetProperty("share").GetDouble(), value.GetProperty("speedFactor").GetDouble(),
                value.GetProperty("excludedClasses").EnumerateArray().Select(item => item.GetString()!).ToArray(),
                value.GetProperty("stepFreeOnly").GetBoolean()));
        }
        return new(settings, profiles.ToArray(), handedOver);
    }

    /// <summary>A self-contained fixture from data/demo/routing/cases.</summary>
    internal static EvacuationCase ReadCase(string path)
    {
        using var json = ReadJson(path);
        var root = json.RootElement;
        var (graph, longitude, latitude) = ReadGraph(root.GetProperty("graph"));
        return new(graph,
            root.GetProperty("people").EnumerateArray().ToDictionary(
                item => item.GetProperty("nodeId").GetInt64(), item => item.GetProperty("people").GetDouble()),
            root.GetProperty("deadlines").EnumerateArray().ToDictionary(
                item => item.GetProperty("nodeId").GetInt64(), item => item.GetProperty("minutes").GetDouble()),
            root.GetProperty("shelters").EnumerateArray().Select(item => ReadShelter(item, item.GetProperty("nodeId").GetInt64())).ToArray(),
            ReadParams(root.GetProperty("params")),
            root.GetProperty("closed").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            longitude, latitude);
    }

    /// <summary>The Valencia inputs prepared by experiments/routing (graph package, population, deadlines, shelters).</summary>
    internal static EvacuationCase ReadDirectory(string directory, string? shelterFile = null)
    {
        using var graphJson = ReadJson(Path.Combine(directory, "valencia-task03.zip"));
        var (graph, longitude, latitude) = ReadGraph(graphJson.RootElement);
        using var paramsJson = ReadJson(Path.Combine(directory, "params.json"));
        using var populationJson = ReadJson(Path.Combine(directory, "valencia", "population.json"));
        using var deadlineJson = ReadJson(Path.Combine(directory, "valencia", "deadlines.json"));
        var people = populationJson.RootElement.GetProperty("nodes").EnumerateArray().ToDictionary(
            item => item.GetProperty("nodeId").GetInt64(), item => item.GetProperty("people").GetDouble());
        var deadlines = deadlineJson.RootElement.GetProperty("nodes").EnumerateArray().ToDictionary(
            item => item.GetProperty("nodeId").GetInt64(), item => item.GetProperty("deadlineMinutes").GetDouble());
        var missing = people.Keys.Where(node => !deadlines.ContainsKey(node)).Take(3).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"No deadline for populated nodes {string.Join(", ", missing)}.");
        var shelters = ReadShelters(shelterFile ?? Path.Combine(directory, "valencia", "shelters.synthetic.json"),
            graph, longitude, latitude);
        return new(graph, people, deadlines, shelters, ReadParams(paramsJson.RootElement), [], longitude, latitude);
    }

    /// <summary>Read cassini.shelters.v1; place each shelter on its nodeId or the nearest walkable node within 250 m.</summary>
    internal static EvacShelter[] ReadShelters(string path, EvacGraph graph, double[] longitude, double[] latitude)
    {
        using var json = ReadJson(path);
        var root = json.RootElement;
        if (root.GetProperty("sheltersVersion").GetString() != "cassini.shelters.v1")
            throw new InvalidDataException("sheltersVersion must be cassini.shelters.v1.");
        var index = new Dictionary<long, int>();
        for (var position = 0; position < graph.Nodes.Length; position++)
            index[graph.Nodes[position].Id] = position;
        var walkable = new bool[graph.Nodes.Length];
        foreach (var road in graph.Roads.Where(road => !road.Closed && road.Walk.Any(value => value is not null)))
            walkable[index[road.From]] = walkable[index[road.To]] = true;
        var result = new List<EvacShelter>();
        foreach (var item in root.GetProperty("shelters").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            int position;
            if (item.TryGetProperty("nodeId", out var node) && node.ValueKind == JsonValueKind.Number)
            {
                if (!index.TryGetValue(node.GetInt64(), out position))
                    throw new InvalidDataException($"Shelter {id}: nodeId {node.GetInt64()} is not in the graph.");
            }
            else
            {
                var x = item.GetProperty("longitude").GetDouble();
                var y = item.GetProperty("latitude").GetDouble();
                var scale = 111_320 * Math.Cos(y * Math.PI / 180);
                position = Enumerable.Range(0, graph.Nodes.Length).Where(candidate => walkable[candidate])
                    .MinBy(candidate => Math.Pow((longitude[candidate] - x) * scale, 2) + Math.Pow((latitude[candidate] - y) * 111_320, 2));
                var metres = Math.Sqrt(Math.Pow((longitude[position] - x) * scale, 2) + Math.Pow((latitude[position] - y) * 111_320, 2));
                if (metres > SnapMetres)
                    throw new InvalidDataException($"Shelter {id}: no pedestrian road within {SnapMetres} m.");
            }
            if (graph.Nodes[position].Zone != "safe")
                throw new InvalidDataException($"Shelter {id} is in the {graph.Nodes[position].Zone} zone.");
            result.Add(ReadShelter(item, graph.Nodes[position].Id));
        }
        return result.ToArray();
    }

    private static EvacShelter ReadShelter(JsonElement item, long nodeId) => new(
        item.GetProperty("id").GetString()!,
        item.TryGetProperty("name", out var name) ? name.GetString()! : item.GetProperty("id").GetString()!,
        nodeId, item.GetProperty("capacity").GetInt32(),
        item.TryGetProperty("intakePerHour", out var intake) ? NullableDouble(intake) : null,
        item.TryGetProperty("stepFree", out var stepFree) && stepFree.GetBoolean());

    private static double? NullableDouble(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();
}
