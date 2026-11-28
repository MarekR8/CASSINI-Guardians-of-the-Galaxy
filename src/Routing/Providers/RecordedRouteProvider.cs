using System.Text.Json;

namespace Cassini.Routing;

internal sealed class RecordedRouteProvider(string directory) : IRouteProvider
{
    public async Task<RouteCandidate?> FindAsync(RouteRequest request, CancellationToken cancellation)
    {
        var key = request.FixtureKey();
        try
        {
            await using var file = File.OpenRead(Path.Combine(directory, key + ".json"));
            using var document = await JsonDocument.ParseAsync(file, cancellationToken: cancellation);
            var fixture = document.RootElement;
            var mode = fixture.GetProperty("mode").GetString();
            var observedAt = fixture.GetProperty("observedAt").GetDateTimeOffset();
            if (mode is not ("recorded" or "synthetic"))
                throw new JsonException("A route fixture must declare its provenance.");
            return OrsGeoJson.Read(fixture.GetProperty("response"),
                new(mode == "recorded" ? "openrouteservice" : "exercise", mode, observedAt));
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new RoutingException(503, "FixtureMissing", $"No route fixture matches request {key}.");
        }
        catch (Exception exception) when (OrsGeoJson.IsInvalidPayload(exception) ||
            exception is IOException or UnauthorizedAccessException)
        {
            throw new RoutingException(503, "InvalidRouteFixture", $"Route fixture {key} could not be read.");
        }
    }
}
