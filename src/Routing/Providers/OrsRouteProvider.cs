using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Cassini.Routing;

internal sealed class OrsRouteProvider(HttpClient client, string apiKey) : IRouteProvider
{
    public async Task<RouteCandidate?> FindAsync(RouteRequest request, CancellationToken cancellation)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"v2/directions/{request.Profile}/geojson");
        message.Headers.Add("Authorization", apiKey);
        message.Content = new ByteArrayContent(request.RequestBody());
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        try
        {
            using var response = await client.SendAsync(message, cancellation);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellation));
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest &&
                    payload.RootElement.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) &&
                    value is 2009 or 2010)
                    return null;

                throw new RoutingException(503, "OrsUnavailable", "OpenRouteService could not evaluate the route.");
            }
            return OrsGeoJson.Read(payload.RootElement, new("openrouteservice", "live", DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException ||
            exception is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new RoutingException(503, "OrsUnavailable", "OpenRouteService is unavailable.");
        }
        catch (Exception exception) when (OrsGeoJson.IsInvalidPayload(exception))
        {
            throw new RoutingException(502, "InvalidOrsResponse", "OpenRouteService returned an invalid route response.");
        }
    }
}
