using System.Text.Json;
using Cassini.Routing.Evacuation;

/// <summary>Checks the C# port against data/demo/routing/expected.json from the Python reference model.</summary>
internal static class EvacuationFixtureTests
{
    internal static void Run()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "routing-fixtures");
        using var expectedJson = EvacuationInputs.ReadJson(Path.Combine(directory, "expected.json"));
        var tolerance = expectedJson.RootElement.GetProperty("tolerance").GetDouble();
        var cases = expectedJson.RootElement.GetProperty("cases");
        var count = 0;
        foreach (var expected in cases.EnumerateObject())
        {
            var input = EvacuationInputs.ReadCase(Path.Combine(directory, "cases", expected.Name + ".json"));
            var model = new EvacuationModel(input.Graph, input.People, input.Deadlines, input.Shelters, input.Params, input.Closed);
            var actual = model.Run();
            var errors = new List<string>();
            Compare(expected.Value, actual, tolerance, errors);
            if (errors.Count > 0)
                throw new InvalidOperationException($"Fixture {expected.Name} differs from expected.json:\n  " +
                                                    string.Join("\n  ", errors.Take(15)));
            CheckProperties(expected.Name, actual);
            count++;
        }
        if (count == 0)
            throw new InvalidOperationException("No routing fixtures were found.");
        Console.WriteLine($"Evacuation model matches expected.json for {count} fixture cases.");
    }

    private static void CompareStatus(string path, JsonElement expected, Dictionary<string, double> actual, double tolerance,
        List<string> errors)
    {
        var status = expected.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetDouble());
        if (!status.Keys.Order().SequenceEqual(actual.Keys.Order()))
            errors.Add($"{path} keys {string.Join(",", status.Keys)} != {string.Join(",", actual.Keys)}");
        foreach (var (key, value) in status)
            Near($"{path}.{key}", value, actual.GetValueOrDefault(key), tolerance, errors);
    }

    private static void Compare(JsonElement expected, EvacuationResult actual, double tolerance, List<string> errors)
    {
        CompareStatus("status", expected.GetProperty("status"), actual.Status, tolerance, errors);
        CompareStatus("statusAll", expected.GetProperty("statusAll"), actual.StatusAll, tolerance, errors);
        var uninformed = expected.GetProperty("uninformed");
        CompareStatus("uninformed.status", uninformed.GetProperty("status"), actual.UninformedStatus, tolerance, errors);
        foreach (var node in uninformed.GetProperty("nodes").EnumerateObject())
            foreach (var profile in node.Value.EnumerateObject())
            {
                var path = $"uninformed.nodes.{node.Name}.{profile.Name}";
                var group = actual.UninformedNodes[long.Parse(node.Name)][profile.Name];
                if (profile.Value.GetProperty("status").GetString() != group.Status)
                    errors.Add($"{path}.status {profile.Value.GetProperty("status").GetString()} != {group.Status}");
                var minutes = profile.Value.GetProperty("leaveFloodMinutes");
                if (minutes.ValueKind == JsonValueKind.Null != group.LeaveFloodMinutes is null)
                    errors.Add($"{path}.leaveFloodMinutes null mismatch");
                else if (group.LeaveFloodMinutes is { } value)
                    Near($"{path}.leaveFloodMinutes", minutes.GetDouble(), value, tolerance, errors);
            }
        if (uninformed.GetProperty("nodes").EnumerateObject().Count() != actual.UninformedNodes.Count)
            errors.Add("uninformed node count differs");

        var shelters = expected.GetProperty("shelters").EnumerateArray().ToArray();
        for (var row = 0; row < shelters.Length; row++)
            Near($"shelters.{shelters[row].GetProperty("id").GetString()}", shelters[row].GetProperty("expectedLoad").GetDouble(),
                actual.Shelters[row].ExpectedLoad, tolerance, errors);

        var waves = expected.GetProperty("waves").EnumerateArray().ToArray();
        if (waves.Length != actual.Waves.Length)
            errors.Add($"wave count {waves.Length} != {actual.Waves.Length}");
        for (var wave = 0; wave < Math.Min(waves.Length, actual.Waves.Length); wave++)
        {
            if (waves[wave].GetProperty("iterations").GetInt32() != actual.Waves[wave].Iterations)
                errors.Add($"wave {wave + 1} iterations {waves[wave].GetProperty("iterations").GetInt32()} != {actual.Waves[wave].Iterations}");
            if (waves[wave].GetProperty("priceIterations").GetInt32() != actual.Waves[wave].PriceIterations)
                errors.Add($"wave {wave + 1} price iterations {waves[wave].GetProperty("priceIterations").GetInt32()} != {actual.Waves[wave].PriceIterations}");
        }

        foreach (var node in expected.GetProperty("nodes").EnumerateObject())
        {
            var perProfile = actual.Nodes[long.Parse(node.Name)];
            foreach (var profile in node.Value.EnumerateObject())
            {
                var path = $"nodes.{node.Name}.{profile.Name}";
                var group = perProfile[profile.Name];
                var want = profile.Value;
                if (want.GetProperty("status").GetString() != group.Status)
                    errors.Add($"{path}.status {want.GetProperty("status").GetString()} != {group.Status}");
                if (want.GetProperty("wave").GetInt32() != group.Wave)
                    errors.Add($"{path}.wave {want.GetProperty("wave").GetInt32()} != {group.Wave}");
                var leave = want.GetProperty("leaveFloodMinutes");
                if (leave.ValueKind == JsonValueKind.Null != group.LeaveFloodMinutes is null)
                    errors.Add($"{path}.leaveFloodMinutes null mismatch");
                else if (group.LeaveFloodMinutes is { } minutes)
                    Near($"{path}.leaveFloodMinutes", leave.GetDouble(), minutes, tolerance, errors);
                var shares = want.GetProperty("shares");
                if (shares.ValueKind == JsonValueKind.Null != group.Shares is null)
                    errors.Add($"{path}.shares null mismatch");
                else if (group.Shares is { } actualShares)
                {
                    var row = 0;
                    foreach (var share in shares.EnumerateObject())
                        Near($"{path}.shares.{share.Name}", share.Value.GetDouble(), actualShares[row++], tolerance, errors);
                }
            }
        }
    }

    private static void CheckProperties(string name, EvacuationResult result)
    {
        foreach (var group in result.Nodes.Values.SelectMany(profile => profile.Values))
        {
            if (group.Shares is { } shares && Math.Abs(shares.Sum() - 1) > 1e-9)
                throw new InvalidOperationException($"{name}: shares do not sum to 1.");
            if (group.Status == "NoEligibleExit" && group.Shares is not null)
                throw new InvalidOperationException($"{name}: a group without an exit received a distribution.");
        }
        if (result.Waves.Any(wave => wave.Iterations >= 100))
            throw new InvalidOperationException($"{name}: MSA did not converge within 100 iterations.");
    }

    private static void Near(string path, double expected, double actual, double tolerance, List<string> errors)
    {
        if (Math.Abs(expected - actual) > tolerance * Math.Max(1, Math.Abs(expected)))
            errors.Add($"{path}: {expected:R} != {actual:R}");
    }
}
