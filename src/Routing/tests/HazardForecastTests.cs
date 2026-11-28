using System.Text.Json;
using Cassini.Routing.Evacuation;

/// <summary>T_safe per shelter (task 03 Stage B): crossing rule, the bundled mock and the Open-Meteo response shape.</summary>
internal static class HazardForecastTests
{
    internal static void Run()
    {
        var t0 = DateTimeOffset.Parse("2024-10-29T17:00:00Z");
        DischargeSample[] series = [new(t0, 900), new(t0.AddHours(1), 1700), new(t0.AddHours(2), 2200)];
        Require(ForecastRules.Crossing(series, 1300) == t0.AddMinutes(30), "linear interpolation between samples");
        Require(ForecastRules.Crossing(series, 500) == t0, "already above the threshold at the first sample");
        Require(ForecastRules.Crossing(series, 5000) is null, "never above the threshold");

        // Same values as `python experiments/routing/forecast.py`.
        var path = Path.Combine(AppContext.BaseDirectory, "routing-fixtures", "glofas-forecast.mock.json");
        var shelters = new[] { "shelter-a", "shelter-b", "shelter-c", "shelter-d", "shelter-e" }
            .Select(id => new EvacShelter(id, id, 1, 100, null, true)).ToArray();
        var forecast = new MockGlofasForecast(path).ReadAsync(shelters, CancellationToken.None).Result;
        var safety = forecast.Shelters.ToDictionary(item => item.ShelterId);
        Require(forecast.Status == "Ok" && forecast.Label == "synthetic", "mock forecast is labelled synthetic");
        Require(safety["shelter-a"].SafeUntilMinutes == 60 && safety["shelter-b"].SafeUntilMinutes == 96 &&
                safety["shelter-e"].SafeUntilMinutes == 72, "mock T_safe matches the Python reference");
        Require(safety["shelter-c"].Status == "SafeThroughHorizon" && safety["shelter-c"].SafeUntilMinutes is null,
            "a shelter whose threshold is never reached has no limit");
        Require(safety["shelter-d"].Status == "NotAssessed", "a shelter without exposure is not assessed");
        var applied = forecast.Apply(shelters);
        Require(applied[0].SafeUntilMinutes == 60 && applied[2].SafeUntilMinutes is null, "T_safe reaches the model input");

        // Open-Meteo returns an object for one location and an array for several; daily values sit at 12:00 UTC.
        ForecastPoint[] points = [new("p1", "p1", 39.4, -0.4, []), new("p2", "p2", 39.5, -0.3, [])];
        using var many = JsonDocument.Parse("""
            [{"daily":{"time":["2026-11-27","2026-11-28"],"river_discharge":[10.5,null]}},
             {"daily":{"time":["2026-11-27"],"river_discharge":[3.0]}}]
            """);
        var parsed = LiveGlofasForecast.ParseDaily(many.RootElement, points);
        Require(parsed[0].Series.Length == 1 && parsed[0].Series[0].DischargeM3s == 10.5 &&
                parsed[0].Series[0].Time == DateTimeOffset.Parse("2026-11-27T12:00:00Z"), "daily series parsed, nulls skipped");
        using var one = JsonDocument.Parse("""{"daily":{"time":["2026-11-27"],"river_discharge":[1.0]}}""");
        Require(LiveGlofasForecast.ParseDaily(one.RootElement, points[..1])[0].Series.Length == 1, "single location object");
        Console.WriteLine("Hazard forecast: T_safe crossing, mock values and Open-Meteo parsing verified.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Hazard forecast: " + message);
    }
}
