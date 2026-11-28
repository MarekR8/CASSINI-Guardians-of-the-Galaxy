using System.Text.Json;

namespace Situation;

internal sealed record IntegrationArea(string Id, double[] Bounds, string ActivationCode, int AoiNumber,
    DateTimeOffset ObservedAt)
{
    internal static IntegrationArea ForScenario(string scenario) => scenario == "valencia"
        ? new("valencia", [-0.47, 39.40, -0.33, 39.47], "EMSR773", 1,
            new DateTimeOffset(2024, 10, 31, 18, 2, 0, TimeSpan.Zero))
        : new("klodzko", [16.642, 50.423, 16.664, 50.442], "EMSR756", 3,
            new DateTimeOffset(2024, 9, 15, 5, 1, 0, TimeSpan.Zero));
}

internal sealed record CopernicusPreview(string Mode, string ActivationCode, int AoiNumber, string ProductType,
    string ProductId, DateTimeOffset ObservedAt, double[] Bounds, JsonElement GeoJson);

internal sealed record FlyPixPreview(string Mode, string LayerId, string ImageProductId, string ModelId,
    DateTimeOffset ObservedAt, double[] Bounds, JsonElement GeoJson);

internal interface ICopernicusSource
{
    Task<CopernicusPreview> GetDelineation(IntegrationArea area, CancellationToken cancellationToken);
}

internal interface IFlyPixSource
{
    Task<FlyPixPreview> GetDetections(IntegrationArea area, CancellationToken cancellationToken);
}

internal sealed record CityZoneCount(string ZoneId, int People);
internal sealed record CityData(string Mode, string DatasetId, CityZoneCount[] Zones);
internal sealed record CityMap(string Mode, string DatasetId, double[] Bounds, JsonElement GeoJson);

internal interface ICitySource
{
    Task<CityData> GetData(IntegrationArea area, CancellationToken cancellationToken);
    Task<CityMap> GetMap(IntegrationArea area, CancellationToken cancellationToken);
}

internal sealed class MockCopernicusSource : ICopernicusSource
{
    public Task<CopernicusPreview> GetDelineation(IntegrationArea area, CancellationToken cancellationToken) =>
        Task.FromResult(new CopernicusPreview("mock", area.ActivationCode, area.AoiNumber, "delineation",
            $"mock-{area.ActivationCode}-AOI{area.AoiNumber:00}-DEL", area.ObservedAt, area.Bounds,
            MockGeometry.Rectangle(area, .24, .25, .68, .66, new { kind = "flood_extent" })));
}

internal sealed class MockFlyPixSource : IFlyPixSource
{
    public Task<FlyPixPreview> GetDetections(IntegrationArea area, CancellationToken cancellationToken) =>
        Task.FromResult(new FlyPixPreview("mock", $"mock-{area.Id}-road-review", $"mock-{area.Id}-event-raster",
            "mock-road-impact-model", area.ObservedAt, area.Bounds,
            MockGeometry.Rectangle(area, .50, .38, .58, .46, new { kind = "suspected_road_impact", confidence = .82 })));
}

internal sealed class MockCitySource : ICitySource
{
    private const string ZoneId = "exercise-zone-1";

    public Task<CityData> GetData(IntegrationArea area, CancellationToken cancellationToken) =>
        Task.FromResult(new CityData("mock", DatasetId(area), [new(ZoneId, 120)]));

    public Task<CityMap> GetMap(IntegrationArea area, CancellationToken cancellationToken) =>
        Task.FromResult(new CityMap("mock", DatasetId(area), area.Bounds,
            MockGeometry.Rectangle(area, .15, .18, .80, .76, new { zoneId = ZoneId })));

    private static string DatasetId(IntegrationArea area) => $"mock-{area.Id}-city-v1";
}

internal static class MockGeometry
{
    internal static JsonElement Rectangle<T>(IntegrationArea area, double left, double bottom, double right,
        double top, T properties)
    {
        var bounds = area.Bounds;
        double[] Position(double x, double y) =>
        [
            Math.Round(bounds[0] + (bounds[2] - bounds[0]) * x, 6),
            Math.Round(bounds[1] + (bounds[3] - bounds[1]) * y, 6),
        ];
        double[][] ring = [Position(left, bottom), Position(right, bottom), Position(right, top),
            Position(left, top), Position(left, bottom)];

        return JsonSerializer.SerializeToElement(new
        {
            type = "FeatureCollection",
            features = new[] { new { type = "Feature", properties, geometry = new { type = "Polygon", coordinates = new[] { ring } } } }
        });
    }
}
