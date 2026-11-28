using System.Net;
using System.Text.Json;
using Cassini.Demo;

namespace Cassini.Dispatch;

internal sealed record RouteLookup(TransportRoute? Route, bool ServiceAvailable, string? ErrorCode = null);
internal sealed record RoutingProblem(string? Code);
internal sealed record CasePosition(string Node, string Progress);
internal sealed record CaseLookup(CasePosition? Case, bool ServiceAvailable);
internal sealed record SituationRevision(long Revision);
internal sealed record RoadRestriction(string Id, bool Closed, long? Revision, RoadBounds? Bounds, DateTimeOffset? ClosedAt);
internal sealed record RestrictionSnapshot(long Revision, bool HasHistory, RoadRestriction[] Roads);
internal sealed record RestrictionLookup(RestrictionSnapshot? Snapshot, string? ErrorCode = null);

internal sealed class ManagementClient(HttpClient client)
{
    public async Task<CaseLookup> GetPosition(string caseId, CancellationToken cancellation)
    {
        try
        {
            using var response = await client.GetAsync($"/queries/cases/{Uri.EscapeDataString(caseId)}", cancellation);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(null, true);
            if (!response.IsSuccessStatusCode)
                return new(null, false);

            var position = await response.Content.ReadFromJsonAsync<CasePosition>(cancellation);
            return position is { Node.Length: > 0, Progress.Length: > 0 }
                ? new(position, true) : new(null, false);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException ||
            exception is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            return new(null, false);
        }
    }
}

internal sealed class RoutingClient(HttpClient client)
{
    public async Task<RestrictionLookup> GetRestrictions(long since, CancellationToken cancellation)
    {
        try
        {
            using var response = await client.GetAsync($"/queries/assistance-restrictions?since={since}", cancellation);
            if (!response.IsSuccessStatusCode)
            {
                var problem = await response.Content.ReadFromJsonAsync<RoutingProblem>(cancellation);
                return new(null, problem?.Code == "SituationUnavailable" ? problem.Code : "RoutingUnavailable");
            }
            var snapshot = await response.Content.ReadFromJsonAsync<RestrictionSnapshot>(cancellation);
            if (snapshot is { Revision: >= 0, Roads: not null })
                return new(snapshot);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException ||
            exception is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            // Lack of fresh route information remains visible as an unverified route.
        }
        return new(null, "RoutingUnavailable");
    }

    public async Task<RouteLookup> GetTransportRoute(string from, string pickup, CancellationToken cancellation)
    {
        try
        {
            var path = $"/queries/assistance-route?from={Uri.EscapeDataString(from)}&pickup={Uri.EscapeDataString(pickup)}";
            using var response = await client.GetAsync(path, cancellation);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                return new(null, true);

            if (!response.IsSuccessStatusCode)
            {
                var problem = await response.Content.ReadFromJsonAsync<RoutingProblem>(cancellation);
                return new(null, false, problem?.Code);
            }

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
