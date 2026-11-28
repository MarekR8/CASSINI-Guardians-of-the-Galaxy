using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cassini.Demo.SiteCatalog;
using Cassini.Messaging;
using Cassini.Sheltering.Catalog;
using Cassini.Sheltering.Infrastructure;

namespace Cassini.Sheltering.Reception;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfirmShelterAdmissionCommand(
    string? CommandId, int? ExpectedVersion, string? ShelterId, string? IncidentId, string? ReportId,
    DateTimeOffset? ObservedAt, AdmissionArrival[]? Arrivals);

/// <summary>A person reported at the shelter. Resources are required only for a walk-in without a hold here.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AdmissionArrival(string? CaseId, string? LegId, string? ReservationId, PlaceNeed? Resources);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ConfirmShelterDepartureCommand(
    string? CommandId, int? ExpectedVersion, string? ShelterId, string? IncidentId, string? ReportId,
    DateTimeOffset? ObservedAt, DepartureLeaver[]? Departures);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DepartureLeaver(string? CaseId, string? LegId);

/// <summary>
/// Shelter operator reports. Only an admission report converts a hold to occupancy and only a departure report frees
/// occupied places; route receipts, positions and pickups never reach these handlers.
/// </summary>
internal sealed class ReceptionCommands(CatalogStore<Shelter> catalog, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal ReceptionResult ConfirmAdmission(ConfirmShelterAdmissionCommand command, OperatorIdentity? caller = null)
    {
        var arrivals = command.Arrivals ?? [];
        Require(command.CommandId, command.ExpectedVersion, command.ShelterId, command.IncidentId, command.ReportId,
            command.ObservedAt, arrivals.Length);
        OperatorDirectory.RequireReporter(caller, command.IncidentId, command.ShelterId);
        if (arrivals.Any(arrival => !SiteRules.IsId(arrival.CaseId) || !SiteRules.IsId(arrival.LegId) ||
                                    (arrival.ReservationId is not null && !SiteRules.IsId(arrival.ReservationId)) ||
                                    arrival.Resources is { IsValid: false }))
            throw Invalid("Each arrival needs a caseId, a legId and, when given, valid resources and a reservationId.");
        if (arrivals.DistinctBy(arrival => (arrival.CaseId, arrival.LegId)).Count() != arrivals.Length)
            throw Invalid("Report each case and leg once.");

        var fingerprint = Fingerprint(new { command.ShelterId, command.IncidentId, command.ObservedAt, arrivals });
        return catalog.Change<object, ReceptionResult>(command.CommandId, command, (shelters, revision) =>
        {
            if (Replay(shelters, command.ReportId!, "admission", fingerprint) is { } previous)
                return (Array.Empty<Shelter>(), previous);

            var shelter = Current(shelters, command.ShelterId!, command.ExpectedVersion!.Value);
            var observedAt = command.ObservedAt!.Value.ToUniversalTime();
            var now = clock.GetUtcNow();
            var ledger = shelter.Ledger;
            var holds = ledger.Holds.ToList();
            var admissions = ledger.Admissions.ToList();
            var commitments = shelter.Commitments;
            var others = new Dictionary<string, Shelter>(StringComparer.Ordinal);
            var entries = new List<ReceptionEntry>();
            var released = new List<ReleasedHold>();
            var events = new List<ReceptionEvent>();
            var version = shelter.Version + 1;

            for (var index = 0; index < arrivals.Length; index++)
            {
                var arrival = arrivals[index];
                var (caseId, legId) = (arrival.CaseId!, arrival.LegId!);
                if (admissions.FirstOrDefault(item => item.Present && item.CaseId == caseId && item.LegId == legId) is { } present)
                {
                    entries.Add(new(caseId, legId, present.AdmissionId, "AlreadyAdmitted", present.ReservationId, present.Resources));
                    continue;
                }

                if (shelters.Values.FirstOrDefault(other => other.ShelterId != shelter.ShelterId && other.Ledger.Admissions
                        .Any(item => item.Present && item.CaseId == caseId && item.LegId == legId)) is { } elsewhere)
                    throw new SiteCatalogError(409, "AdmittedElsewhere",
                        $"Case {caseId} is admitted at {elsewhere.ShelterId} for this leg; confirm that departure first.");
                if (admissions.FirstOrDefault(item => item.Present && item.CaseId == caseId) is { } otherLeg)
                    throw new SiteCatalogError(409, "AlreadyPresent",
                        $"Case {caseId} is already present here on leg {otherLeg.LegId}.");

                // A person still present at another shelter on an earlier leg arrives on a transfer leg. Link the two
                // stays; the original places stay occupied until that shelter confirms the departure.
                var origin = shelters.Values.Where(other => other.ShelterId != shelter.ShelterId)
                    .SelectMany(other => other.Ledger.Admissions
                        .Where(item => item.Present && item.CaseId == caseId && item.IncidentId == command.IncidentId)
                        .Select(item => new StayRef(other.ShelterId, item.AdmissionId, item.LegId)))
                    .FirstOrDefault();

                var admissionId = $"{command.ReportId}-{index + 1}";
                ShelterAdmission admission;
                var hold = holds.FirstOrDefault(item => item.CaseId == caseId && item.LegId == legId);
                if (hold is not null)
                {
                    if (arrival.ReservationId is { } reservation && reservation != hold.ReservationId)
                        throw new SiteCatalogError(409, "ReservationMismatch",
                            $"Case {caseId} holds {hold.ReservationId} here, not {reservation}.");
                    if (arrival.Resources is { } need && need != hold.Resources)
                        throw new SiteCatalogError(409, "ResourceMismatch",
                            $"Case {caseId} holds {hold.Resources.Places} place(s); reserve the change before admitting.");
                    holds.Remove(hold);
                    commitments = commitments with
                    {
                        HeldPlaces = commitments.HeldPlaces - hold.Resources.Places,
                        HeldAccessiblePlaces = commitments.HeldAccessiblePlaces - hold.Resources.AccessiblePlaces,
                        OccupiedPlaces = commitments.OccupiedPlaces + hold.Resources.Places,
                        OccupiedAccessiblePlaces = commitments.OccupiedAccessiblePlaces + hold.Resources.AccessiblePlaces,
                    };
                    admission = new(admissionId, command.IncidentId!, caseId, legId, hold.ReservationId, false, hold.Resources,
                        command.ReportId!, observedAt, now, ReportedBy: caller?.OperatorId, TransferFrom: origin);
                    entries.Add(new(caseId, legId, admissionId, "Admitted", hold.ReservationId, hold.Resources, origin));
                }
                else
                {
                    if (arrival.Resources is not { } need)
                        throw new SiteCatalogError(422, "WalkInResourcesRequired",
                            $"Case {caseId} has no hold here; state the places a walk-in needs.");
                    if (arrival.ReservationId is not null)
                        throw new SiteCatalogError(409, "ReservationMismatch",
                            $"Case {caseId} has no hold at {shelter.ShelterId} under {arrival.ReservationId}.");
                    RequireAdmitting(shelter, observedAt);

                    // Reconcile first: an unused hold elsewhere for the same leg is released in this transaction.
                    foreach (var other in shelters.Values.Where(other => other.ShelterId != shelter.ShelterId))
                    {
                        var current = others.GetValueOrDefault(other.ShelterId) ?? other;
                        if (current.Ledger.Holds.FirstOrDefault(item => item.CaseId == caseId && item.LegId == legId) is not { } stale)
                            continue;
                        others[other.ShelterId] = current with
                        {
                            Version = other.Version + 1,
                            Commitments = current.Commitments with
                            {
                                HeldPlaces = current.Commitments.HeldPlaces - stale.Resources.Places,
                                HeldAccessiblePlaces = current.Commitments.HeldAccessiblePlaces - stale.Resources.AccessiblePlaces,
                            },
                            Reception = current.Ledger with { Holds = current.Ledger.Holds.Where(item => item != stale).ToArray() },
                        };
                        released.Add(new(other.ShelterId, stale.ReservationId, caseId, legId, stale.Resources));
                    }

                    commitments = commitments with
                    {
                        OccupiedPlaces = commitments.OccupiedPlaces + need.Places,
                        OccupiedAccessiblePlaces = commitments.OccupiedAccessiblePlaces + need.AccessiblePlaces,
                    };
                    if (commitments.TotalPlaces > shelter.Resources.Places ||
                        commitments.TotalAccessiblePlaces > shelter.Resources.AccessiblePlaces)
                        throw new SiteCatalogError(409, "NoAvailablePlaces",
                            $"No {(need.AccessiblePlaces > 0 ? "accessible " : "")}place is free for walk-in {caseId}; record the unmet need with the coordinator.");
                    admission = new(admissionId, command.IncidentId!, caseId, legId, null, true, need, command.ReportId!,
                        observedAt, now, ReportedBy: caller?.OperatorId, TransferFrom: origin);
                    entries.Add(new(caseId, legId, admissionId, "WalkInAdmitted", null, need, origin));
                }

                admissions.Add(admission);
                events.Add(Event(EventTypes.ShelterAdmissionConfirmed, shelter.ShelterId, version, now, admission, command.ReportId!, observedAt));
            }

            var result = new ReceptionResult(command.ReportId!, "admission", shelter.ShelterId, revision, version,
                entries.ToArray(), released.ToArray());
            var updated = shelter with
            {
                Version = version,
                Commitments = commitments,
                Reception = ledger with
                {
                    Holds = holds.ToArray(),
                    Admissions = admissions.ToArray(),
                    Reports = [.. ledger.Reports, new ReceptionReport(command.ReportId!, "admission", fingerprint, result)],
                    Outbox = [.. ledger.Outbox, .. events],
                },
            };
            return ([updated, .. others.Values], result);
        });
    }

    internal ReceptionResult ConfirmDeparture(ConfirmShelterDepartureCommand command, OperatorIdentity? caller = null)
    {
        var leavers = command.Departures ?? [];
        Require(command.CommandId, command.ExpectedVersion, command.ShelterId, command.IncidentId, command.ReportId,
            command.ObservedAt, leavers.Length);
        OperatorDirectory.RequireReporter(caller, command.IncidentId, command.ShelterId);
        if (leavers.Any(leaver => !SiteRules.IsId(leaver.CaseId) || !SiteRules.IsId(leaver.LegId)))
            throw Invalid("Each departure needs a caseId and a legId.");
        if (leavers.DistinctBy(leaver => (leaver.CaseId, leaver.LegId)).Count() != leavers.Length)
            throw Invalid("Report each case and leg once.");

        var fingerprint = Fingerprint(new { command.ShelterId, command.IncidentId, command.ObservedAt, leavers });
        return catalog.Change<object, ReceptionResult>(command.CommandId, command, (shelters, revision) =>
        {
            if (Replay(shelters, command.ReportId!, "departure", fingerprint) is { } previous)
                return (Array.Empty<Shelter>(), previous);

            var shelter = Current(shelters, command.ShelterId!, command.ExpectedVersion!.Value);
            var observedAt = command.ObservedAt!.Value.ToUniversalTime();
            var now = clock.GetUtcNow();
            var ledger = shelter.Ledger;
            var admissions = ledger.Admissions.ToList();
            var commitments = shelter.Commitments;
            var entries = new List<ReceptionEntry>();
            var events = new List<ReceptionEvent>();
            var version = shelter.Version + 1;

            foreach (var leaver in leavers)
            {
                var (caseId, legId) = (leaver.CaseId!, leaver.LegId!);
                var row = admissions.FindLastIndex(item => item.CaseId == caseId && item.LegId == legId);
                if (row < 0)
                    throw new SiteCatalogError(404, "NotAdmitted",
                        $"Case {caseId} has no confirmed admission at {shelter.ShelterId} for this leg.");
                var admission = admissions[row];
                if (!admission.Present)
                {
                    entries.Add(new(caseId, legId, admission.AdmissionId, "AlreadyDeparted", admission.ReservationId, admission.Resources));
                    continue;
                }
                if (observedAt < admission.ObservedAt)
                    throw new SiteCatalogError(422, "DepartureBeforeAdmission",
                        $"Case {caseId} was admitted at {admission.ObservedAt:O}, after the reported departure.");

                commitments = commitments with
                {
                    OccupiedPlaces = commitments.OccupiedPlaces - admission.Resources.Places,
                    OccupiedAccessiblePlaces = commitments.OccupiedAccessiblePlaces - admission.Resources.AccessiblePlaces,
                };
                admissions[row] = admission with { Departure = new ShelterDeparture(command.ReportId!, observedAt, now, caller?.OperatorId) };
                entries.Add(new(caseId, legId, admission.AdmissionId, "Departed", admission.ReservationId, admission.Resources));
                events.Add(Event(EventTypes.ShelterDepartureConfirmed, shelter.ShelterId, version, now, admission, command.ReportId!, observedAt));
            }

            if (!commitments.IsValid)
                throw new InvalidOperationException($"Occupancy of {shelter.ShelterId} would become negative.");
            var result = new ReceptionResult(command.ReportId!, "departure", shelter.ShelterId, revision, version,
                entries.ToArray(), []);
            var updated = shelter with
            {
                Version = version,
                Commitments = commitments,
                Reception = ledger with
                {
                    Admissions = admissions.ToArray(),
                    Reports = [.. ledger.Reports, new ReceptionReport(command.ReportId!, "departure", fingerprint, result)],
                    Outbox = [.. ledger.Outbox, .. events],
                },
            };
            return ([updated], result);
        });
    }

    private static void Require(string? commandId, int? expectedVersion, string? shelterId, string? incidentId,
        string? reportId, DateTimeOffset? observedAt, int people)
    {
        if (!SiteRules.IsId(commandId) || expectedVersion is null or < 1 || !SiteRules.IsId(shelterId) ||
            !SiteRules.IsId(incidentId) || !SiteRules.IsId(reportId) || observedAt is null)
            throw Invalid("Specify commandId, expectedVersion, shelterId, incidentId, reportId and observedAt.");
        if (people is < 1 or > 100)
            throw Invalid("Report between 1 and 100 people.");
    }

    /// <summary>A report already processed returns its result; the same reportId with other content is a conflict.</summary>
    private static ReceptionResult? Replay(IReadOnlyDictionary<string, Shelter> shelters, string reportId, string kind,
        string fingerprint)
    {
        var report = shelters.Values.SelectMany(shelter => shelter.Ledger.Reports).FirstOrDefault(item => item.ReportId == reportId);
        if (report is null)
            return null;
        if (report.Kind != kind || report.Fingerprint != fingerprint)
            throw new SiteCatalogError(409, "ReportConflict", "This reportId was already used for a different report.");
        return report.Result;
    }

    private static Shelter Current(IReadOnlyDictionary<string, Shelter> shelters, string shelterId, int expectedVersion)
    {
        if (!shelters.TryGetValue(shelterId, out var shelter))
            throw new SiteCatalogError(404, "UnknownShelter", $"Shelter {shelterId} is not in the catalog.");
        if (shelter.Version != expectedVersion)
            throw new SiteCatalogError(409, "VersionConflict", "Read the current shelter before reporting.");
        return shelter;
    }

    private static void RequireAdmitting(Shelter shelter, DateTimeOffset at)
    {
        if (shelter.Status != "open")
            throw new SiteCatalogError(422, "ShelterNotAdmitting", $"Shelter {shelter.ShelterId} is {shelter.Status}.");
        if (shelter.OperatingWindow.OpensAt is not { } opens || at < opens || at >= shelter.OperatingWindow.ClosesAt)
            throw new SiteCatalogError(422, "OutsideOperatingWindow", $"Shelter {shelter.ShelterId} is not admitting at {at:O}.");
    }

    private static ReceptionEvent Event(string type, string shelterId, int version, DateTimeOffset now,
        ShelterAdmission admission, string reportId, DateTimeOffset observedAt) => new(
        Guid.NewGuid(), type, "sheltering", shelterId, version, now, admission.IncidentId, reportId,
        new(shelterId, admission.IncidentId, admission.AdmissionId, admission.CaseId, admission.LegId,
            admission.ReservationId, admission.WalkIn, admission.Resources.Places, admission.Resources.AccessiblePlaces,
            reportId, observedAt, admission.TransferFrom?.ShelterId));

    private static string Fingerprint(object content) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, Json)));

    private static SiteCatalogError Invalid(string message) => new(400, "InvalidReport", message);
}
