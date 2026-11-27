using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cassini.Routing;

internal sealed record SituationSnapshot(
    [property: JsonRequired] long Revision,
    [property: JsonRequired] int Stage,
    [property: JsonRequired] string[] ClosedSegments,
    Dictionary<string, long>? SegmentRevisions);

internal sealed class SituationGateway(HttpClient client)
{
    internal async Task<SituationSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await client.GetFromJsonAsync<SituationSnapshot>("queries/snapshot", cancellationToken);
            if (snapshot is null || snapshot.Revision < 0 || snapshot.Stage is < 0 or > 2 ||
                snapshot.ClosedSegments is null || snapshot.ClosedSegments.Any(segment =>
                    !Network.Segments.Any(link => link.Id == segment)) ||
                snapshot.SegmentRevisions?.Values.Any(revision => revision < 0 || revision > snapshot.Revision) == true)
                throw new RoutingException(503, "SituationUnavailable", "Migawka warunków przejścia jest niepełna.");
            return snapshot;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException ||
                                          exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new RoutingException(503, "SituationUnavailable", "Lokalny serwis sytuacji jest niedostępny.");
        }
    }
}

internal sealed class RoutingException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
