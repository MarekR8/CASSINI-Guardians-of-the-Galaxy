using System.Net;
using System.Text.Json;

namespace Cassini.Dispatch;

internal sealed record RouteLookup(TransportRoute? Route, bool ServiceAvailable);
internal sealed record SituationRevision(long Revision);

internal sealed class RoutingClient(HttpClient client)
{
    public async Task<RouteLookup> GetTransportRoute(string from, string pickup, CancellationToken cancellation)
    {
        try
        {
            var path = $"/queries/assistance-route?from={Uri.EscapeDataString(from)}&pickup={Uri.EscapeDataString(pickup)}";
            using var response = await client.GetAsync(path, cancellation);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                return new(null, true);

            if (!response.IsSuccessStatusCode)
                return new(null, false);

            var route = await response.Content.ReadFromJsonAsync<TransportRoute>(cancellation);
            return route is { Revision: >= 0, Exit.Length: > 0, ApproachSegments: not null, OutboundSegments: not null }
                ? new(route, true) : new(null, false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException ||
            exception is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            return new(null, false);
        }
    }
}

internal sealed class SituationClient(HttpClient client)
{
    public async Task<long?> GetRevision(CancellationToken cancellation)
    {
        try
        {
            var snapshot = await client.GetFromJsonAsync<SituationRevision>("/queries/snapshot", cancellation);
            return snapshot?.Revision;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException ||
            exception is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            return null;
        }
    }
}
