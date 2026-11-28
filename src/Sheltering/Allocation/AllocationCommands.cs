using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cassini.Demo.SiteCatalog;
using Cassini.Messaging;
using Cassini.Sheltering.Catalog;
using Cassini.Sheltering.Infrastructure;
using Cassini.Sheltering.Reception;
using ReceptionHold = Cassini.Sheltering.Reception.ShelterHold;

namespace Cassini.Sheltering.Allocation;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OpenShelterAssignmentCommand(
    string? CommandId, string? CorrelationId, string? IncidentId, string? CaseId, string? LegId, PlaceNeeds? Needs,
    GroupPlanRef? GroupPlanRef);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AssignmentTarget(
    string? IncidentId, string? CaseId, string? LegId, int? ExpectedVersion, RouteEvidenceRef? Route);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReserveShelterPlacesCommand(
    string? CommandId, string? CorrelationId, string? ShelterId, int? ExpectedShelterVersion, GroupPlanRef? GroupPlanRef,
    DateTimeOffset? HoldsExpireAt, string? ApprovalRef, AssignmentTarget[]? Assignments);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReassignShelterPlacesCommand(
    string? CommandId, string? CorrelationId, string? FromShelterId, int? ExpectedFromShelterVersion, string? ShelterId,
    int? ExpectedShelterVersion, string? Reason, GroupPlanRef? GroupPlanRef, DateTimeOffset? HoldsExpireAt,
    string? ApprovalRef, AssignmentTarget[]? Assignments);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InvalidateShelterAssignmentCommand(
    string? CommandId, string? CorrelationId, string? IncidentId, string? CaseId, string? LegId, int? ExpectedVersion,
    string? Reason, string? EvidenceRef, DateTimeOffset? At);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReleaseShelterReservationCommand(
    string? CommandId, string? CorrelationId, string? IncidentId, string? CaseId, string? LegId, int? ExpectedVersion,
    string? Reason, bool? StillRequired, string? ApprovalRef, DateTimeOffset? ObservedAt);

internal sealed record AssignmentWrite(int Revision, ShelterAssignment Assignment);
internal sealed record BatchWrite(int Revision, string ShelterId, string? FromShelterId, ShelterAssignment[] Assignments);

internal sealed record ShelterAssignmentChangedPayload(
    string IncidentId, string CaseId, string LegId, int AssignmentVersion, string Status, string? ShelterId,
    string? ReservationId, ArrivalWindow? ArrivalWindow, string? WithdrawnShelterId, string? WithdrawnReservationId,
    int? WithdrawnAssignmentVersion, string? Reason);

internal sealed record ShelterAvailabilityChangedPayload(
    string ShelterId, int ShelterVersion, int AllocationRevision, PlaceNeeds? Held, string Reason);

