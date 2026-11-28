using System.Globalization;
using System.Net;
using System.Text.Json;
using Cassini.Demo.SiteCatalog;
using Cassini.Sheltering.Allocation;

namespace Cassini.Sheltering.Infrastructure;

internal sealed record RouteTravelCase(string CaseId);
internal sealed record RouteTravel(DateTimeOffset At, string Origin, RouteTravelCase[]? Cases);
internal sealed record RouteEvidenceView(string EvidenceId, int? SiteVersion);
internal sealed record RouteCheck(
    string Status, string? Reason, long SituationRevision, int? SiteVersion, string? AvailabilityReason, RouteTravel? Travel,
    DateTimeOffset? DepartsAt, ArrivalWindow? ArrivalWindow, RouteEvidenceView? Evidence)
{
    internal bool Eligible => Status == "Eligible" && ArrivalWindow is not null && Evidence is not null;
}

/// <summary>Repeats Routing's read-only destination check (SH-W3). It never publishes or withdraws guidance.</summary>
internal sealed class RoutingClient(HttpClient client)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<RouteCheck> CheckShelterRoute(AssignmentRef assignment, string shelterId, RouteQuery query,
        string? expectedEvidenceId, CancellationToken cancellation)
    {
        var args = new List<(string, string?)>
        {
            ("incidentId", assignment.IncidentId), ("legId", assignment.LegId),
            ("destinationKind", "shelter"), ("destinationId", shelterId),
            ("mode", query.Mode), ("at", Time(query.At)), ("node", query.Node), ("caseIds", query.CaseIds),
            ("clearBy", Time(query.ClearBy)), ("stepFree", query.StepFree is { } stepFree ? (stepFree ? "true" : "false") : null),
            ("groupId", query.GroupId), ("groupRevision", query.GroupRevision?.ToString(CultureInfo.InvariantCulture)),
            ("transportId", query.TransportId), ("recordedRouteId", query.RecordedRouteId),
            ("expectedEvidenceId", expectedEvidenceId),
        };
        var path = "/queries/destination-route?" + string.Join('&', args.Where(arg => arg.Item2 is not null)
            .Select(arg => $"{arg.Item1}={Uri.EscapeDataString(arg.Item2!)}"));

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(path, cancellation);
        }
        catch (Exception error) when (error is HttpRequestException ||
            error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            throw Unavailable();
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
            {
                // The route inputs no longer resolve (time, subject revision or party constraints): not eligible.
                var code = await ProblemCode(response, cancellation) ?? "RouteQueryRejected";
                return new RouteCheck("Ineligible", code, -1, null, null, null, null, null, null);
            }
            if (!response.IsSuccessStatusCode)
                throw Unavailable();
            try
            {
                return await response.Content.ReadFromJsonAsync<RouteCheck>(Json, cancellation) ?? throw Unavailable();
            }
            catch (JsonException)
            {
                throw Unavailable();
            }
        }
    }

    private static async Task<string?> ProblemCode(HttpResponseMessage response, CancellationToken cancellation)
    {
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            return problem.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Time(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal static SiteCatalogError Unavailable() =>
        new(503, "RoutingUnavailable", "The current route evidence could not be verified with Routing.");
}

internal sealed record PlanCase(string CaseId);
internal sealed record PlanLink(string Kind, string CaseId, string RelatedCaseId);
internal sealed record PlanProposal(string? LegId, PlanCase[]? Cases, PlanLink[]? RequiredCare);
internal sealed record ApprovedPlan(int PlanRevision, string Status, PlanProposal? Proposal)
{
    internal bool Includes(string caseId) => Proposal?.Cases?.Any(person => person.CaseId == caseId) == true;
}

/// <summary>Reads the approved travel plan from Grouping (SH-W2), hosted in Management.</summary>
internal sealed class GroupingClient(HttpClient client)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<ApprovedPlan?> GetPlan(GroupPlanRef reference, CancellationToken cancellation)
    {
        var path = $"/queries/approved-travel-plans/{Uri.EscapeDataString(reference.GroupId!)}" +
            $"?incidentId={Uri.EscapeDataString(reference.IncidentId!)}";
        try
        {
            using var response = await client.GetAsync(path, cancellation);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
                throw Unavailable();
            return await response.Content.ReadFromJsonAsync<ApprovedPlan>(Json, cancellation) ?? throw Unavailable();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException ||
            error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            throw Unavailable();
        }
    }

    internal static SiteCatalogError Unavailable() =>
        new(503, "GroupingUnavailable", "The approved travel plan could not be verified with Grouping.");
}
