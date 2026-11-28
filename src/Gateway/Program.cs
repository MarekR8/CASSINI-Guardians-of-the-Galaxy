using System.Net;
using System.Net.Http.Headers;
using Cassini.Gateway.Link;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 9 * 1024 * 1024);
var upstreams = new Dictionary<string, string>
{
    ["management"] = builder.Configuration["Services:Management"] ?? "http://127.0.0.1:5081",
    ["situation"] = builder.Configuration["Services:Situation"] ?? "http://127.0.0.1:5082",
    ["routing"] = builder.Configuration["Services:Routing"] ?? "http://127.0.0.1:5083",
    ["dispatch"] = builder.Configuration["Services:Dispatch"] ?? "http://127.0.0.1:5084",
};
if (builder.Configuration["Services:Sheltering"] is { Length: > 0 } sheltering)
    upstreams["sheltering"] = sheltering;

builder.Services.AddHttpClient("services", client => client.Timeout = TimeSpan.FromSeconds(6))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
    });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IUpstreamCommands>(services =>
    new UpstreamCommands(services.GetRequiredService<IHttpClientFactory>(), upstreams));
builder.Services.AddSingleton(services => new LinkSimulator(services.GetRequiredService<IUpstreamCommands>(),
    services.GetRequiredService<TimeProvider>(), builder.Configuration["Demo:DataDirectory"]));
builder.Services.AddHostedService<LinkForwarder>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]"))
    {
        context.Response.StatusCode = 400;
        return;
    }
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.Context.Request.Path.StartsWithSegments("/maps") &&
            Path.GetExtension(context.File.Name) == ".jpg")
            context.Context.Response.Headers.CacheControl = "private,max-age=86400";
    },
});
var cityDirectory = builder.Configuration["Demo:CityDirectory"];
if (!string.IsNullOrWhiteSpace(cityDirectory))
{
    var assets = new (string Route, string File, string ContentType)[]
    {
        ("city.svg", "city-map.svg", "image/svg+xml"),
        ("city-nodes.svg", "city-nodes.svg", "image/svg+xml"),
        ("city-imagery.json", "city-imagery.json", "application/json"),
        ("sentinel-before.jpg", "sentinel-before.jpg", "image/jpeg"),
        ("sentinel-event.jpg", "sentinel-event.jpg", "image/jpeg"),
    };
    foreach (var asset in assets)
    {
        var file = Path.GetFullPath(Path.Combine(cityDirectory, asset.File));
        if (File.Exists(file))
            app.MapGet("/maps/" + asset.Route, () => Results.File(file, asset.ContentType));
    }
}
app.MapGet("/health", () => Results.Ok(new { service = "gateway" }));
app.MapGet("/features", () => Results.Ok(new { sheltering = upstreams.ContainsKey("sheltering") }));
app.MapGet("/api/{service}/queries/{operation}/{id?}", (
    string service, string operation, string? id, HttpContext context, IHttpClientFactory clients) =>
    Forward(service, "queries", operation, id, context, clients));
app.MapLinkSimulator();
app.MapPost("/api/{service}/commands/{operation}", (
    string service, string operation, HttpContext context, IHttpClientFactory clients, LinkSimulator link) =>
    LinkEndpoints.IsFieldRequest(context) && upstreams.ContainsKey(service) && IsSegment(operation) &&
    context.Request.HasJsonContentType()
        ? LinkEndpoints.RelayFieldCommand(context, link, service, operation)
        : Forward(service, "commands", operation, null, context, clients));
app.Run();

async Task Forward(string service, string group, string operation, string? id, HttpContext context,
    IHttpClientFactory clients)
{
    if (!upstreams.TryGetValue(service, out var baseUrl) || !IsSegment(operation) ||
        (id is not null && !IsSegment(id)))
    {
        context.Response.StatusCode = 404;
        return;
    }

    var uri = $"{baseUrl.TrimEnd('/')}/{group}/{operation}";
    if (id is not null)
        uri += $"/{id}";
    uri += context.Request.QueryString.ToUriComponent();
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), uri);
    // Owner services authorise the caller themselves (Sheltering reception in Stage B); the Gateway only forwards.
    if (context.Request.Headers.Authorization.ToString() is { Length: > 0 } authorization)
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    if (group == "commands")
    {
        if (!context.Request.HasJsonContentType())
        {
            context.Response.StatusCode = 415;
            return;
        }
        request.Content = new StreamContent(context.Request.Body);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType!);
        request.Content.Headers.ContentLength = context.Request.ContentLength;
    }

    try
    {
        using var response = await clients.CreateClient("services").SendAsync(
            request, HttpCompletionOption.ResponseContentRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (HttpRequestException)
    {
        await Unavailable();
    }
    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        await Unavailable();
    }

    async Task Unavailable()
    {
        context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { code = "ServiceUnavailable", service });
    }
}

static bool IsSegment(string value) =>
    value.Length is > 0 and <= 120 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
