using Cassini.Routing;
using Cassini.Demo;

var builder = WebApplication.CreateBuilder(args);
var network = DemoNetwork.Load(builder.Configuration["Demo:Scenario"]);
Network.Configure(network, network is null ? null : CityNetwork.Load(builder.Configuration["Demo:CityDirectory"]));
if (builder.Configuration["urls"] is null)
    builder.WebHost.UseUrls("http://127.0.0.1:5083");

builder.Services.AddHttpClient<SituationGateway>(client =>
{
    var url = builder.Configuration["Situation:BaseUrl"] ?? "http://127.0.0.1:5082";
    client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(3);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton(_ => new DrawJournal(
    builder.Configuration["Routing:StatePath"] ??
    Path.Combine(builder.Environment.ContentRootPath, "artifacts", "routing-draws.json")));
builder.Services.AddTransient<RoutingQueries>();
builder.Services.AddTransient<RoutingCommands>();
var ors = builder.Configuration.GetSection("Integrations:Ors");
var providerMode = ors["Mode"] ?? "graph";
switch (providerMode)
{
    case "graph":
        break;
    case "mock":
    case "recorded":
        builder.Services.AddSingleton<IRouteProvider>(new RecordedRouteProvider(
            ors["FixtureDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "ors", "recorded")));
        break;
    case "live":
        var apiKey = ors["ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Configure Integrations:Ors:ApiKey for live routing.");
        builder.Services.AddHttpClient("openrouteservice", client =>
        {
            client.BaseAddress = new Uri((ors["BaseUrl"] ?? "https://api.openrouteservice.org").TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(1.5);
        }).AddTypedClient<IRouteProvider>(client => new OrsRouteProvider(client, apiKey))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        break;
    default:
        throw new InvalidOperationException($"Unknown Integrations:Ors:Mode '{providerMode}'.");
}
if (providerMode != "graph")
    builder.Services.AddTransient<AssistanceRouteProvider>();

var app = builder.Build();
_ = app.Services.GetRequiredService<DrawJournal>();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (RoutingException exception)
    {
        await Results.Problem(
            statusCode: exception.Status,
            title: exception.Message,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code })
            .ExecuteAsync(context);
    }
});

app.MapGet("/health", () => Results.Ok(new { service = "routing" }));
app.MapGet("/health/live", () => Results.Ok(new { service = "routing" }));
app.MapGet("/health/ready", () => Results.Ok(new { service = "routing" }));

var queries = app.MapGroup("/queries");
queries.MapGet("/network", () => new {
    Network.Nodes, Network.Segments, Network.Exits, Network.Warsaw?.Bounds,
    Network.Warsaw?.ContextRoads, Network.Warsaw?.Waterways, Network.Warsaw?.Attribution, Network.Warsaw?.OsmTimestamp,
    city = Network.City is { } city ? new { city.Bounds, city.OsmTimestamp, city.RoadCount } : null,
    scenario = Network.Warsaw is null ? "sample" : "warsaw"
});
queries.MapGet("/validate-route", async (string segments, DateTimeOffset? at, RoutingQueries handlers,
    CancellationToken cancellationToken) => await handlers.ValidateRouteAsync(segments, at, cancellationToken));
queries.MapGet("/city-route", (string from, string to, string? mode, RoutingQueries handlers, CancellationToken cancellationToken) =>
    handlers.CityRouteAsync(from, to, mode, cancellationToken));
queries.MapGet("/distribution",
    (string node, DateTimeOffset? at, string? mode, string? destination, bool? accessible, RoutingQueries handlers,
        CancellationToken cancellationToken) =>
        handlers.DistributionAsync(node, at, mode, destination, accessible == true, cancellationToken));
queries.MapGet("/assistance-route",
    (string from, string pickup, RoutingQueries handlers, CancellationToken cancellationToken) =>
        handlers.AssistanceAsync(from, pickup, cancellationToken));
queries.MapGet("/draws/{commandId}",
    (string commandId, RoutingQueries handlers, CancellationToken cancellationToken) =>
        handlers.DrawStatusAsync(commandId, cancellationToken));

var commands = app.MapGroup("/commands");
commands.MapPost("/draw",
    (DrawCommand command, RoutingCommands handlers, CancellationToken cancellationToken) =>
        handlers.DrawAsync(command, cancellationToken));

app.Run();
