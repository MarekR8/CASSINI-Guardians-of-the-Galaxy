using System.Text.Json;
using System.Text.Json.Serialization;

namespace Situation;

internal sealed record PopulationEstimate([property: JsonRequired] int Value, string Method, string Source, int Year);

internal interface IPopulationEstimator
{
    PopulationEstimate? Estimate(string hazardId);
}

internal sealed class MockGhslEstimator : IPopulationEstimator
{
    private readonly Dictionary<string, PopulationEstimate> estimates;

    internal MockGhslEstimator(string path)
    {
        estimates = JsonSerializer.Deserialize<Dictionary<string, PopulationEstimate>>(
            File.ReadAllBytes(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("The GHSL estimates file is empty.");
    }

    public PopulationEstimate? Estimate(string hazardId) => estimates.GetValueOrDefault(hazardId);
}
