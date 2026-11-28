using System.Security.Cryptography;
using System.Text.Json;

namespace Cassini.Routing;

internal interface IRouteProvider
{
    Task<RouteCandidate?> FindAsync(RouteRequest request, CancellationToken cancellation);
}

internal sealed record RouteRequest(string Profile, double[][] Coordinates, double[][][][] AvoidPolygons)
{
    internal byte[] RequestBody()
    {
        var options = AvoidPolygons.Length == 0 ? null :
            new { avoid_polygons = new { type = "MultiPolygon", coordinates = AvoidPolygons } };
        return options is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { coordinates = Coordinates })
            : JsonSerializer.SerializeToUtf8Bytes(new { coordinates = Coordinates, options });
    }

    internal string FixtureKey()
    {
        var request = RequestBody();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Profile + "\n"));
        hash.AppendData(request);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

internal sealed record RouteProvenance(string Source, string Mode, DateTimeOffset ObservedAt);
internal sealed record RouteCandidate(double[][] Geometry, int Seconds, RouteProvenance Provenance);

internal static class OrsGeoJson
{
    internal static RouteCandidate? Read(JsonElement payload, RouteProvenance provenance)
    {
        if (payload.GetProperty("type").GetString() != "FeatureCollection")
            throw new JsonException("Expected a GeoJSON FeatureCollection.");
        var features = payload.GetProperty("features");
        if (features.GetArrayLength() == 0)
            return null;
        var feature = features[0];
        var geometry = feature.GetProperty("geometry");
        var duration = feature.GetProperty("properties").GetProperty("summary").GetProperty("duration").GetDouble();
        if (geometry.GetProperty("type").GetString() != "LineString" ||
            !double.IsFinite(duration) || duration <= 0 || duration > int.MaxValue)
            throw new JsonException("Invalid route type or duration.");

        var line = geometry.GetProperty("coordinates").Deserialize<double[][]>();
        if (line is not { Length: >= 2 and <= 100_000 } || line.Any(point => point is not { Length: 2 } ||
            !double.IsFinite(point[0]) || !double.IsFinite(point[1]) ||
            point[0] is < -180 or > 180 || point[1] is < -90 or > 90))
            throw new JsonException("Invalid route coordinates.");
        return new(line, (int)Math.Ceiling(duration), provenance);
    }

    internal static bool IsInvalidPayload(Exception exception) =>
        exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException;
}
