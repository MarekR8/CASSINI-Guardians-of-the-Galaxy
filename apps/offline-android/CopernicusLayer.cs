using System.Globalization;
using System.Text.Json;

namespace Cassini.Offline;

internal sealed record CopernicusLayer(string? Product, DateTimeOffset? ObservedAt, IReadOnlyList<MapPoint[][]> Polygons)
{
    internal const int MaxBytes = 8 * 1024 * 1024;
    private const int MaxPoints = 100_000;

    internal static CopernicusLayer Read(byte[] bytes)
    {
        if (bytes.Length > MaxBytes)
            throw new InvalidDataException("Plik przekracza 8 MB.");

        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() != "FeatureCollection" ||
            !root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Wybierz plik GeoJSON typu FeatureCollection.");

        var polygons = new List<MapPoint[][]>();
        var pointCount = 0;
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.ValueKind != JsonValueKind.Object ||
                !feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object ||
                !geometry.TryGetProperty("type", out var geometryType) || geometryType.ValueKind != JsonValueKind.String ||
                !geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
                continue;

            switch (geometryType.GetString())
            {
                case "Polygon":
                    polygons.Add(ReadPolygon(coordinates, ref pointCount));
                    break;
                case "MultiPolygon":
                    foreach (var polygon in coordinates.EnumerateArray())
                        polygons.Add(ReadPolygon(polygon, ref pointCount));
                    break;
            }
        }

        if (polygons.Count == 0)
            throw new InvalidDataException("Plik nie zawiera obszarów Polygon ani MultiPolygon.");

        var product = ReadLabel(root, "productId");
        var date = ReadLabel(root, "observedAt");
        DateTimeOffset? observedAt = null;
        if (date is not null)
        {
            if (!DateTimeOffset.TryParseExact(date, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var observation))
                throw new InvalidDataException("Pole observedAt musi zawierać datę i czas w formacie ISO 8601.");
            observedAt = observation;
        }

        return new CopernicusLayer(product, observedAt, polygons);
    }

    private static string? ReadLabel(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 and <= 100 } label ? label : null;

    private static MapPoint[][] ReadPolygon(JsonElement polygon, ref int pointCount)
    {
        if (polygon.ValueKind != JsonValueKind.Array || polygon.GetArrayLength() == 0)
            throw new InvalidDataException("Obszar nie ma obrysu.");

        var rings = new List<MapPoint[]>();
        foreach (var ring in polygon.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() < 4)
                throw new InvalidDataException("Obrys obszaru jest nieprawidłowy.");

            pointCount += ring.GetArrayLength();
            if (pointCount > MaxPoints)
                throw new InvalidDataException("Warstwa przekracza 100 tys. punktów.");

            var points = new List<MapPoint>();
            foreach (var position in ring.EnumerateArray())
            {
                if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() < 2 ||
                    position[0].ValueKind != JsonValueKind.Number || !position[0].TryGetDouble(out var lon) ||
                    position[1].ValueKind != JsonValueKind.Number || !position[1].TryGetDouble(out var lat) ||
                    !double.IsFinite(lon) || lon is < -180 or > 180 ||
                    !double.IsFinite(lat) || lat is < -90 or > 90)
                    throw new InvalidDataException("Współrzędne muszą być podane w kolejności długość, szerokość (WGS 84).");

                points.Add(new MapPoint(lon, lat));
            }

            if (points[0] != points[^1])
                throw new InvalidDataException("Obrys obszaru musi być zamknięty.");
            rings.Add(points.ToArray());
        }

        return rings.ToArray();
    }
}
