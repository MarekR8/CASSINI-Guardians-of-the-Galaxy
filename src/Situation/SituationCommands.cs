using System.Security.Cryptography;
using System.Text.Json;

namespace Situation;

internal sealed record ClosureCommand(string? CommandId, string? SegmentId, bool? Closed, int? ExpectedRevision);
internal sealed record ImportCopernicusCommand(
    string? CommandId, int? ExpectedRevision, string? ProductId, DateTimeOffset? ObservedAt, JsonElement GeoJson);

internal sealed class SituationCommands(SituationStore store)
{
    private static readonly HashSet<string> KnownSegments = new(StringComparer.Ordinal)
    {
        "walk-to-pickup", "east-link", "bridge-2", "north-link", "north-exit-link", "outside-connector"
    };

    internal SituationSnapshot SetClosure(ClosureCommand command)
    {
        var (id, revision) = CheckVersion(command.CommandId, command.ExpectedRevision);
        if (command.SegmentId is not { } segment || !KnownSegments.Contains(segment) || command.Closed is not { } closed)
            throw Invalid("Specify a known segmentId and whether it is closed.");

        return store.Apply(id, revision, Fingerprint("closure", command), snapshot =>
        {
            var closures = new SortedSet<string>(snapshot.ClosedSegments, StringComparer.Ordinal);
            var changed = closed ? closures.Add(segment) : closures.Remove(segment);
            if (!changed)
                return snapshot;

            var stage = closures.Contains("bridge-2") ? (closures.Contains("north-link") ? 2 : 1) : 0;
            var revision = snapshot.Revision + 1;
            var segmentRevisions = new Dictionary<string, int>(snapshot.SegmentRevisions) { [segment] = revision };
            return snapshot with
            {
                Revision = revision, Stage = stage, ClosedSegments = closures.ToArray(), SegmentRevisions = segmentRevisions
            };
        });
    }

    internal SituationSnapshot ImportCopernicus(ImportCopernicusCommand command)
    {
        var (id, revision) = CheckVersion(command.CommandId, command.ExpectedRevision);
        if (command.ProductId?.Trim() is not { Length: > 0 and <= 120 } product ||
            command.ObservedAt is not { } observedAt)
            throw Invalid("A Copernicus EMS product ID and observation timestamp are required.");

        var geometry = ReadPolygons(command.GeoJson);
        var layer = new HazardLayer("copernicus:" + product, "copernicus-ems-operator-import",
            product, observedAt.ToUniversalTime(), geometry);
        return store.Apply(id, revision, Fingerprint("copernicus", command), snapshot =>
            snapshot with
            {
                Revision = snapshot.Revision + 1,
                Hazards = [.. snapshot.Hazards.Where(item => item.Id != layer.Id), layer]
            });
    }

    private static (string, int) CheckVersion(string? commandId, int? revision)
    {
        if (string.IsNullOrWhiteSpace(commandId) || commandId.Length > 120 || revision is null or < 0)
            throw Invalid("A commandId and a non-negative expectedRevision are required.");

        return (commandId, revision.Value);
    }

    private static string Fingerprint<T>(string kind, T command) =>
        $"{kind}:{Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command)))}";

    private static JsonElement ReadPolygons(JsonElement collection)
    {
        if (ReadType(collection) != "FeatureCollection" ||
            !collection.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            throw Invalid("geoJson must be a GeoJSON FeatureCollection.");

        var geometries = new List<JsonElement>();
        var points = 0;
        foreach (var feature in features.EnumerateArray())
        {
            if (ReadType(feature) != "Feature" || !feature.TryGetProperty("geometry", out var geometry))
                throw Invalid("geoJson contains a malformed Feature.");

            var type = ReadType(geometry);
            if (type is not ("Polygon" or "MultiPolygon"))
                continue;

            if (!geometry.TryGetProperty("coordinates", out var coordinates) ||
                coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() == 0)
                throw Invalid("A polygon must have at least one boundary.");

            if (type == "Polygon")
                CheckPolygon(coordinates, ref points);
            else
                foreach (var polygon in coordinates.EnumerateArray())
                    CheckPolygon(polygon, ref points);

            geometries.Add(JsonSerializer.SerializeToElement(new { type, coordinates }));
        }

        if (geometries.Count == 0)
            throw Invalid("geoJson must have a Polygon or MultiPolygon feature.");

        return JsonSerializer.SerializeToElement(new { type = "GeometryCollection", geometries });
    }

    private static void CheckPolygon(JsonElement polygon, ref int points)
    {
        if (polygon.ValueKind != JsonValueKind.Array || polygon.GetArrayLength() == 0)
            throw Invalid("A polygon must have at least one boundary.");

        foreach (var ring in polygon.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() < 4)
                throw Invalid("A polygon boundary must have at least four positions.");

            points += ring.GetArrayLength();
            if (points > 100_000)
                throw Invalid("The layer exceeds the 100,000-position demo limit.");

            foreach (var position in ring.EnumerateArray())
            {
                if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2 ||
                    !IsCoordinate(position[0], -180, 180) || !IsCoordinate(position[1], -90, 90))
                    throw Invalid("Positions must use WGS 84 longitude followed by latitude.");
            }

            var first = ring[0];
            var last = ring[ring.GetArrayLength() - 1];
            if (first[0].GetDouble() != last[0].GetDouble() || first[1].GetDouble() != last[1].GetDouble())
                throw Invalid("A polygon boundary must end at its starting coordinates.");
        }
    }

    private static string? ReadType(JsonElement element) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;

    private static bool IsCoordinate(JsonElement value, int minimum, int maximum) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number >= minimum && number <= maximum;

    private static SituationError Invalid(string message) => new(400, "InvalidRequest", message);
}
