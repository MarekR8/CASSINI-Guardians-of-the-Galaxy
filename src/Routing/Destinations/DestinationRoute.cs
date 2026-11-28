using System.Security.Cryptography;
using System.Text.Json;
using Cassini.Demo.SiteCatalog;

namespace Cassini.Routing.Destinations;

internal sealed record TravelCase(string CaseId, string Node, string Mobility, int Version, DateTimeOffset? PositionObservedAt);
internal sealed record TravelRelationship(string Kind, string CaseId, string RelatedCaseId);
internal sealed record TravelSubject(
    string IncidentId, int? GroupRevision, TravelCase[] Cases, TravelRelationship[]? Relationships);
internal sealed record TravelContext(
    string IncidentId, string LegId, string Origin, string Mode, bool StepFree, DateTimeOffset At,
    DateTimeOffset? ClearBy, string? GroupId, int? GroupRevision, TravelCase[] Cases)
{
    public bool RequiresAssistance => Cases.Any(person => person.Mobility == "Assisted");
}

internal sealed record DestinationRouteRequest(
    string? IncidentId, string? LegId, string? Node, string? Mode, bool? StepFree,
    DateTimeOffset? At, DateTimeOffset? ClearBy, string? GroupId, int? GroupRevision, string? CaseIds);
internal sealed record DestinationRef(string Kind, string Id);
internal sealed record ArrivalWindow(DateTimeOffset Earliest, DateTimeOffset LatestExclusive);
internal sealed record DestinationRouteEvidence(
    string EvidenceId, long SituationRevision, int? SiteCatalogRevision, int? SiteVersion,
    string Source, string Mode, DateTimeOffset ObservedAt, DateTimeOffset ValidUntil,
    SiteEvidence? Site, string? RecordedRouteId, string? TransportId);
internal sealed record DestinationRoute(
    string Status, string? Reason, DestinationRef Destination, TravelContext Travel, long SituationRevision,
    int? SiteCatalogRevision = null, int? SiteVersion = null, string? AvailabilityReason = null,
    string[]? Nodes = null, string[]? Segments = null, DateTimeOffset? DepartsAt = null,
    DateTimeOffset? ClearsHazardAt = null, ArrivalWindow? ArrivalWindow = null, DestinationRouteEvidence? Evidence = null);

internal sealed record DestinationSite(
    string? PointId, string? ShelterId, string? IncidentId, SitePlace? Place, bool? StepFree,
    string? Status, SiteWindow? OperatingWindow, SiteEvidence? Evidence, int? Version,
    AvailableSiteResources? Available, bool? Overcommitted);

internal sealed record AvailableSiteResources(int? Places, int? AccessiblePlaces);