internal sealed class AllocationCommands(
    AllocationStore store, RoutingClient routing, GroupingClient grouping, TimeProvider clock)
{
    private const string Producer = "sheltering";
    private const int MaxBatch = 32;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<AssignmentWrite> Open(OpenShelterAssignmentCommand command, CancellationToken cancellation)
    {
        RequireCommand(command.CommandId);
        var reference = RequireRef(command.IncidentId, command.CaseId, command.LegId);
        if (command.Needs is not { IsValid: true } needs)
            throw Invalid("InvalidNeeds", "Specify 1 to 10 places and accessible places within that total.");
        if (command.GroupPlanRef is { } planRef && (!planRef.IsValid || planRef.IncidentId != reference.IncidentId))
            throw Invalid("InvalidGroupPlanRef", "Reference an approved plan revision in the same incident.");
        if (store.TryReplay(command.CommandId!, command, out AssignmentWrite replay))
            return replay;

        if (command.GroupPlanRef is { } plan)
            RequirePlan(await grouping.GetPlan(plan, cancellation), plan, [reference.CaseId]);

        return store.Execute<AssignmentWrite>(command.CommandId!, command, state =>
        {
            if (state.Find(reference) is { } existing)
            {
                if (existing.Status == AssignmentStatus.Closed || existing.Needs != needs ||
                    existing.GroupPlanRef != command.GroupPlanRef)
                    throw new SiteCatalogError(409, "AssignmentAlreadyOpen",
                        "This case and leg already has an assignment with different requirements.");
                return (new AllocationChange([], [], []), revision => new AssignmentWrite(state.State.Revision, existing));
            }

            var opened = new ShelterAssignment(reference.IncidentId, reference.CaseId, reference.LegId, 1,
                AssignmentStatus.Pending, needs, command.GroupPlanRef, null, null, null);
            return (new AllocationChange([opened], [], [AssignmentChanged(opened, command, "Opened")]),
                revision => new AssignmentWrite(revision, opened));
        });
    }

    internal async Task<BatchWrite> Reserve(ReserveShelterPlacesCommand command, CancellationToken cancellation)
    {
        RequireCommand(command.CommandId);
        var targets = RequireTargets(command.Assignments, command.ShelterId, command.ExpectedShelterVersion,
            command.HoldsExpireAt, command.ApprovalRef, command.GroupPlanRef);
        if (store.TryReplay(command.CommandId!, command, out BatchWrite replay))
            return replay;

        var checks = await CheckRoutes(targets, command.ShelterId!, cancellation);
        var plan = command.GroupPlanRef is { } planRef ? await grouping.GetPlan(planRef, cancellation) : null;

        return store.Execute<BatchWrite>(command.CommandId!, command, state =>
        {
            var current = targets.Select(target =>
            {
                var assignment = RequireAssignment(state, target);
                if (assignment.Status is not (AssignmentStatus.Pending or AssignmentStatus.AwaitingReplacement))
                    throw assignment.Status switch
                    {
                        AssignmentStatus.Reserved => new SiteCatalogError(409, "AssignmentAlreadyReserved",
                            "Use ReassignShelterPlaces to move an existing reservation."),
                        AssignmentStatus.Admitted => new SiteCatalogError(422, "AlreadyAdmitted",
                            "An admitted person keeps their occupied place until departure is confirmed."),
                        _ => new SiteCatalogError(409, "AssignmentClosed", "The assignment is closed."),
                    };
                return assignment;
            }).ToArray();

            var shelter = RequireShelter(state, command.ShelterId!, command.ExpectedShelterVersion!.Value);
            var (held, booked) = PlaceHolds(state, shelter, current, targets, checks, plan, command.GroupPlanRef,
                command.HoldsExpireAt!.Value, command.ApprovalRef!, command.CommandId!, _ => null);
            var revision = state.State.Revision + 1;
            var events = held.Select(item => AssignmentChanged(item, command, "Reserved"))
                .Append(AvailabilityChanged(booked, command, revision, "Reserved")).ToArray();
            return (new AllocationChange(held, [booked], events), next => new BatchWrite(next, shelter.ShelterId, null, held));
        });
    }

    internal async Task<BatchWrite> Reassign(ReassignShelterPlacesCommand command, CancellationToken cancellation)
    {
        RequireCommand(command.CommandId);
        var targets = RequireTargets(command.Assignments, command.ShelterId, command.ExpectedShelterVersion,
            command.HoldsExpireAt, command.ApprovalRef, command.GroupPlanRef);
        if (!SiteRules.IsId(command.FromShelterId) || command.ExpectedFromShelterVersion is not > 0 ||
            !SiteRules.IsId(command.Reason))
            throw Invalid("InvalidCommand", "Specify the original shelter, its expected version and a reason code.");
        if (command.FromShelterId == command.ShelterId)
            throw Invalid("SameShelter", "A transfer needs a different destination shelter.");
        if (store.TryReplay(command.CommandId!, command, out BatchWrite replay))
            return replay;

        var checks = await CheckRoutes(targets, command.ShelterId!, cancellation);
        var plan = command.GroupPlanRef is { } planRef ? await grouping.GetPlan(planRef, cancellation) : null;

        return store.Execute<BatchWrite>(command.CommandId!, command, state =>
        {
            var current = targets.Select(target =>
            {
                var assignment = RequireAssignment(state, target);
                if (assignment.Status == AssignmentStatus.Admitted)
                    throw new SiteCatalogError(422, "OccupancyCannotTransfer",
                        "Confirmed occupancy stays until departure. Open a new leg for an onward transfer.");
                if (!assignment.HoldsAt(command.FromShelterId!))
                    throw new SiteCatalogError(409, "AssignmentNotReserved",
                        "The assignment has no current hold at the original shelter.");
                return assignment;
            }).ToArray();

            var from = RequireShelter(state, command.FromShelterId!, command.ExpectedFromShelterVersion!.Value);
            var shelter = RequireShelter(state, command.ShelterId!, command.ExpectedShelterVersion!.Value);
            var (moved, booked) = PlaceHolds(state, shelter, current, targets, checks, plan, command.GroupPlanRef,
                command.HoldsExpireAt!.Value, command.ApprovalRef!, command.CommandId!,
                previous => new WithdrawnHold(previous.Version, previous.Reservation!.ReservationId,
                    previous.Reservation.ShelterId, command.Reason!, null));
            // The new holds and the release of the unused original holds commit together.
            var released = current.Aggregate(from, (site, item) => ShelterLedger.RemoveHold(site, item.Reservation!.ReservationId));
            var revision = state.State.Revision + 1;
            var events = moved.Select(item => AssignmentChanged(item, command, command.Reason!))
                .Append(AvailabilityChanged(released, command, revision, "TransferredOut"))
                .Append(AvailabilityChanged(booked, command, revision, "TransferredIn")).ToArray();
            return (new AllocationChange(moved, [released, booked], events),
                next => new BatchWrite(next, shelter.ShelterId, from.ShelterId, moved));
        });
    }

    internal async Task<AssignmentWrite> Invalidate(InvalidateShelterAssignmentCommand command, CancellationToken cancellation)
    {
        RequireCommand(command.CommandId);
        var reference = RequireRef(command.IncidentId, command.CaseId, command.LegId);
        if (command.ExpectedVersion is not > 0 || !SiteRules.IsId(command.Reason) ||
            (command.EvidenceRef is not null && !SiteRules.IsId(command.EvidenceRef)))
            throw Invalid("InvalidCommand", "Specify the expected assignment version, a reason code and an optional evidence reference.");
        if (store.TryReplay(command.CommandId!, command, out AssignmentWrite replay))
            return replay;

        var snapshot = RequireReserved(store.Read().Find(reference), command.ExpectedVersion.Value);
        var hold = snapshot.Reservation!;
        var finding = await CurrentIneligibility(reference, hold, command.At, cancellation)
            ?? throw new SiteCatalogError(422, "AssignmentStillEligible",
                "The destination and route are still eligible. Use ReleaseShelterReservation to cancel instead.");

        return store.Execute<AssignmentWrite>(command.CommandId!, command, state =>
        {
            var assignment = RequireReserved(state.Find(reference), command.ExpectedVersion.Value);
            if (assignment.Reservation != hold)
                throw new SiteCatalogError(409, "AssignmentVersionConflict", "Read the current assignment before deciding again.");
            var withdrawn = new WithdrawnHold(assignment.Version, hold.ReservationId, hold.ShelterId, command.Reason!,
                command.EvidenceRef);
            var next = assignment with
            {
                Version = assignment.Version + 1, Status = AssignmentStatus.AwaitingReplacement, Reservation = null,
                Withdrawn = withdrawn, Reason = finding,
            };
            var revision = state.State.Revision + 1;
            var events = new List<IntegrationEvent> { AssignmentChanged(next, command, command.Reason!) };
            var shelters = new List<Shelter>();
            if (state.Shelter(hold.ShelterId) is { } shelter)
            {
                shelters.Add(ShelterLedger.RemoveHold(shelter, hold.ReservationId));
                events.Add(AvailabilityChanged(shelters[0], command, revision, "Invalidated"));
            }
            return (new AllocationChange([next], shelters, events), version => new AssignmentWrite(version, next));
        });
    }

    internal Task<AssignmentWrite> Release(ReleaseShelterReservationCommand command, CancellationToken _)
    {
        RequireCommand(command.CommandId);
        var reference = RequireRef(command.IncidentId, command.CaseId, command.LegId);
        if (command.ExpectedVersion is not > 0 || command.Reason is not ("Cancelled" or "HoldExpired") ||
            command.StillRequired is null || !SiteRules.IsId(command.ApprovalRef) ||
            (command.Reason == "HoldExpired" && command.ObservedAt is null))
            throw Invalid("InvalidCommand",
                "Specify the expected version, Cancelled or HoldExpired with observedAt, stillRequired and an approval reference.");

        return Task.FromResult(store.Execute<AssignmentWrite>(command.CommandId!, command, state =>
        {
            var assignment = state.Find(reference) ?? throw NotFound();
            if (assignment.Version != command.ExpectedVersion)
                throw new SiteCatalogError(409, "AssignmentVersionConflict", "Read the current assignment before deciding again.");
            if (assignment.Status == AssignmentStatus.Admitted)
                throw new SiteCatalogError(422, "OccupancyRequiresDeparture",
                    "An admitted person's place is released only by a confirmed departure.");
            var cancelsNeed = assignment.Status == AssignmentStatus.AwaitingReplacement &&
                command.Reason == "Cancelled" && command.StillRequired == false;
            if (assignment.Status != AssignmentStatus.Reserved && !cancelsNeed)
                throw new SiteCatalogError(409, "NothingToRelease", "The assignment holds no reservation to release.");

            var hold = assignment.Reservation;
            if (command.Reason == "HoldExpired" && command.ObservedAt < hold!.HoldsExpireAt)
                throw new SiteCatalogError(422, "HoldNotExpired", "The reservation is still within its agreed validity.");

            var next = assignment with
            {
                Version = assignment.Version + 1,
                Status = command.StillRequired == true ? AssignmentStatus.AwaitingReplacement : AssignmentStatus.Closed,
                Reservation = null,
                Withdrawn = hold is null ? assignment.Withdrawn
                    : new WithdrawnHold(assignment.Version, hold.ReservationId, hold.ShelterId, command.Reason!, command.ApprovalRef),
                Reason = command.Reason,
            };
            var revision = state.State.Revision + 1;
            var events = new List<IntegrationEvent> { AssignmentChanged(next, command, command.Reason!) };
            var shelters = new List<Shelter>();
            if (hold is not null && state.Shelter(hold.ShelterId) is { } shelter)
            {
                shelters.Add(ShelterLedger.RemoveHold(shelter, hold.ReservationId));
                events.Add(AvailabilityChanged(shelters[0], command, revision, "Released"));
            }
            return (new AllocationChange([next], shelters, events), version => new AssignmentWrite(version, next));
        }));
    }

    /// <summary>
    /// Rechecks capacity, intake, opening, care and route evidence for the whole batch at one shelter and returns
    /// every updated assignment with the shelter carrying the new holds. Any failure throws, so nothing commits.
    /// </summary>
    private static (ShelterAssignment[] Assignments, Shelter Shelter) PlaceHolds(
        AllocationView state, Shelter shelter, ShelterAssignment[] current, AssignmentTarget[] targets,
        IReadOnlyDictionary<string, RouteCheck> checks, ApprovedPlan? plan, GroupPlanRef? planRef,
        DateTimeOffset holdsExpireAt, string approvalRef, string commandId, Func<ShelterAssignment, WithdrawnHold?> withdrawn)
    {
        for (var index = 0; index < current.Length; index++)
        {
            if (current[index].GroupPlanRef != planRef)
                throw new SiteCatalogError(409, "GroupPlanRefMismatch",
                    "Reserve with the same approved plan reference that opened the assignment.");
        }
        if (planRef is not null)
            RequirePlan(plan, planRef, current.Select(item => item.CaseId).ToArray());

        var checksByIndex = targets.Select(target => checks[RouteKey(target)]).ToArray();
        for (var index = 0; index < current.Length; index++)
        {
            var check = checksByIndex[index];
            var route = targets[index].Route!;
            if (!check.Eligible)
                throw new SiteCatalogError(422, "RouteIneligible",
                    $"Routing reported {check.Reason ?? "an ineligible route"} for {current[index].CaseId}.");
            if (check.SiteVersion != shelter.Version)
                throw new SiteCatalogError(409, "RouteEvidenceStale", "The route was checked against another shelter version.");
            if (route.Query!.CaseIds is { } routed &&
                (!routed.Split(',').Contains(current[index].CaseId) ||
                    check.Travel?.Cases?.Any(person => person.CaseId == current[index].CaseId) != true))
                throw new SiteCatalogError(422, "RouteSubjectMismatch", "The route evidence does not cover this case.");

            var arrival = check.ArrivalWindow!;
            var reason = SiteRules.IneligibleReason(shelter.Status, shelter.OperatingWindow, shelter.Evidence,
                arrival.Earliest, checked((int)check.SituationRevision));
            if (reason is null && shelter.OperatingWindow.ClosesAt < arrival.LatestExclusive)
                reason = "OutsideOperatingWindow";
            if (reason is not null)
                throw new SiteCatalogError(422, "ShelterIneligible", $"The shelter is not eligible at arrival: {reason}.");
            if (holdsExpireAt < arrival.LatestExclusive)
                throw new SiteCatalogError(422, "HoldExpiresBeforeArrival", "Hold the places at least until the expected arrival.");
        }

        RequireCareTogether(state, shelter.ShelterId, current, plan);

        // Commitments already include every hold and admission recorded in the shelter's reception ledger.
        if (shelter.IsOvercommitted)
            throw new SiteCatalogError(422, "Overcommitted", "The shelter has more commitments than usable places.");
        var free = shelter.CalculateAvailability();
        var demand = current.Aggregate(PlaceNeeds.Zero, (sum, item) => sum.Add(item.Needs));
        if (demand.Places > free.Places)
            throw new SiteCatalogError(422, "InsufficientCapacity",
                $"The batch needs {demand.Places} places and {free.Places} remain.");
        if (demand.AccessiblePlaces > free.AccessiblePlaces)
            throw new SiteCatalogError(422, "InsufficientAccessiblePlaces",
                $"The batch needs {demand.AccessiblePlaces} accessible places and {free.AccessiblePlaces} remain.");

        if (shelter.Resources.IntakePerHour is { } intake)
        {
            var batch = current.Select((item, index) =>
                (Hour: ShelterLedger.Hour(checksByIndex[index].ArrivalWindow!.Earliest), item.Needs.Places));
            foreach (var bucket in batch.GroupBy(entry => entry.Hour))
            {
                if (ShelterLedger.ArrivalsInHour(shelter, bucket.Key) + bucket.Sum(entry => entry.Places) > intake)
                    throw new SiteCatalogError(422, "IntakeLimitReached",
                        $"The shelter accepts {intake} arrivals in the hour starting {bucket.Key:O}.");
            }
        }

        var booked = shelter;
        var assignments = current.Select((item, index) =>
        {
            var check = checksByIndex[index];
            var arrival = check.ArrivalWindow!;
            var reservationId = ReservationId(commandId, item.CaseId);
            booked = ShelterLedger.AddHold(booked, new ReceptionHold(reservationId, item.IncidentId, item.CaseId, item.LegId,
                new PlaceNeed(item.Needs.Places, item.Needs.AccessiblePlaces),
                new SiteWindow(arrival.Earliest, arrival.LatestExclusive), holdsExpireAt));
            var hold = new AssignmentHold(reservationId, shelter.ShelterId, shelter.Version, item.Needs, arrival,
                holdsExpireAt, targets[index].Route!, check.SituationRevision, approvalRef, commandId);
            return item with
            {
                Version = item.Version + 1, Status = AssignmentStatus.Reserved, Reservation = hold,
                Withdrawn = withdrawn(item) ?? item.Withdrawn, Reason = null,
            };
        }).ToArray();
        return (assignments, booked);
    }

    /// <summary>A reservation ID the shelter operator can quote in an admission report.</summary>
    private static string ReservationId(string commandId, string caseId)
    {
        var id = $"{commandId}-{caseId}";
        return id.Length <= 120 ? id
            : "res-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..40];
    }

    /// <summary>Both members of a required-care link travel to the same shelter on the same leg.</summary>
    private static void RequireCareTogether(AllocationView state, string shelterId, ShelterAssignment[] batch, ApprovedPlan? plan)
    {
        if (plan?.Proposal?.RequiredCare is not { } links)
            return;
        var moving = batch.Select(item => item.CaseId).ToHashSet(StringComparer.Ordinal);
        var sample = batch[0];
        foreach (var link in links.Where(link => link.Kind == "required-care"))
        {
            if (moving.Contains(link.CaseId) == moving.Contains(link.RelatedCaseId))
                continue;
            var partner = moving.Contains(link.CaseId) ? link.RelatedCaseId : link.CaseId;
            var other = state.Find(new(sample.IncidentId, partner, sample.LegId));
            if (other?.HoldsAt(shelterId) != true && other?.Reason != $"AdmittedAt:{shelterId}")
                throw new SiteCatalogError(422, "RequiredCareSplit",
                    "Reserve a required-care pair together or at the shelter where the other person is already held.");
        }
    }

    private static void RequirePlan(ApprovedPlan? plan, GroupPlanRef reference, IEnumerable<string> caseIds)
    {
        if (plan is null)
            throw new SiteCatalogError(422, "GroupPlanNotFound", "The referenced group has no approved plan.");
        if (plan.PlanRevision != reference.PlanRevision)
            throw new SiteCatalogError(409, "GroupPlanChanged", "Use the current approved plan revision.");
        if (plan.Status != "Approved")
            throw new SiteCatalogError(422, "GroupPlanReviewRequired", "The approved plan is under review.");
        if (caseIds.Any(caseId => !plan.Includes(caseId)))
            throw new SiteCatalogError(422, "CaseNotInGroupPlan", "Every case must belong to the approved plan.");
    }

    private async Task<Dictionary<string, RouteCheck>> CheckRoutes(AssignmentTarget[] targets, string shelterId,
        CancellationToken cancellation)
    {
        var checks = new Dictionary<string, RouteCheck>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var key = RouteKey(target);
            if (!checks.ContainsKey(key))
                checks[key] = await routing.CheckShelterRoute(new(target.IncidentId!, target.CaseId!, target.LegId!), shelterId,
                    target.Route!.Query!, target.Route.EvidenceId, cancellation);
        }
        return checks;
    }

    /// <summary>Returns why the held destination is no longer usable, or null when it remains eligible.</summary>
    private async Task<string?> CurrentIneligibility(AssignmentRef reference, AssignmentHold hold, DateTimeOffset? at,
        CancellationToken cancellation)
    {
        var shelter = store.Read().Shelter(hold.ShelterId);
        if (shelter is null)
            return "ShelterNotFound";
        if (shelter.Status != "open")
            return SiteRules.IneligibleReason(shelter.Status, shelter.OperatingWindow, shelter.Evidence,
                hold.ArrivalWindow.Earliest, null);

        var query = at is null ? hold.Route.Query! : hold.Route.Query! with { At = at };
        var check = await routing.CheckShelterRoute(reference, hold.ShelterId, query, null, cancellation);
        if (!check.Eligible)
            return check.Reason ?? "RouteIneligible";
        if (check.AvailabilityReason == "Overcommitted")
            return "Overcommitted";
        return null;
    }

    private static Shelter RequireShelter(AllocationView state, string shelterId, int expectedVersion)
    {
        var shelter = state.Shelter(shelterId)
            ?? throw new SiteCatalogError(404, "ShelterNotFound", "The shelter is not in the catalog.");
        if (shelter.Version != expectedVersion)
            throw new SiteCatalogError(409, "ShelterVersionConflict", "Read the current shelter before deciding again.");
        return shelter;
    }

    private static ShelterAssignment RequireAssignment(AllocationView state, AssignmentTarget target)
    {
        var assignment = state.Find(new(target.IncidentId!, target.CaseId!, target.LegId!)) ?? throw NotFound();
        if (assignment.Version != target.ExpectedVersion)
            throw new SiteCatalogError(409, "AssignmentVersionConflict", "Read the current assignment before deciding again.");
        return assignment;
    }

    private static ShelterAssignment RequireReserved(ShelterAssignment? assignment, int expectedVersion)
    {
        if (assignment is null)
            throw NotFound();
        if (assignment.Version != expectedVersion)
            throw new SiteCatalogError(409, "AssignmentVersionConflict", "Read the current assignment before deciding again.");
        if (assignment.Status == AssignmentStatus.Admitted)
            throw new SiteCatalogError(422, "OccupancyRequiresDeparture",
                "An admitted person's place is released only by a confirmed departure.");
        if (assignment.Status != AssignmentStatus.Reserved)
            throw new SiteCatalogError(409, "AssignmentNotReserved", "The assignment holds no current reservation.");
        return assignment;
    }

    private static AssignmentTarget[] RequireTargets(AssignmentTarget[]? targets, string? shelterId, int? shelterVersion,
        DateTimeOffset? holdsExpireAt, string? approvalRef, GroupPlanRef? planRef)
    {
        if (!SiteRules.IsId(shelterId) || shelterVersion is not > 0 || holdsExpireAt is null || !SiteRules.IsId(approvalRef))
            throw Invalid("InvalidCommand", "Specify the shelter, its expected version, hold validity and an approval reference.");
        if (targets is not { Length: > 0 and <= MaxBatch })
            throw Invalid("InvalidBatch", $"Include 1 to {MaxBatch} assignments.");
        if (planRef is not null && !planRef.IsValid)
            throw Invalid("InvalidGroupPlanRef", "Reference an approved plan revision.");
        foreach (var target in targets)
        {
            if (target is null)
                throw Invalid("InvalidBatch", "Each assignment entry must be present.");
            RequireRef(target.IncidentId, target.CaseId, target.LegId);
            if (target.ExpectedVersion is not > 0)
                throw Invalid("InvalidBatch", "Each assignment needs its expected version.");
            if (target.Route is not { EvidenceId: { Length: 64 } evidenceId, Query: { } query } ||
                !evidenceId.All(char.IsAsciiHexDigitLower) || query.Mode is not ("walking" or "driving") ||
                query.At is null || (query.Node is null && query.CaseIds is null))
                throw Invalid("InvalidRouteEvidence",
                    "Each assignment needs the Routing evidence ID and the inputs of the destination-route query.");
        }
        if (targets.Select(target => target.IncidentId).Distinct().Count() != 1 ||
            (planRef is not null && planRef.IncidentId != targets[0].IncidentId) ||
            targets.Select(target => $"{target.CaseId}/{target.LegId}").Distinct().Count() != targets.Length)
            throw Invalid("InvalidBatch", "Use one incident and distinct case and leg pairs.");
        return targets;
    }

    private static AssignmentRef RequireRef(string? incidentId, string? caseId, string? legId)
    {
        if (!SiteRules.IsId(incidentId) || !SiteRules.IsId(caseId) || !SiteRules.IsId(legId))
            throw Invalid("InvalidAssignmentRef", "Specify incidentId, caseId and legId.");
        return new(incidentId!, caseId!, legId!);
    }

    private static void RequireCommand(string? commandId)
    {
        if (!SiteRules.IsId(commandId))
            throw Invalid("InvalidCommand", "Specify a commandId.");
    }

    private static string RouteKey(AssignmentTarget target) =>
        $"{target.IncidentId}/{target.LegId}/{target.Route!.EvidenceId}/{JsonSerializer.Serialize(target.Route.Query, Json)}";

    private IntegrationEvent AssignmentChanged(ShelterAssignment assignment, object command, string reason)
    {
        var (correlation, causation) = Trace(command);
        return IntegrationEvent.Create(EventTypes.ShelterAssignmentChanged, Producer, assignment.Ref.Key,
            assignment.Version, clock.GetUtcNow(), new ShelterAssignmentChangedPayload(
                assignment.IncidentId, assignment.CaseId, assignment.LegId, assignment.Version, assignment.Status,
                assignment.Reservation?.ShelterId, assignment.Reservation?.ReservationId, assignment.Reservation?.ArrivalWindow,
                assignment.Withdrawn?.ShelterId, assignment.Withdrawn?.ReservationId, assignment.Withdrawn?.AssignmentVersion,
                reason), correlation, causation);
    }

    /// <summary>Reports the shelter's held places after the change, from its updated commitments.</summary>
    private IntegrationEvent AvailabilityChanged(Shelter shelter, object command, int revision, string reason)
    {
        var remaining = new PlaceNeeds(shelter.Commitments.HeldPlaces, shelter.Commitments.HeldAccessiblePlaces);
        var (correlation, causation) = Trace(command);
        return IntegrationEvent.Create(EventTypes.ShelterAvailabilityChanged, Producer, shelter.ShelterId,
            revision, clock.GetUtcNow(), new ShelterAvailabilityChangedPayload(shelter.ShelterId, shelter.Version, revision,
                remaining, reason), correlation, causation);
    }

    private static (string? Correlation, string? Causation) Trace(object command) => command switch
    {
        OpenShelterAssignmentCommand item => (item.CorrelationId, item.CommandId),
        ReserveShelterPlacesCommand item => (item.CorrelationId, item.CommandId),
        ReassignShelterPlacesCommand item => (item.CorrelationId, item.CommandId),
        InvalidateShelterAssignmentCommand item => (item.CorrelationId, item.CommandId),
        ReleaseShelterReservationCommand item => (item.CorrelationId, item.CommandId),
        _ => (null, null),
    };

    private static SiteCatalogError NotFound() =>
        new(404, "AssignmentNotFound", "Open a shelter assignment for this case and leg first.");

    private static SiteCatalogError Invalid(string code, string message) => new(400, code, message);
}
