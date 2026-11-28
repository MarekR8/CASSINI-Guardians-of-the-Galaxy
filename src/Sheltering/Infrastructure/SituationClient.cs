using System.Text.Json;
using Cassini.Demo.SiteCatalog;

namespace Cassini.Sheltering.Infrastructure;

internal sealed class SituationClient(HttpClient client)
{
    private sealed record RevisionView(int? Revision);

    internal async Task<int?> GetRevision(CancellationToken cancellation)
    {
        try
        {
            var snapshot = await client.GetFromJsonAsync<RevisionView>("/queries/snapshot", cancellation);
            return snapshot?.Revision is >= 0 ? snapshot.Revision : null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException ||
            error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static SiteCatalogError Unavailable() =>
        new(503, "SituationUnavailable", "The current Situation revision could not be verified.");
}
