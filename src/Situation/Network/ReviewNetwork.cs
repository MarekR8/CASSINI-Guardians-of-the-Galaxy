using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cassini.Demo;

namespace Situation;

internal sealed record ImportReviewCommand(string? CommandId, int? ExpectedRevision, string? LayerId,
    string? ImageProductId, DateTimeOffset? ObservedAt, string? Mode, JsonElement GeoJson, string? ModelId = null);
internal sealed record ReviewDecisionCommand(string? CommandId, int? ExpectedRevision, string? ReviewFeatureId, string? Decision);
internal sealed record ReviewFeature(string Id, string Source, string Mode, string LayerId, string ImageProductId,
    DateTimeOffset ObservedAt, string? ModelId, double? Confidence, JsonElement Geometry, string[] SegmentIds,
    string Status = "Pending", DateTimeOffset? DecidedAt = null);
internal sealed record ClosureEvidence(string Source, string? ReviewFeatureId, DateTimeOffset? ObservedAt,
    DateTimeOffset DecidedAt);

internal sealed class ReviewNetwork(CityNetwork? city)
{
    internal bool CityOnly => city is not null;
    internal bool ContainsCitySegment(string id) => city?.Contains(id) == true;
    internal ReviewFeature[] Read(ImportReviewCommand command)
    {
        if (city is null)
            throw new SituationError(409, "ScenarioRequired", "FlyPix road review requires an exercise road network.");
        if (command.LayerId?.Trim() is not { Length: > 0 and <= 120 } layerId ||
            command.ImageProductId?.Trim() is not { Length: > 0 and <= 120 } product ||
            command.ObservedAt is not { } observed || command.ModelId?.Length > 120 ||
            command.Mode is not ("synthetic" or "manual" or "recorded" or "live"))
            throw SituationCommands.Invalid("Provide the layer, image product, UTC observation time and import mode.");
        if (SituationCommands.ReadType(command.GeoJson) != "FeatureCollection" ||
            !command.GeoJson.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array ||
            features.GetArrayLength() > 1000)
            throw SituationCommands.Invalid("Provide a GeoJSON FeatureCollection of up to 1,000 detections.");

        var points = 0;
        var result = new List<ReviewFeature>();
        foreach (var feature in features.EnumerateArray())
        {
            if (SituationCommands.ReadType(feature) != "Feature" || !feature.TryGetProperty("geometry", out var geometry) ||
                SituationCommands.ReadType(geometry) is not ("Polygon" or "MultiPolygon") ||
                !geometry.TryGetProperty("coordinates", out var coordinates) ||
                coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() == 0)
                throw SituationCommands.Invalid("Each detection must contain a Polygon or MultiPolygon.");
            JsonElement[] polygons = SituationCommands.ReadType(geometry) == "Polygon"
                ? [coordinates] : coordinates.EnumerateArray().ToArray();
            foreach (var polygon in polygons)
                SituationCommands.CheckPolygon(polygon, ref points);

            double? confidence = null;
            if (feature.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object &&
                props.TryGetProperty("confidence", out var score) && score.ValueKind != JsonValueKind.Null)
            {
                if (score.ValueKind != JsonValueKind.Number || !score.TryGetDouble(out var value) || value is < 0 or > 1)
                    throw SituationCommands.Invalid("Confidence must be between 0 and 1.");
                confidence = value;
            }
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { layerId, product, observed = observed.ToUniversalTime(), command.ModelId, feature }))))[..24];
            var source = command.Mode switch { "synthetic" => "exercise", "manual" => "flypix-manual", _ => "flypix-ai" };
            result.Add(new ReviewFeature("flypix-" + id, source, command.Mode, layerId, product,
                observed.ToUniversalTime(), command.ModelId, confidence, geometry.Clone(), Match(city, polygons)));
        }
        return result.DistinctBy(feature => feature.Id).ToArray();
    }

    private static string[] Match(CityNetwork city, JsonElement[] polygons)
    {
        var boundaries = polygons.Select(polygon => polygon.EnumerateArray().Select(ring => ring.EnumerateArray()
            .Select(point => (X: point[0].GetDouble(), Y: point[1].GetDouble())).ToArray()).ToArray()).ToArray();
        var points = boundaries.SelectMany(polygon => polygon[0]).ToArray();
        if (boundaries.SelectMany(polygon => polygon).SelectMany(ring => ring)
            .Any(point => !city.InBounds(point.X, point.Y)))
            throw SituationCommands.Invalid("Review geometry must be inside the city bounds.");
        var candidates = city.IntersectingBounds(points.Min(point => point.X), points.Min(point => point.Y),
            points.Max(point => point.X), points.Max(point => point.Y));
        return candidates.Where(road => Intersects(road.Geometry, boundaries))
            .Select(road => road.Id).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool Intersects(double[][] line, (double X, double Y)[][][] polygons)
    {
        foreach (var polygon in polygons)
        {
            for (var index = 1; index < line.Length; index++)
            {
                var a = (X: line[index - 1][0], Y: line[index - 1][1]);
                var b = (X: line[index][0], Y: line[index][1]);
                if ((Inside(a, polygon[0]) && !polygon.Skip(1).Any(ring => Inside(a, ring))) ||
                    (Inside(b, polygon[0]) && !polygon.Skip(1).Any(ring => Inside(b, ring))))
                    return true;
                foreach (var ring in polygon)
                    for (var side = 1; side < ring.Length; side++)
                        if (Crosses(a, b, ring[side - 1], ring[side]))
                            return true;
            }
        }
        return false;
    }

    private static bool Inside((double X, double Y) point, (double X, double Y)[] ring)
    {
        var inside = false;
        for (var index = 1; index < ring.Length; index++)
        {
            var a = ring[index - 1];
            var b = ring[index];
            if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static bool Crosses((double X, double Y) a, (double X, double Y) b,
        (double X, double Y) c, (double X, double Y) d)
    {
        if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) || Math.Max(c.X, d.X) < Math.Min(a.X, b.X) ||
            Math.Max(a.Y, b.Y) < Math.Min(c.Y, d.Y) || Math.Max(c.Y, d.Y) < Math.Min(a.Y, b.Y))
            return false;

        static int Side((double X, double Y) p, (double X, double Y) q, (double X, double Y) r) =>
            Math.Sign((q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X));
        return Side(a, b, c) * Side(a, b, d) <= 0 && Side(c, d, a) * Side(c, d, b) <= 0;
    }
}