internal static class DestinationRouteEvaluator
{
    internal static DestinationRoute Evaluate(SituationSnapshot snapshot, TravelContext travel, DestinationRef destination,
        string? target, DestinationSite? site, int? catalogRevision, RecordedDestinationRoute? recorded = null,
        TransportWindow? transport = null)
    {
        var result = new DestinationRoute("Ineligible", null, destination, travel, snapshot.Revision,
            catalogRevision, site?.Version);
        if (!Network.Contains(target))
            return result with { Reason = "DestinationGraphNodeUnverified" };
        if (!Network.Exits.Contains(target!))
            return result with { Reason = "DestinationOutsideSafeAreaUnverified" };
        if (site is not null && travel.StepFree && site.StepFree != true)
            return result with { Reason = "DestinationAccessibilityUnverified" };

        var readyAt = transport is not null && travel.At < transport.DepartsFrom ? transport.DepartsFrom : travel.At;
        var departsAt = RoutingQueries.SampleDeparture(readyAt, target!);
        if (transport is not null && departsAt >= transport.DepartsBefore)
            return result with { Reason = "TransportWindowUnavailable" };
        if (departsAt >= RoutingQueries.ScheduleCloses)
            return result with { Reason = "DepartureWindowClosed" };

        var closed = snapshot.ClosedSegments.ToHashSet(StringComparer.Ordinal);
        var driving = travel.Mode == "driving";
        bool CanTraverse(Segment segment, string from, string to) =>
            (!Network.Exits.Contains(from) || Network.Exits.Contains(to)) && (!travel.StepFree || segment.StepFree == true);
        var path = recorded is null
            ? Network.FindPath(travel.Origin, target!, driving, closed, CanTraverse)
            : MatchRecording(recorded, travel.Origin, target!, driving, closed, CanTraverse);
        if (path is null)
        {
            var accessiblePathMissing = travel.StepFree && Network.FindPath(travel.Origin, target!, driving, closed,
                (_, from, to) => !Network.Exits.Contains(from) || Network.Exits.Contains(to)) is not null;
            return result with { Reason = recorded is not null ? "RecordedRouteIneligible"
                : accessiblePathMissing ? "RouteAccessibilityUnverified" : "NoEligibleRoute" };
        }

        var arrivesAt = departsAt.AddSeconds(path.Seconds);
        var arrival = new ArrivalWindow(arrivesAt, arrivesAt.AddSeconds(1));
        if (transport is not null && arrival.LatestExclusive > transport.ArrivesBefore)
            return result with { Reason = "TransportWindowUnavailable" };
        var clearsAt = travel.At;
        if (!Network.Exits.Contains(travel.Origin))
        {
            var elapsed = 0;
            for (var index = 0; index < path.Segments.Length; index++)
            {
                elapsed += Network.SegmentSeconds(
                    Network.Segments.Single(segment => segment.Id == path.Segments[index]), driving)!.Value;
                if (Network.Exits.Contains(path.Nodes[index + 1]))
                    break;
            }
            clearsAt = departsAt.AddSeconds(elapsed);
            if (clearsAt > travel.ClearBy)
                return result with { Reason = "ClearanceDeadlineMissed" };
        }

        var validUntil = transport is null ? RoutingQueries.ScheduleCloses
            : Earlier(RoutingQueries.ScheduleCloses, transport.ArrivesBefore);
        if (site is not null)
        {
            if (site.OperatingWindow is null || site.Evidence is null || site.Status is null || site.Version is not > 0)
                return result with { Reason = "DestinationEvidenceMissing" };
            var siteReason = SiteRules.IneligibleReason(site.Status, site.OperatingWindow, site.Evidence,
                arrivesAt, checked((int)snapshot.Revision));
            if (siteReason is not null)
                return result with { Reason = siteReason };
            if (site.Evidence.ObservedAt > travel.At)
                return result with { Reason = "DestinationEvidenceNotYetObserved" };
            validUntil = Earlier(validUntil, site.Evidence.ValidUntil!.Value);
            if (site.OperatingWindow.ClosesAt is { } closes)
                validUntil = Earlier(validUntil, closes);
        }

        var observedAt = RoutingQueries.ScenarioTime(snapshot.Stage);
        if (recorded is not null)
        {
            if (recorded.ObservedAt > travel.At)
                return result with { Reason = "RecordedEvidenceNotYetObserved" };
            observedAt = recorded.ObservedAt;
            validUntil = Earlier(validUntil, recorded.ValidUntil);
        }
        if (arrival.LatestExclusive > validUntil)
            return result with { Reason = "RouteEvidenceExpiresBeforeArrival" };

        var source = recorded?.Source ?? "sample-graph";
        var mode = recorded?.Mode ?? "synthetic";
        var evidenceData = new { travel, destination, snapshot.Revision, catalogRevision, SiteVersion = site?.Version,
            path.Segments, departsAt, arrival, source, mode, observedAt, validUntil, transport, RecordedRouteId = recorded?.RouteId };
        var evidenceId = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(evidenceData)));
        return result with
        {
            Status = "Eligible", AvailabilityReason = ShelterAvailability(site, travel.StepFree),
            Nodes = path.Nodes, Segments = path.Segments, DepartsAt = departsAt, ClearsHazardAt = clearsAt,
            ArrivalWindow = arrival, Evidence = new(evidenceId, snapshot.Revision, catalogRevision, site?.Version,
                source, mode, observedAt, validUntil, site?.Evidence, recorded?.RouteId, transport?.TransportId)
        };
    }

    private static GraphPath? MatchRecording(RecordedDestinationRoute recording, string origin, string target,
        bool driving, HashSet<string> closed, Func<Segment, string, string, bool> canTraverse)
    {
        if (recording.Nodes.Length != recording.Segments.Length + 1 || recording.Nodes[0] != origin ||
            recording.Nodes[^1] != target)
            return null;

        var total = 0;
        for (var index = 0; index < recording.Segments.Length; index++)
        {
            var id = recording.Segments[index];
            var from = recording.Nodes[index];
            var to = recording.Nodes[index + 1];
            var segment = Network.Segments.FirstOrDefault(segment => segment.Id == id);
            if (segment is null || closed.Contains(id) ||
                !((segment.From == from && segment.To == to) || (segment.BothWays && segment.To == from && segment.From == to)) ||
                Network.SegmentSeconds(segment, driving) is not { } seconds || !canTraverse(segment, from, to))
                return null;
            total += seconds;
        }
        return new(recording.Nodes, recording.Segments, total);
    }

    private static string? ShelterAvailability(DestinationSite? site, bool stepFree)
    {
        if (site?.ShelterId is null)
            return null;
        if (site.Overcommitted is null || site.Available?.Places is not >= 0 || site.Available.AccessiblePlaces is not >= 0)
            return "ShelterAvailabilityUnverified";
        if (site.Overcommitted.Value)
            return "Overcommitted";
        if (site.Available.Places == 0)
            return "NoAvailablePlaces";
        return stepFree && site.Available.AccessiblePlaces == 0 ? "NoAccessiblePlaces" : null;
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
