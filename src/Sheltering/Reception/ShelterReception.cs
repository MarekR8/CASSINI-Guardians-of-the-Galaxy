using Cassini.Demo.SiteCatalog;

namespace Cassini.Sheltering.Reception;

/// <summary>Places one person needs: a place, of which some may need to be accessible.</summary>
internal sealed record PlaceNeed(int Places, int AccessiblePlaces)
{
    internal bool IsValid => Places >= 1 && AccessiblePlaces >= 0 && AccessiblePlaces <= Places;
}

/// <summary>
/// An active hold for one case and leg, as issued by a reservation (SH-W4). Admission removes it and
/// converts its places to occupancy; nothing else in this module creates holds.
/// </summary>
internal sealed record ShelterHold(
    string ReservationId, string IncidentId, string CaseId, string LegId, PlaceNeed Resources,
    SiteWindow? ArrivalWindow, DateTimeOffset? ExpiresAt);

internal sealed record ShelterDeparture(string ReportId, DateTimeOffset ObservedAt, DateTimeOffset RecordedAt);

/// <summary>One confirmed admission. Its places stay occupied until a departure is confirmed.</summary>
internal sealed record ShelterAdmission(
    string AdmissionId, string IncidentId, string CaseId, string LegId, string? ReservationId, bool WalkIn,
    PlaceNeed Resources, string ReportId, DateTimeOffset ObservedAt, DateTimeOffset RecordedAt,
    ShelterDeparture? Departure = null)
{
    internal bool Present => Departure is null;
}

/// <summary>A processed operator report, kept so a retried report counts once whatever its commandId.</summary>
internal sealed record ReceptionReport(string ReportId, string Kind, string Fingerprint, ReceptionResult Result);

/// <summary>An integration event written in the same state change as the fact it reports (outbox).</summary>
internal sealed record ReceptionEvent(
    Guid EventId, string Type, string Producer, string SubjectId, int SubjectVersion, DateTimeOffset OccurredAt,
    string CorrelationId, string CausationId, ReceptionEventPayload Payload);

internal sealed record ReceptionEventPayload(
    string ShelterId, string IncidentId, string AdmissionId, string CaseId, string LegId, string? ReservationId,
    bool WalkIn, int Places, int AccessiblePlaces, string ReportId, DateTimeOffset ObservedAt);

internal sealed record ShelterReception(
    ShelterHold[] Holds, ShelterAdmission[] Admissions, ReceptionReport[] Reports, ReceptionEvent[] Outbox)
{
    internal static ShelterReception Empty { get; } = new([], [], [], []);
}

/// <summary>What one admission or departure report changed.</summary>
internal sealed record ReceptionResult(
    string ReportId, string Kind, string ShelterId, int Revision, int ShelterVersion,
    ReceptionEntry[] Entries, ReleasedHold[] ReleasedHolds);

/// <summary>
/// Outcome per person: Admitted (hold converted), WalkInAdmitted, AlreadyAdmitted (no change),
/// Departed or AlreadyDeparted (no change).
/// </summary>
internal sealed record ReceptionEntry(
    string CaseId, string LegId, string AdmissionId, string Outcome, string? ReservationId, PlaceNeed Resources);

/// <summary>A hold at another shelter released because the person was admitted elsewhere as a walk-in.</summary>
internal sealed record ReleasedHold(string ShelterId, string ReservationId, string CaseId, string LegId, PlaceNeed Resources);
