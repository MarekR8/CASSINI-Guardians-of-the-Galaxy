using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net;
using System.Text.Json;
using Cassini.Routing.Publication;

namespace Cassini.Routing.Destinations;

internal sealed record DestinationSources(Uri Situation, Uri Sheltering, Uri Management, string? GroupingReadToken = null);
internal sealed record AssemblyPointCatalog(int? Revision, int? SituationRevision, DateTimeOffset? At,
    JsonElement[]? AssemblyPoints);
internal sealed record ShelterAvailabilityCatalog(int? Revision, int? SituationRevision, DateTimeOffset? At,
    JsonElement[]? Shelters);
internal sealed record DestinationCatalog(string IncidentId, int SituationRevision, DateTimeOffset At,
    int AssemblyPointRevision, int ShelterRevision, JsonElement[] AssemblyPoints, JsonElement[] Shelters);
internal sealed record ShelterAssignmentResponse(OwnedShelterAssignment? Assignment);
internal sealed record OwnedShelterAssignment(string IncidentId, string CaseId, string LegId, int Version,
    string Status, OwnedShelterHold? Reservation);
internal sealed record OwnedShelterHold(string ReservationId, string ShelterId, DateTimeOffset HoldsExpireAt,
    ArrivalWindow? ArrivalWindow, HeldResources? Resources, RouteProposal? Route)
{
    internal bool Covers(DestinationRoute route) =>
        route is { Status: "Eligible", ArrivalWindow: { } arrival,
            AvailabilityReason: not ("Overcommitted" or "ShelterAvailabilityUnverified") } &&
        ArrivalWindow is { } held && held.Earliest <= arrival.Earliest && arrival.LatestExclusive <= held.LatestExclusive &&
        arrival.LatestExclusive <= HoldsExpireAt &&
        Resources is { Places: > 0 } resources && (!route.Travel.StepFree || resources.AccessiblePlaces > 0);
}
internal sealed record HeldResources(int Places, int AccessiblePlaces);

internal sealed class DestinationQueries(HttpClient http, DestinationSources sources, TimeProvider clock)
{
    internal async Task<DestinationCatalog> GetDestinations(string? incidentId, DateTimeOffset? at,
        CancellationToken cancellation)
    {
        if (!ValidId(incidentId))
            throw new RoutingException(400, "InvalidIncident", "Specify a valid incidentId.");

        var instant = (at ?? clock.GetUtcNow()).ToUniversalTime();
        var assemblyPoints = ReadPoints(incidentId!, instant, cancellation, eligibleOnly: true);
        var shelters = ReadShelters(instant, cancellation, eligibleOnly: true);
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

        return new DestinationCatalog(incidentId!, points.SituationRevision.Value, instant, points.Revision.Value,
            availability.Revision.Value, points.AssemblyPoints, availability.Shelters);
    }

    internal Task<AssemblyPointCatalog> ReadPoints(string incidentId, DateTimeOffset at, CancellationToken cancellation,
        bool eligibleOnly = false) => Read<AssemblyPointCatalog>(new Uri(sources.Situation,
            $"queries/assembly-points?incidentId={Uri.EscapeDataString(incidentId)}&eligibleOnly={eligibleOnly}&at={Time(at)}"), cancellation);

    internal Task<ShelterAvailabilityCatalog> ReadShelters(DateTimeOffset at, CancellationToken cancellation,
        bool eligibleOnly = false) => Read<ShelterAvailabilityCatalog>(new Uri(sources.Sheltering,
            $"queries/shelter-availability?eligibleOnly={eligibleOnly}&at={Time(at)}"), cancellation);

    internal async Task<OwnedShelterAssignment?> ReadAssignment(string incidentId, string caseId, string legId,
        CancellationToken cancellation)
    {
        const string code = "ReservationEvidenceUnavailable";
        var path = $"queries/shelter-assignment?incidentId={Uri.EscapeDataString(incidentId)}" +
            $"&caseId={Uri.EscapeDataString(caseId)}&legId={Uri.EscapeDataString(legId)}";
        var response = await ReadResponse<ShelterAssignmentResponse>(new Uri(sources.Sheltering, path), cancellation, code,
            notFoundCode: "AssignmentNotFound");
        if (response is null)
            return null;
        if (response.Assignment is not { Version: > 0 } assignment || assignment.IncidentId != incidentId ||
            assignment.CaseId != caseId || assignment.LegId != legId)
            throw Unavailable(code);
        return assignment;
    }

    internal Task<TravelSubject> ReadSubject(string incidentId, string? groupId, CancellationToken cancellation)
    {
        var path = groupId is null ? "queries/incident"
            : $"queries/evacuation-groups/{Uri.EscapeDataString(groupId)}?incidentId={Uri.EscapeDataString(incidentId)}";
        return Read<TravelSubject>(new Uri(sources.Management, path), cancellation, "TravelSubjectUnavailable",
            groupId is null ? null : sources.GroupingReadToken);
    }

    private static string Time(DateTimeOffset at) => Uri.EscapeDataString(at.ToString("O"));

    internal static bool ValidId(string? value) => value is { Length: > 0 and <= 120 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private async Task<T> Read<T>(Uri uri, CancellationToken cancellation, string code = "DestinationCatalogUnavailable",
        string? bearer = null) where T : class =>
        await ReadResponse<T>(uri, cancellation, code, bearer) ?? throw Unavailable(code);

    private async Task<T?> ReadResponse<T>(Uri uri, CancellationToken cancellation, string code, string? bearer = null,
        string? notFoundCode = null) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(bearer))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await http.SendAsync(request, cancellation);
            if (notFoundCode is not null && response.StatusCode == HttpStatusCode.NotFound &&
                (await response.Content.ReadFromJsonAsync<JsonElement>(cancellation)) is { ValueKind: JsonValueKind.Object } problem &&
                problem.TryGetProperty("code", out var reason) && reason.ValueKind == JsonValueKind.String &&
                reason.GetString() == notFoundCode)
                return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(cancellation) ?? throw Unavailable(code);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or NotSupportedException ||
            (error is OperationCanceledException && !cancellation.IsCancellationRequested))
        {
            throw Unavailable(code);
        }
    }

    private static bool IsEligible(JsonElement item) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty("eligible", out var eligible) && eligible.ValueKind == JsonValueKind.True;

    private static RoutingException Unavailable(string code = "DestinationCatalogUnavailable") => new(503, code,
        "Current evidence could not be read from the owning service.");
}
