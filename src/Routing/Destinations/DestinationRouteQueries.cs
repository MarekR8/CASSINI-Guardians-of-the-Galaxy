using System.Text.Json;

namespace Cassini.Routing.Destinations;

internal sealed record RouteDecisionReason(DestinationRef? Destination, string Code);
internal sealed record FirstLeg(
    string Status, string ShelterStatus, long SituationRevision, DestinationRoute? Route,
    RouteDecisionReason[] Reasons, string? ReservationId = null, string? TransportId = null, string? FixtureMode = null);

internal sealed class DestinationRouteQueries(
    SituationGateway situation, DestinationQueries destinations, DestinationEvidenceFixtures fixtures)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<DestinationRoute> GetRoute(DestinationRouteRequest request, string? destinationKind,
        string? destinationId, string? recordedRouteId, string? expectedEvidenceId, string? transportId,
        CancellationToken cancellation)
    {
        if (destinationKind is not ("exit" or "assemblyPoint" or "shelter") || !DestinationQueries.ValidId(destinationId))
            throw new RoutingException(400, "InvalidDestination", "Specify a typed destination ID.");
        if (expectedEvidenceId is not null && (expectedEvidenceId.Length != 64 ||
            !expectedEvidenceId.All(char.IsAsciiHexDigitLower)))
            throw new RoutingException(400, "InvalidEvidenceId", "Use the evidence ID returned by Routing.");

        var snapshot = await situation.ReadAsync(cancellation);
        var travel = await ReadTravel(request, snapshot, cancellation);
        var destination = new DestinationRef(destinationKind, destinationId!);
        var recorded = fixtures.Routes.SingleOrDefault(route => route.RouteId == recordedRouteId);
        if (recordedRouteId is not null && (recorded is null || recorded.TravelMode != travel.Mode))
            return new("Ineligible", "RecordedRouteUnavailable", destination, travel, snapshot.Revision);

        var transport = ReadTransport(transportId, travel, snapshot);
        if (transport is null && (transportId is not null || (travel.RequiresAssistance && expectedEvidenceId is not null)))
            return new("Ineligible", TransportReason(transportId), destination, travel, snapshot.Revision);

        var result = await EvaluateNamed(snapshot, travel, destination, recorded, transport, cancellation);
        if (expectedEvidenceId is not null && result.Evidence?.EvidenceId != expectedEvidenceId)
            return new("Ineligible", result.Reason ?? "RouteEvidenceChanged", destination, travel, snapshot.Revision,
                result.SiteCatalogRevision, result.SiteVersion);
        if (expectedEvidenceId is not null)
        {
            var current = await situation.ReadAsync(cancellation);
            if (current.Revision != snapshot.Revision)
                return new("Ineligible", "RouteEvidenceChanged", destination, travel, current.Revision);
        }
        return result;
    }

    internal async Task<FirstLeg> GetFirstLeg(DestinationRouteRequest request, string? reservationId, string? transportId,
        CancellationToken cancellation)
    {
        var snapshot = await situation.ReadAsync(cancellation);
        var travel = await ReadTravel(request, snapshot, cancellation);
        var reasons = new List<RouteDecisionReason>();
        var transport = ReadTransport(transportId, travel, snapshot);
        if (travel.RequiresAssistance && transport is null)
        {
            var reason = new RouteDecisionReason(null, TransportReason(transportId));
            return Network.Exits.Contains(travel.Origin)
                ? new("AlreadyOutsideHazard", "Pending", snapshot.Revision, null, [reason])
                : new("EscalationRequired", "Pending", snapshot.Revision, null, [reason, new(null, "NoEligibleFirstLeg")]);
        }

        if (reservationId is not null)
        {
            var booking = fixtures.Bookings.SingleOrDefault(item => item.ReservationId == reservationId);
            if (booking is not null && booking.Status == "Reserved" && booking.SituationRevision == snapshot.Revision &&
                booking.IncidentId == travel.IncidentId && booking.LegId == travel.LegId &&
                travel.Cases.Length > 0 && DestinationEvidenceFixtures.SameCases(booking.CaseIds, travel) &&
                (!travel.StepFree || booking.AccessiblePlacesHeld >= travel.Cases.Length) &&
                travel.At >= booking.ValidFrom && travel.At < booking.ValidUntil)
            {
                var reference = new DestinationRef("shelter", booking.ShelterId);
                try
                {
                    var route = await EvaluateNamed(snapshot, travel, reference, null, transport, cancellation);
                    if (route.Status == "Eligible" && route.SiteVersion == booking.ShelterVersion &&
                        route.AvailabilityReason is not ("Overcommitted" or "ShelterAvailabilityUnverified") &&
                        route.ArrivalWindow!.LatestExclusive <= booking.ValidUntil)
                    {
                        return new("DirectShelter", "Reserved", snapshot.Revision, route, [], booking.ReservationId,
                            transport?.TransportId, "synthetic");
                    }
                    else
                        reasons.Add(new(reference, route.Reason ?? "ReservationRequiresReview"));
                }
                catch (RoutingException error) when (error.Code == "DestinationCatalogUnavailable")
                {
                    reasons.Add(new(reference, error.Code));
                }
            }
            else
                reasons.Add(new(null, "ReservationUnverified"));
        }

        if (Network.Exits.Contains(travel.Origin))
            return new("AlreadyOutsideHazard", "Pending", snapshot.Revision, null, reasons.ToArray());

        var fallback = new List<DestinationRoute>();
        try
        {
            var points = await destinations.ReadPoints(travel.IncidentId, travel.At, cancellation);
            CheckCatalog(snapshot, travel, points.Revision, points.SituationRevision, points.At);
            foreach (var point in ParseSites(points.AssemblyPoints))
            {
                var reference = new DestinationRef("assemblyPoint", point.PointId!);
                if (!DestinationQueries.ValidId(point.PointId) || point.IncidentId != travel.IncidentId)
                    throw Unavailable();
                fallback.Add(DestinationRouteEvaluator.Evaluate(snapshot, travel, reference, point.Place?.GraphNodeId,
                    point, points.Revision, transport: transport));
            }
        }
        catch (RoutingException error) when (error.Code == "DestinationCatalogUnavailable")
        {
            fallback.Clear();
            reasons.Add(new(null, error.Code));
        }
        fallback.AddRange(Network.Exits.Select(exit =>
            DestinationRouteEvaluator.Evaluate(snapshot, travel, new("exit", exit), exit, null, null, transport: transport)));

        foreach (var route in fallback.Where(route => route.Status != "Eligible"))
            reasons.Add(new(route.Destination, route.Reason!));
        var selected = fallback.Where(route => route.Status == "Eligible")
            .OrderBy(route => route.ArrivalWindow!.Earliest)
            .ThenBy(route => route.Destination.Kind == "exit")
            .ThenBy(route => route.Destination.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return selected is null
            ? new("EscalationRequired", "Pending", snapshot.Revision, null,
                [.. reasons, new(null, "NoEligibleFirstLeg")])
            : new("ShelterPending", "Pending", snapshot.Revision, selected, reasons.ToArray(),
                TransportId: transport?.TransportId, FixtureMode: transport is null ? null : "synthetic");
    }

    private async Task<DestinationRoute> EvaluateNamed(SituationSnapshot snapshot, TravelContext travel,
        DestinationRef reference, RecordedDestinationRoute? recorded, TransportWindow? transport, CancellationToken cancellation)
    {
        if (reference.Kind == "exit")
            return DestinationRouteEvaluator.Evaluate(snapshot, travel, reference, reference.Id, null, null, recorded, transport);

        DestinationSite? site;
        int? catalogRevision;
        if (reference.Kind == "assemblyPoint")
        {
            var catalog = await destinations.ReadPoints(travel.IncidentId, travel.At, cancellation);
            CheckCatalog(snapshot, travel, catalog.Revision, catalog.SituationRevision, catalog.At);
            site = ParseSites(catalog.AssemblyPoints).SingleOrDefault(point =>
                point.PointId == reference.Id && point.IncidentId == travel.IncidentId);
            catalogRevision = catalog.Revision;
        }
        else
        {
            var catalog = await destinations.ReadShelters(travel.At, cancellation);
            CheckCatalog(snapshot, travel, catalog.Revision, catalog.SituationRevision, catalog.At);
            site = ParseSites(catalog.Shelters).SingleOrDefault(shelter => shelter.ShelterId == reference.Id);
            catalogRevision = catalog.Revision;
        }
        return site is null
            ? new("Ineligible", "DestinationNotFound", reference, travel, snapshot.Revision, catalogRevision)
            : DestinationRouteEvaluator.Evaluate(snapshot, travel, reference, site.Place?.GraphNodeId,
                site, catalogRevision, recorded, transport);
    }

    private TransportWindow? ReadTransport(string? transportId, TravelContext travel, SituationSnapshot snapshot) =>
        fixtures.TransportWindows.SingleOrDefault(window => window.TransportId == transportId &&
            window.SituationRevision == snapshot.Revision && travel.Mode == "driving" &&
            window.IncidentId == travel.IncidentId && window.Origin == travel.Origin &&
            (!travel.StepFree || window.StepFree) &&
            DestinationEvidenceFixtures.SameCases(window.CaseIds, travel));

    private static string TransportReason(string? transportId) =>
        transportId is null ? "TransportEvidenceMissing" : "TransportWindowUnavailable";

    private async Task<TravelContext> ReadTravel(DestinationRouteRequest request, SituationSnapshot snapshot,
        CancellationToken cancellation)
    {
        var ids = request.CaseIds?.Split(',') ?? [];
        if (!DestinationQueries.ValidId(request.IncidentId) || !DestinationQueries.ValidId(request.LegId) ||
            request.Mode is not ("walking" or "driving") || ids.Length > 32 ||
            !ids.All(DestinationQueries.ValidId) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length ||
            (request.GroupId is null && (ids.Length > 1 || request.GroupRevision is not null)) ||
            (request.GroupId is not null && (!DestinationQueries.ValidId(request.GroupId) ||
                request.GroupRevision is not > 0 || ids.Length == 0)))
            throw new RoutingException(400, "InvalidTravelRequest",
                "Provide an incident, leg, travel mode and valid optional case or versioned group references.");

        var currentTime = RoutingQueries.ScenarioTime(snapshot.Stage);
        var at = (request.At ?? currentTime).ToUniversalTime();
        if (at < currentTime)
            throw new RoutingException(400, "InvalidTime", "The requested instant precedes the exercise clock.");

        TravelCase[] cases = [];
        if (ids.Length > 0)
        {
            var subject = await destinations.ReadSubject(request.IncidentId!, request.GroupId, cancellation);
            if (subject.IncidentId != request.IncidentId || subject.Cases is null ||
                (request.GroupId is not null &&
                    (subject.GroupRevision != request.GroupRevision || subject.Relationships is null)))
                throw new RoutingException(409, "TravelSubjectChanged", "Refresh the incident and group revision.");
            cases = subject.Cases.Where(person => ids.Contains(person.CaseId))
                .OrderBy(person => person.CaseId, StringComparer.Ordinal).ToArray();
            if (cases.Length != ids.Length || cases.Any(person => person.Version <= 0 ||
                person.Mobility is not ("Walking" or "Assisted")))
                throw new RoutingException(422, "CaseRequirementsUnverified", "Verify the current case and mobility needs.");
            if (cases.Any(person => person.PositionObservedAt > at))
                throw new RoutingException(422, "PositionNotYetObserved", "Use an instant at or after the current position observation.");

            if (subject.Relationships?.Any(link => link.Kind == "required-care" &&
                ids.Contains(link.CaseId) != ids.Contains(link.RelatedCaseId)) == true)
                throw new RoutingException(422, "RequiredCareSplit", "Include both members of each required-care link.");
            if (request.Mode != "driving" && cases.Any(person => person.Mobility == "Assisted"))
                throw new RoutingException(422, "TravelModeIncompatible", "The requested party needs assisted transport.");
        }

        var origin = request.Node ?? cases.FirstOrDefault()?.Node;
        if (!Network.Contains(origin))
            throw new RoutingException(400, "InvalidOrigin", "Provide a known origin node or a located case.");
        if (cases.Any(person => person.Node != origin))
            throw new RoutingException(422, "PartyOriginsDiffer", "Use separately verified departures or a verified meeting plan.");
        if (!Network.Exits.Contains(origin!) && request.ClearBy is null)
            throw new RoutingException(400, "ClearanceDeadlineRequired", "Provide an evacuation clearance deadline.");
        return new(request.IncidentId!, request.LegId!, origin!, request.Mode, request.StepFree == true,
            at, request.ClearBy?.ToUniversalTime(), request.GroupId, request.GroupRevision, cases);
    }

    private static void CheckCatalog(SituationSnapshot snapshot, TravelContext travel, int? revision,
        int? situationRevision, DateTimeOffset? at)
    {
        if (revision is not >= 0 || situationRevision is not >= 0 || at != travel.At)
            throw Unavailable();
        if (situationRevision != snapshot.Revision)
            throw new RoutingException(503, "DestinationEvidenceChanged", "The site and graph used different Situation revisions.");
    }

    private static DestinationSite[] ParseSites(JsonElement[]? entries)
    {
        try
        {
            return entries?.Select(item => item.Deserialize<DestinationSite>(Json) ?? throw Unavailable()).ToArray()
                ?? throw Unavailable();
        }
        catch (JsonException)
        {
            throw Unavailable();
        }
    }

    private static RoutingException Unavailable() => new(503, "DestinationCatalogUnavailable",
        "The site owner did not supply a verifiable catalog.");
}
