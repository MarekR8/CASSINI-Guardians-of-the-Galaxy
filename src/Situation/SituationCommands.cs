using System.Security.Cryptography;
using System.Text.Json;

namespace Situation;

internal sealed record ClosureCommand(string? CommandId, string? SegmentId, bool? Closed, int? ExpectedRevision);
internal sealed record ImportCopernicusCommand(
    string? CommandId, int? ExpectedRevision, string? ProductId, DateTimeOffset? ObservedAt, JsonElement GeoJson,
    string? SourceUrl = null, string? Source = null);

internal sealed class SituationCommands(SituationStore store, ReviewNetwork reviewNetwork)
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
            var evidence = new ClosureEvidence("operator", null, null, DateTimeOffset.UtcNow);
            return UpdateClosures(snapshot, [segment], closed, evidence);
        });
    }

    internal SituationSnapshot ImportReview(ImportReviewCommand command)
    {
        var (id, revision) = CheckVersion(command.CommandId, command.ExpectedRevision);
        var incoming = reviewNetwork.Read(command);
        return store.Apply(id, revision, Fingerprint("import-review", command), snapshot =>
        {
            var existing = snapshot.ReviewFeatures ?? [];
            var existingIds = existing.Select(feature => feature.Id).ToHashSet(StringComparer.Ordinal);
            var additions = incoming.Where(feature => !existingIds.Contains(feature.Id)).ToArray();
            return additions.Length == 0 ? snapshot : snapshot with
            {
                Revision = snapshot.Revision + 1,
                ReviewFeatures = [.. existing, .. additions],
            };
        });
    }

    internal SituationSnapshot DecideReview(ReviewDecisionCommand command)
    {
        var (id, revision) = CheckVersion(command.CommandId, command.ExpectedRevision);
        if (command.Decision is not ("approve" or "reject") || string.IsNullOrEmpty(command.ReviewFeatureId))
            throw Invalid("Specify a reviewFeatureId and approve or reject.");

        return store.Apply(id, revision, Fingerprint("review-decision", command), snapshot =>
        {
            var features = snapshot.ReviewFeatures ?? [];
            var feature = features.SingleOrDefault(item => item.Id == command.ReviewFeatureId)
                ?? throw new SituationError(404, "ReviewFeatureMissing", "The review feature was not found.");
            if (feature.Status != "Pending")
                throw new SituationError(409, "ReviewAlreadyDecided", "The review feature already has a decision.");
            var approve = command.Decision == "approve";
            if (approve && feature.SegmentIds.Length == 0)
                throw Invalid("Confirm a feature that intersects at least one exercise corridor.");

            var decidedAt = DateTimeOffset.UtcNow;
            var evidenceSource = feature.Mode == "synthetic" ? "exercise-verified" : "flypix-verified";
            var updated = approve
                ? UpdateClosures(snapshot, feature.SegmentIds, true,
                    new ClosureEvidence(evidenceSource, feature.Id, feature.ObservedAt, decidedAt))
                : snapshot;
            return updated with
            {
                Revision = snapshot.Revision + 1,
                ReviewFeatures = features.Select(item => item.Id == feature.Id
                    ? item with { Status = approve ? "Approved" : "Rejected", DecidedAt = decidedAt }
                    : item).ToArray()
            };
        });
    }

    private static SituationSnapshot UpdateClosures(SituationSnapshot snapshot, string[] segments, bool closed,
        ClosureEvidence evidence)
    {
        var closures = new SortedSet<string>(snapshot.ClosedSegments, StringComparer.Ordinal);
        var changed = segments.Where(segment => closed ? closures.Add(segment) : closures.Remove(segment)).ToArray();
        if (changed.Length == 0)
            return snapshot;

        var stage = closures.Contains("bridge-2") ? (closures.Contains("north-link") ? 2 : 1) : 0;
        var revision = snapshot.Revision + 1;
        var segmentRevisions = new Dictionary<string, int>(snapshot.SegmentRevisions);
        var activeEvidence = snapshot.ClosureEvidence is null
            ? new Dictionary<string, ClosureEvidence>() : new(snapshot.ClosureEvidence);
        foreach (var segment in changed)
        {
            segmentRevisions[segment] = revision;
            if (closed) activeEvidence[segment] = evidence;
            else activeEvidence.Remove(segment);
        }
        return snapshot with
        {
            Revision = revision, Stage = stage, ClosedSegments = closures.ToArray(),
            SegmentRevisions = segmentRevisions, ClosureEvidence = activeEvidence
        };
    }

    internal SituationSnapshot ImportCopernicus(ImportCopernicusCommand command)
    {
        var (id, revision) = CheckVersion(command.CommandId, command.ExpectedRevision);
        if (command.ProductId?.Trim() is not { Length: > 0 and <= 120 } product ||
            command.ObservedAt is not { } observedAt)
            throw Invalid("A Copernicus EMS product ID and observation timestamp are required.");

        var geometry = ReadPolygons(command.GeoJson);
        var source = command.Source switch
        {
            null or "ems" => "copernicus-ems-operator-import",
            "sentinel-1" => "copernicus-sentinel-1-operator-import",
            _ => throw Invalid("Specify ems or sentinel-1 as the Copernicus source.")
        };
        var layer = new HazardLayer((command.Source == "sentinel-1" ? "sentinel-1:" : "copernicus:") + product, source,
            product, observedAt.ToUniversalTime(), geometry, CheckSourceUrl(command.SourceUrl));
        var fingerprint = command.Source is null && command.SourceUrl is null
            ? Fingerprint("copernicus", new { command.CommandId, command.ExpectedRevision, command.ProductId,
                command.ObservedAt, command.GeoJson })
            : Fingerprint("copernicus", command);
        return store.Apply(id, revision, fingerprint, snapshot =>
        {
            var existing = snapshot.Hazards.FirstOrDefault(item => item.Id == layer.Id);
            if (existing is not null && existing.ObservedAt == layer.ObservedAt && existing.SourceUrl == layer.SourceUrl &&
                JsonElement.DeepEquals(existing.Geometry, layer.Geometry))
                return snapshot;
            return snapshot with
            {
                Revision = snapshot.Revision + 1,
                Hazards = [.. snapshot.Hazards.Where(item => item.Id != layer.Id), layer]
            };
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

    internal static void CheckPolygon(JsonElement polygon, ref int points)
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

    internal static string? ReadType(JsonElement element) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;

    private static bool IsCoordinate(JsonElement value, int minimum, int maximum) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && number >= minimum && number <= maximum;

    internal static string? CheckSourceUrl(string? sourceUrl)
    {
        if (sourceUrl is null) return null;
        if (sourceUrl.Length > 400 || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
            throw Invalid("Provide an HTTPS URL for the public source.");
        return sourceUrl;
    }

    internal static SituationError Invalid(string message) => new(400, "InvalidRequest", message);
}
