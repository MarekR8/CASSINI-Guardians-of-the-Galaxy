using System.Net.Http.Json;
using System.Text.Json;

namespace Cassini.Routing.Destinations;

internal sealed record DestinationSources(Uri Situation, Uri Sheltering);
internal sealed record AssemblyPointCatalog(int? Revision, int? SituationRevision, DateTimeOffset? At,
    JsonElement[]? AssemblyPoints);
internal sealed record ShelterAvailabilityCatalog(int? Revision, int? SituationRevision, DateTimeOffset? At,
    JsonElement[]? Shelters);
internal sealed record DestinationCatalog(string IncidentId, int SituationRevision, DateTimeOffset At,
    int AssemblyPointRevision, int ShelterRevision, JsonElement[] AssemblyPoints, JsonElement[] Shelters);

internal sealed class DestinationQueries(HttpClient http, DestinationSources sources, TimeProvider clock)
{
    internal async Task<DestinationCatalog> GetDestinations(string? incidentId, DateTimeOffset? at,
        CancellationToken cancellation)
    {
        if (incidentId is not { Length: > 0 and <= 120 } ||
            !incidentId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new RoutingException(400, "InvalidIncident", "Specify a valid incidentId.");

        var instant = (at ?? clock.GetUtcNow()).ToUniversalTime();
        var timeQuery = "&at=" + Uri.EscapeDataString(instant.ToString("O"));
        var pointsPath = $"queries/assembly-points?incidentId={Uri.EscapeDataString(incidentId)}&eligibleOnly=true{timeQuery}";
        var sheltersPath = "queries/shelter-availability?eligibleOnly=true" + timeQuery;

        var assemblyPoints = Read<AssemblyPointCatalog>(new Uri(sources.Situation, pointsPath), cancellation);
        var shelters = Read<ShelterAvailabilityCatalog>(new Uri(sources.Sheltering, sheltersPath), cancellation);
        await Task.WhenAll(assemblyPoints, shelters);

        var points = await assemblyPoints;
        var availability = await shelters;
        if (points.Revision is not >= 0 || points.SituationRevision is not >= 0 ||
            availability.Revision is not >= 0 || availability.SituationRevision is not >= 0 ||
            points.At != instant || availability.At != instant ||
            points.AssemblyPoints is null || availability.Shelters is null ||
            !points.AssemblyPoints.All(IsEligible) || !availability.Shelters.All(IsEligible))
            throw Unavailable();

        if (points.SituationRevision != availability.SituationRevision)
            throw new RoutingException(503, "DestinationEvidenceChanged",
                "The site catalogs were checked against different situation revisions. Read again.");

        return new DestinationCatalog(incidentId, points.SituationRevision.Value, instant, points.Revision.Value,
            availability.Revision.Value, points.AssemblyPoints, availability.Shelters);
    }

    private async Task<T> Read<T>(Uri uri, CancellationToken cancellation)
    {
        try
        {
            return await http.GetFromJsonAsync<T>(uri, cancellation) ?? throw Unavailable();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException ||
            (error is OperationCanceledException && !cancellation.IsCancellationRequested))
        {
            throw Unavailable();
        }
    }

    private static bool IsEligible(JsonElement item) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty("eligible", out var eligible) && eligible.ValueKind == JsonValueKind.True;

    private static RoutingException Unavailable() => new(503, "DestinationCatalogUnavailable",
        "Current assembly point and shelter evidence could not be read.");
}
