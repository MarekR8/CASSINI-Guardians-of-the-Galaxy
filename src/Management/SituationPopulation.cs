using System.Text.Json;

namespace Cassini.Management;

/// <summary>Population estimate for the hazard zone as published by Situation (GHSL, task 1).</summary>
internal sealed record PopulationEstimate(int Value, string? Method, string? Source, int? Year, long Revision);

internal interface ISituationPopulation
{
    /// <summary>Returns null when Situation is unreachable or publishes no estimate; never a guessed value.</summary>
    Task<PopulationEstimate?> ReadEstimate(CancellationToken cancellation);
}

internal sealed class SituationPopulationClient(HttpClient client) : ISituationPopulation
{
    private sealed record Snapshot(long Revision, Estimate? PopulationEstimate);
    private sealed record Estimate(int? Value, string? Method, string? Source, int? Year);

    public async Task<PopulationEstimate?> ReadEstimate(CancellationToken cancellation)
    {
        try
        {
            var snapshot = await client.GetFromJsonAsync<Snapshot>("/queries/snapshot", ManagementStore.Json, cancellation);
            return snapshot?.PopulationEstimate is { Value: { } value } estimate && value >= 0
                ? new(value, estimate.Method, estimate.Source, estimate.Year, snapshot.Revision)
                : null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or NotSupportedException ||
            exception is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            return null;
        }
    }
}
