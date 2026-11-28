using System.Net.Http.Headers;

namespace Cassini.Gateway.Link;

internal sealed record UpstreamResponse(int Status, string? ContentType, byte[] Body);

internal interface IUpstreamCommands
{
    /// <summary>Sends a buffered command to its owner. Returns null when the service cannot be reached.</summary>
    Task<UpstreamResponse?> SendAsync(string service, string operation, string contentType, byte[] body,
        CancellationToken cancellation);
}

internal sealed class UpstreamCommands(IHttpClientFactory clients, IReadOnlyDictionary<string, string> upstreams)
    : IUpstreamCommands
{
    public async Task<UpstreamResponse?> SendAsync(string service, string operation, string contentType, byte[] body,
        CancellationToken cancellation)
    {
        if (!upstreams.TryGetValue(service, out var baseUrl))
            return new(404, null, []);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/commands/{operation}")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        try
        {
            using var response = await clients.CreateClient("services").SendAsync(request, cancellation);
            return new((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(),
                await response.Content.ReadAsByteArrayAsync(cancellation));
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return null;
        }
    }
}
