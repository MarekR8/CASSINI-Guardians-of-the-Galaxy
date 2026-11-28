using System.Globalization;
using System.Text.Json;

namespace Cassini.Routing.Evacuation;

internal sealed record DischargeSample(DateTimeOffset Time, double DischargeM3s);

internal sealed record ForecastPoint(string PointId, string Name, double Latitude, double Longitude, DischargeSample[] Series);

internal sealed record ShelterExposure(string ShelterId, string PointId, double ThresholdM3s);

/// <summary>
/// T_safe of one shelter. Status: Unsafe (already above threshold at the plan start), SafeUntil (crosses later),
/// SafeThroughHorizon (never crosses within the forecast) or NotAssessed (no exposure entry).
/// </summary>
internal sealed record ShelterSafety(
    string ShelterId, string Status, string? PointId, double? ThresholdM3s, double? PeakM3s, DateTimeOffset? UnsafeFrom,
    double? SafeUntilMinutes);

/// <summary>
/// One forecast as used by a plan. Status: Ok, Cached (last good live forecast after a failed refresh) or
/// Unavailable (no limit applied; the plan says so instead of implying the exits stay dry).
/// </summary>
internal sealed record HazardForecast(
    string Mode, string Source, string Label, string Status, DateTimeOffset? IssuedAt, DateTimeOffset PlanStart,
    DateTimeOffset? FetchedAt, ShelterSafety[] Shelters, string? Detail = null)
{
    internal static HazardForecast Off(DateTimeOffset now) => new("off", "none", "none", "NotConfigured", null, now, null, []);

    internal EvacShelter[] Apply(EvacShelter[] shelters)
    {
        var limits = Shelters.ToDictionary(item => item.ShelterId, item => item.SafeUntilMinutes, StringComparer.Ordinal);
        return shelters.Select(shelter => shelter with { SafeUntilMinutes = limits.GetValueOrDefault(shelter.Id) }).ToArray();
    }
}

/// <summary>Source of T_safe per exit (task 03 Stage B): a forecast file offline, GloFAS via Open-Meteo when live.</summary>
internal interface IHazardForecast
{
    Task<HazardForecast> ReadAsync(IReadOnlyList<EvacShelter> shelters, CancellationToken cancellationToken);
}

internal sealed record ForecastDocument(
    string Source, string Label, DateTimeOffset? IssuedAt, DateTimeOffset PlanStart, ForecastPoint[] Points,
    ShelterExposure[] Exposure)
{
    internal const string Version = "cassini.hazard-forecast.v1";

    internal static ForecastDocument Read(string path)
    {
        using var json = EvacuationInputs.ReadJson(path);
        var root = json.RootElement;
        if (root.GetProperty("forecastVersion").GetString() != Version)
            throw new InvalidDataException($"forecastVersion must be {Version}.");
        var points = root.GetProperty("points").EnumerateArray().Select(point => new ForecastPoint(
            point.GetProperty("pointId").GetString()!, point.GetProperty("name").GetString()!,
            point.GetProperty("latitude").GetDouble(), point.GetProperty("longitude").GetDouble(),
            point.TryGetProperty("series", out var series)
                ? series.EnumerateArray().Select(sample => new DischargeSample(
                    sample.GetProperty("time").GetDateTimeOffset(), sample.GetProperty("dischargeM3s").GetDouble())).ToArray()
                : [])).ToArray();
        var exposure = root.GetProperty("exposure").EnumerateArray().Select(item => new ShelterExposure(
            item.GetProperty("shelterId").GetString()!, item.GetProperty("pointId").GetString()!,
            item.GetProperty("thresholdM3s").GetDouble())).ToArray();
        if (exposure.Any(item => points.All(point => point.PointId != item.PointId) || item.ThresholdM3s <= 0))
            throw new InvalidDataException("Each exposure needs a known pointId and a positive thresholdM3s.");
        return new(root.GetProperty("source").GetString()!, root.GetProperty("label").GetString()!,
            root.TryGetProperty("issuedAt", out var issued) && issued.ValueKind == JsonValueKind.String ? issued.GetDateTimeOffset() : null,
            root.GetProperty("planStart").GetDateTimeOffset(), points, exposure);
    }
}

internal static class ForecastRules
{
    /// <summary>
    /// First time the discharge reaches the threshold, interpolated linearly between samples; null if it never does
    /// within the series. Same rule as experiments/routing/forecast.py.
    /// </summary>
    internal static DateTimeOffset? Crossing(IReadOnlyList<DischargeSample> series, double threshold)
    {
        var ordered = series.OrderBy(sample => sample.Time).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].DischargeM3s >= threshold)
            {
                if (index == 0)
                    return ordered[0].Time;
                var (before, after) = (ordered[index - 1], ordered[index]);
                var fraction = (threshold - before.DischargeM3s) / (after.DischargeM3s - before.DischargeM3s);
                return before.Time + (after.Time - before.Time) * fraction;
            }
        }
        return null;
    }

    internal static ShelterSafety[] Evaluate(ForecastDocument forecast, IReadOnlyList<EvacShelter> shelters,
        DateTimeOffset planStart)
    {
        var points = forecast.Points.ToDictionary(point => point.PointId, StringComparer.Ordinal);
        var exposure = forecast.Exposure.ToDictionary(item => item.ShelterId, StringComparer.Ordinal);
        return shelters.Select(shelter =>
        {
            if (!exposure.TryGetValue(shelter.Id, out var item))
                return new ShelterSafety(shelter.Id, "NotAssessed", null, null, null, null, null);
            var series = points[item.PointId].Series;
            var peak = series.Length == 0 ? (double?)null : series.Max(sample => sample.DischargeM3s);
            if (Crossing(series, item.ThresholdM3s) is not { } crossing)
                return new ShelterSafety(shelter.Id, "SafeThroughHorizon", item.PointId, item.ThresholdM3s, peak, null, null);
            var minutes = Math.Max(0, (crossing - planStart).TotalMinutes);
            return new ShelterSafety(shelter.Id, minutes > 0 ? "SafeUntil" : "Unsafe", item.PointId, item.ThresholdM3s, peak,
                crossing, Math.Round(minutes, 6));
        }).ToArray();
    }
}

