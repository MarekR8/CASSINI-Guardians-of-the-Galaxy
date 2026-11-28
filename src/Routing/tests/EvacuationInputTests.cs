using System.Text;
using Cassini.Routing.Evacuation;

/// <summary>Loader checks: the shipped Valencia inputs and client shelter files.</summary>
internal static class EvacuationInputTests
{
    internal static void Run()
    {
        ValenciaInputsLoad();
        ShelterFiles();
        Console.WriteLine("Evacuation inputs load and shelter files are validated.");
    }

    private static void ValenciaInputsLoad()
    {
        var input = EvacuationInputs.ReadDirectory(Path.Combine(AppContext.BaseDirectory, "evacuation"));
        Check(input.Graph.Nodes.Length > 60_000 && input.Graph.Roads.Length > 90_000, "The Valencia graph package loads.");
        Check(input.People.Values.Sum() > 100_000, "The GHSL estimate covers the observed flood.");
        Check(input.People.Keys.All(input.Deadlines.ContainsKey), "Every populated node has a deadline.");
        var zones = input.Graph.Nodes.ToDictionary(node => node.Id, node => node.Zone);
        Check(input.People.Keys.All(node => zones[node] == "hazard"), "People are placed on hazard nodes only.");
        Check(input.Deadlines.Values.All(minutes => minutes is >= 1 and <= 600), "Deadlines are plausible minutes.");
        Check(input.Shelters.Length > 0 && input.Shelters.All(shelter => zones[shelter.NodeId] == "safe"),
            "Exercise shelters are on dry nodes.");
        Check(input.Params.Profiles.Select(profile => profile.Name).SequenceEqual(["independent", "slow", "wheelchair"]) &&
              Math.Abs(input.Params.Profiles.Sum(profile => profile.Share) + input.Params.HandedOverShare - 1) < 1e-9,
            "Profile shares and the handed-over share add up to the whole population.");
    }

    private static void ShelterFiles()
    {
        // Fixture graph: hazard nodes 1 and 2, dry nodes 10 and 20 at longitudes 0.001-0.004, latitude 0.005.
        var fixture = EvacuationInputs.ReadCase(Path.Combine(AppContext.BaseDirectory, "routing-fixtures", "cases", "baseline.json"));
        var directory = Directory.CreateTempSubdirectory("cassini-shelters-");
        try
        {
            EvacShelter[] Read(string shelters, bool byteOrderMark = false)
            {
                var path = Path.Combine(directory.FullName, Guid.NewGuid().ToString("N") + ".json");
                var text = $$"""{"sheltersVersion": "cassini.shelters.v1", "scenario": "test", "provenance": {"source": "test", "mode": "client"}, "shelters": [{{shelters}}]}""";
                File.WriteAllText(path, text, new UTF8Encoding(byteOrderMark));
                return EvacuationInputs.ReadShelters(path, fixture.Graph, fixture.Longitude, fixture.Latitude);
            }

            var read = Read("""
                {"id": "by-node", "name": "A", "nodeId": 10, "capacity": 100, "stepFree": true},
                {"id": "by-position", "name": "B", "longitude": 0.00405, "latitude": 0.005, "capacity": 50, "intakePerHour": 20}
                """, byteOrderMark: true);
            Check(read.Length == 2 && read[0].NodeId == 10 && read[0].StepFree && read[0].IntakePerHour is null,
                "A shelter is placed on its nodeId; a file with a byte order mark is accepted.");
            Check(read[1].NodeId == 20 && !read[1].StepFree && read[1].IntakePerHour == 20,
                "A shelter given by position snaps to the nearest walkable node.");
            Rejects(() => Read("""{"id": "wet", "name": "C", "nodeId": 1, "capacity": 10}"""), "hazard");
            Rejects(() => Read("""{"id": "far", "name": "D", "longitude": 0.004, "latitude": 0.009, "capacity": 10}"""), "250");
            Rejects(() => Read("""{"id": "ghost", "name": "E", "nodeId": 999, "capacity": 10}"""), "999");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void Rejects(Action action, string expected)
    {
        try
        {
            action();
        }
        catch (InvalidDataException error) when (error.Message.Contains(expected, StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException($"Expected the shelter file to be rejected with '{expected}'.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }
}
