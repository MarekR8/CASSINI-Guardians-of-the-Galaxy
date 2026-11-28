using System.Text.Json.Serialization;
using Cassini.Demo.SiteCatalog;
using Cassini.Sheltering.Allocation;
using Cassini.Sheltering.Infrastructure;

namespace Cassini.Sheltering.Publication;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReconcileShelterInstructionCommand(
    string? CommandId, string? CorrelationId, string? IncidentId, string? CaseId, string? LegId, int? ExpectedAssignmentVersion);

/// <summary>SH-W5 instruction state: the Sheltering assignment next to the Routing instruction for the same leg.</summary>
internal sealed record ShelterInstruction(string State, string? Reason, ShelterAssignment Assignment, RouteInstructionView? Instruction);

internal static class InstructionStates
{
    internal const string NoInstruction = "NoInstruction";
    internal const string PendingPublication = "PendingPublication";
    internal const string Current = "Current";
    internal const string WithdrawalPending = "WithdrawalPending";
    internal const string Withdrawn = "Withdrawn";
    internal const string PublicationUnknown = "PublicationUnknown";

    /// <summary>
    /// A capacity hold is not an issued route: a reservation is <c>Current</c> only while Routing holds a committed
    /// instruction for exactly that hold. Guidance left current for anything else must be withdrawn.
    /// </summary>
    internal static string Classify(string? status, string? reservationId, string? routedReservationId, bool withdrawn) =>
        status == AssignmentStatus.Reserved
            ? routedReservationId is not null && routedReservationId == reservationId ? Current : PendingPublication
            : routedReservationId is not null ? WithdrawalPending : withdrawn ? Withdrawn : NoInstruction;
}

/// <summary>
/// The coordinator's publication step after a committed booking (SH-T06). Each run reads both owners and then
/// publishes the current hold through Routing, withdraws guidance the assignment no longer supports, or changes
/// nothing. The command ID goes to Routing unchanged, so a retry after an unknown outcome finds Routing's receipt
/// or the instruction it already committed instead of creating a second route version.
/// </summary>
internal sealed class InstructionWorkflow(AllocationStore store, RoutingClient routing)
{
    internal async Task<ShelterInstruction> Get(string? incidentId, string? caseId, string? legId, CancellationToken cancellation)
    {
        var assignment = Find(Ref(incidentId, caseId, legId));
        try
        {
            return View(assignment, await routing.GetInstruction(assignment.Ref.Key, cancellation));
        }
        catch (SiteCatalogError error) when (error.Status == 503)
        {
            // Without Routing the outcome is unknown; it is never shown as an issued route.
            return new(InstructionStates.PublicationUnknown, error.Code, assignment, null);
        }
    }

    internal async Task<ShelterInstruction> Reconcile(ReconcileShelterInstructionCommand command, CancellationToken cancellation)
    {
        if (!SiteRules.IsId(command.CommandId) || command.ExpectedAssignmentVersion is not > 0 ||
            (command.CorrelationId is not null && !SiteRules.IsId(command.CorrelationId)))
            throw new SiteCatalogError(400, "InvalidCommand", "Specify a commandId and the expected assignment version.");
        var reference = Ref(command.IncidentId, command.CaseId, command.LegId);
        var assignment = Find(reference);
        if (assignment.Version != command.ExpectedAssignmentVersion)
            throw new SiteCatalogError(409, "AssignmentVersionConflict", "Read the current assignment before deciding again.");

        var instruction = await routing.GetInstruction(reference.Key, cancellation);
        var subject = new { kind = "shelter-assignment", id = reference.Key };
        switch (View(assignment, instruction).State)
        {
            case InstructionStates.PendingPublication:
                var hold = assignment.Reservation!;
                // Propose again: the hold's route may have changed since booking; only an eligible arrival is published.
                var proposal = await routing.CheckShelterRoute(reference, hold.ShelterId, hold.Route.Query!, null, cancellation);
                if (!proposal.Eligible)
                    throw new SiteCatalogError(422, "RouteIneligible",
                        $"Routing reported {proposal.Reason ?? "an ineligible route"}; the reservation stays pending publication.");
                if (proposal.ArrivalWindow!.LatestExclusive > hold.HoldsExpireAt)
                    throw new SiteCatalogError(422, "HoldExpiresBeforeArrival", "The hold ends before the expected arrival.");
                instruction = await routing.Publish(new
                {
                    commandId = command.CommandId, correlationId = command.CorrelationId, subject,
                    expectedVersion = instruction.AggregateVersion,
                    assignment = new
                    {
                        reference.IncidentId, reference.CaseId, reference.LegId, assignmentVersion = assignment.Version,
                        hold.ReservationId, hold.ShelterId,
                    },
                    route = new { proposal.Evidence!.EvidenceId, query = hold.Route.Query },
                }, cancellation);
                break;
            case InstructionStates.WithdrawalPending:
                instruction = await routing.Withdraw(new
                {
                    commandId = command.CommandId, correlationId = command.CorrelationId, subject,
                    expectedVersion = instruction.AggregateVersion, routeVersion = instruction.Current!.RouteVersion,
                    reasonCode = assignment.Status == AssignmentStatus.Admitted ? "ArrivedAtShelter"
                        : assignment.Withdrawn?.Reason ?? "AssignmentChanged",
                    evidenceRef = assignment.Withdrawn?.EvidenceRef,
                }, cancellation);
                break;
            default:
                return View(assignment, instruction);
        }

        // The assignment may have moved on while Routing committed; the caller then reconciles again.
        var latest = Find(reference);
        if (latest.Version != assignment.Version)
            throw new SiteCatalogError(409, "AssignmentChanged", "The assignment changed during publication. Reconcile again.");
        return View(latest, instruction);
    }

    private static ShelterInstruction View(ShelterAssignment assignment, RouteInstructionView instruction)
    {
        var state = InstructionStates.Classify(assignment.Status, assignment.Reservation?.ReservationId,
            instruction.Current?.Assignment.ReservationId, instruction.LastWithdrawal is not null);
        var reason = state switch
        {
            InstructionStates.Withdrawn => instruction.LastWithdrawal!.ReasonCode,
            InstructionStates.WithdrawalPending or InstructionStates.NoInstruction => assignment.Reason,
            _ => null,
        };
        return new(state, reason, assignment, instruction);
    }

    private ShelterAssignment Find(AssignmentRef reference) => store.Read().Find(reference)
        ?? throw new SiteCatalogError(404, "AssignmentNotFound", "No shelter assignment exists for this case and leg.");

    private static AssignmentRef Ref(string? incidentId, string? caseId, string? legId) =>
        SiteRules.IsId(incidentId) && SiteRules.IsId(caseId) && SiteRules.IsId(legId) ? new(incidentId!, caseId!, legId!)
            : throw new SiteCatalogError(400, "InvalidAssignmentRef", "Specify incidentId, caseId and legId.");
}
