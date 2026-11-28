using System.Text.Json;

namespace Cassini.Routing.Destinations;

internal sealed record RecordedDestinationRoute(
    string RouteId, string Source, string Mode, DateTimeOffset ObservedAt, DateTimeOffset ValidUntil,
    string TravelMode, string[] Nodes, string[] Segments);
internal sealed record DestinationBooking(
    string ReservationId, string IncidentId, string LegId, string ShelterId, string[] CaseIds,
    string Status, long SituationRevision, int ShelterVersion, int AccessiblePlacesHeld,
    DateTimeOffset ValidFrom, DateTimeOffset ValidUntil);
internal sealed record TransportWindow(
    string TransportId, string IncidentId, string Origin, string[] CaseIds, long SituationRevision, bool StepFree,
    DateTimeOffset DepartsFrom, DateTimeOffset DepartsBefore, DateTimeOffset ArrivesBefore);

internal sealed record DestinationEvidenceFixtures(
    RecordedDestinationRoute[] Routes, DestinationBooking[] Bookings, TransportWindow[] TransportWindows)
{
    internal static DestinationEvidenceFixtures Load(string? path) => string.IsNullOrWhiteSpace(path)
        ? new([], [], [])
        : JsonSerializer.Deserialize<DestinationEvidenceFixtures>(File.ReadAllBytes(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Missing sample destination route evidence.");

    internal static bool SameCases(string[] caseIds, TravelContext travel) =>
        caseIds.Order(StringComparer.Ordinal).SequenceEqual(travel.Cases.Select(person => person.CaseId));
}