/// <summary>Offline forecast from a file, with its own plan start (the exercise clock).</summary>
internal sealed class MockGlofasForecast(string path) : IHazardForecast
{
    private readonly ForecastDocument document = ForecastDocument.Read(path);

    public Task<HazardForecast> ReadAsync(IReadOnlyList<EvacShelter> shelters, CancellationToken cancellationToken) =>
        Task.FromResult(new HazardForecast("mock", document.Source, document.Label, "Ok", document.IssuedAt,
            document.PlanStart, null, ForecastRules.Evaluate(document, shelters, document.PlanStart)));
}

/// <summary>
/// GloFAS river discharge from the Open-Meteo Flood API for the points of the forecast file, with its thresholds.
/// Daily values are placed at 12:00 UTC. A failed refresh reuses the last good forecast for up to maxAge, then
/// reports Unavailable. Opt-in only: it needs the internet and is never used on the offline path.
/// </summary>
internal sealed class LiveGlofasForecast(HttpClient http, string path, TimeProvider clock, TimeSpan maxAge,
    ILogger<LiveGlofasForecast> log) : IHazardForecast
{
    internal const string Endpoint = "https://flood-api.open-meteo.com/v1/flood";
    private readonly ForecastDocument template = ForecastDocument.Read(path);
    private (ForecastDocument Document, DateTimeOffset FetchedAt)? lastGood;

    public async Task<HazardForecast> ReadAsync(IReadOnlyList<EvacShelter> shelters, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        try
        {
            var document = template with { Source = "glofas-open-meteo", Label = "forecast", IssuedAt = null, PlanStart = now,
                Points = await FetchAsync(cancellationToken) };
            lastGood = (document, now);
            return new("live", document.Source, document.Label, "Ok", null, now, now,
                ForecastRules.Evaluate(document, shelters, now));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException
                                              or InvalidDataException or KeyNotFoundException)
        {
            log.LogWarning("GloFAS forecast unavailable: {Message}", exception.Message);
            if (lastGood is { } cached && now - cached.FetchedAt <= maxAge)
                return new("live", cached.Document.Source, cached.Document.Label, "Cached", null, now, cached.FetchedAt,
                    ForecastRules.Evaluate(cached.Document, shelters, now), exception.Message);
            return new("live", "glofas-open-meteo", "forecast", "Unavailable", null, now, null, [], exception.Message);
        }
    }

    private async Task<ForecastPoint[]> FetchAsync(CancellationToken cancellationToken)
    {
        string Join(Func<ForecastPoint, double> value) =>
            string.Join(',', template.Points.Select(point => value(point).ToString("0.####", CultureInfo.InvariantCulture)));
        var uri = $"{Endpoint}?latitude={Join(point => point.Latitude)}&longitude={Join(point => point.Longitude)}" +
                  "&daily=river_discharge&past_days=1&forecast_days=7";
        using var response = await http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return ParseDaily(json.RootElement, template.Points);
    }

    /// <summary>Open-Meteo returns one object for one location and an array for several, in request order.</summary>
    internal static ForecastPoint[] ParseDaily(JsonElement root, IReadOnlyList<ForecastPoint> points)
    {
        var locations = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
        if (locations.Length != points.Count)
            throw new InvalidDataException($"Expected {points.Count} locations, got {locations.Length}.");
        return points.Select((point, index) =>
        {
            var daily = locations[index].GetProperty("daily");
            var times = daily.GetProperty("time").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var values = daily.GetProperty("river_discharge").EnumerateArray().ToArray();
            var series = times.Zip(values)
                .Where(pair => pair.Second.ValueKind == JsonValueKind.Number)
                .Select(pair => new DischargeSample(
                    DateTimeOffset.ParseExact(pair.First, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).AddHours(12),
                    pair.Second.GetDouble())).ToArray();
            return point with { Series = series };
        }).ToArray();
    }
}

/// <summary>Configuration switch: Routing:Forecast:Mode = mock (default), live or off.</summary>
internal sealed class NoHazardForecast(TimeProvider clock) : IHazardForecast
{
    public Task<HazardForecast> ReadAsync(IReadOnlyList<EvacShelter> shelters, CancellationToken cancellationToken) =>
        Task.FromResult(HazardForecast.Off(clock.GetUtcNow()));
}
