using Cassini.Routing;
using Cassini.Routing.Evacuation;
using Cassini.Demo;
using Cassini.Routing.Destinations;

var builder = WebApplication.CreateBuilder(args);
var scenario = builder.Configuration["Demo:Scenario"] is { Length: > 0 } configured ? configured : "sample";
var valencia = scenario == "valencia";
var evacuationEnabled = valencia && builder.Configuration.GetValue("Routing:EvacuationEnabled", true);
if (scenario is not ("sample" or "valencia"))
    throw new InvalidDataException($"Unknown demo scenario '{scenario}'.");
Network.Configure(valencia ? CityNetwork.Load(builder.Configuration["Demo:CityDirectory"], scenario) : null);
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
if (evacuationEnabled)
{
    // Task 03 evacuation model: inputs prepared by experiments/routing, plan recomputed per Situation revision.
    var evacuationDirectory = builder.Configuration["Routing:EvacuationDirectory"] is { Length: > 0 } directory
        ? directory : Path.Combine(AppContext.BaseDirectory, "evacuation");
    var shelterFile = builder.Configuration["Routing:EvacuationShelters"] is { Length: > 0 } file ? file : null;
    builder.Services.AddSingleton(_ => EvacuationInputs.ReadDirectory(evacuationDirectory, shelterFile));
    builder.Services.AddSingleton<EvacuationPlanner>();
    builder.Services.AddHostedService<EvacuationPlanRefresher>();
    builder.Services.AddTransient<EvacuationRouting>();
}
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
            client.BaseAddress = new Uri((ors["BaseUrl"] ?? "https://api.heigit.org/openrouteservice").TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(1.5);
        }).AddTypedClient<IRouteProvider>(client => new OrsRouteProvider(client, apiKey))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        break;
    default:
        throw new InvalidOperationException($"Unknown Integrations:Ors:Mode '{providerMode}'.");
}
if (providerMode != "graph")
    builder.Services.AddTransient<AssistanceRouteProvider>();

var destinationsEnabled = !valencia &&
    builder.Configuration["Sheltering:BaseUrl"] is { Length: > 0 };
if (destinationsEnabled)
{
    builder.Services.AddSingleton(new DestinationSources(
        new Uri((builder.Configuration["Situation:BaseUrl"] ?? "http://127.0.0.1:5082").TrimEnd('/') + "/"),
        new Uri(builder.Configuration["Sheltering:BaseUrl"]!.TrimEnd('/') + "/"),
        new Uri((builder.Configuration["Management:BaseUrl"] ?? "http://127.0.0.1:5081").TrimEnd('/') + "/")));
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(DestinationEvidenceFixtures.Load(builder.Configuration["Routing:DestinationEvidenceFile"]));
    builder.Services.AddTransient<DestinationRouteQueries>();
    builder.Services.AddHttpClient<DestinationQueries>(client => client.Timeout = TimeSpan.FromSeconds(4))
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
        });
}

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
if (destinationsEnabled)
{
    queries.MapGet("/destinations", (string? incidentId, DateTimeOffset? at, DestinationQueries handler,
        CancellationToken cancellation) => handler.GetDestinations(incidentId, at, cancellation));
    queries.MapGet("/destination-route", ([AsParameters] DestinationRouteRequest travel, string? destinationKind,
        string? destinationId, string? recordedRouteId, string? expectedEvidenceId, string? transportId, DestinationRouteQueries handler,
        CancellationToken cancellation) =>
        handler.GetRoute(travel, destinationKind, destinationId, recordedRouteId, expectedEvidenceId, transportId, cancellation));
    queries.MapGet("/first-leg", ([AsParameters] DestinationRouteRequest travel, string? reservationId,
        string? transportId, DestinationRouteQueries handler, CancellationToken cancellation) =>
        handler.GetFirstLeg(travel, reservationId, transportId, cancellation));
}
queries.MapGet("/network", () => new {
    Network.Nodes, Network.Segments, Network.Exits,
    city = Network.City is { } city ? new { city.Bounds, city.OsmTimestamp, city.RoadCount, city.ZoneCounts } : null,
    scenario
});
queries.MapGet("/city-node/{id:long}", (long id) => Network.City?.Node(id) is { } node
    ? Results.Ok(node) : Results.NotFound());
queries.MapGet("/city-edge/{id}", (string id) => Network.City?.Road(id) is { } road
    ? Results.Ok(road) : Results.NotFound());
queries.MapGet("/validate-route", async (string segments, DateTimeOffset? at, RoutingQueries handlers,
    CancellationToken cancellationToken) => await handlers.ValidateRouteAsync(segments, at, cancellationToken));
queries.MapGet("/city-route", (string from, string to, string? mode, RoutingQueries handlers, CancellationToken cancellationToken) =>
    handlers.CityRouteAsync(from, to, mode, cancellationToken));
if (!valencia)
{
    queries.MapGet("/distribution",
        (string node, DateTimeOffset? at, string? mode, string? destination, bool? accessible, RoutingQueries handlers,
            CancellationToken cancellationToken) =>
            handlers.DistributionAsync(node, at, mode, destination, accessible == true, cancellationToken));
    queries.MapGet("/assistance-route",
        (string from, string pickup, RoutingQueries handlers, CancellationToken cancellationToken) =>
            handlers.AssistanceAsync(from, pickup, cancellationToken));
queries.MapGet("/assistance-restrictions",
    (long since, RoutingQueries handlers, CancellationToken cancellationToken) =>
        handlers.AssistanceRestrictionsAsync(since, cancellationToken));
    queries.MapGet("/draws/{commandId}",
        (string commandId, RoutingQueries handlers, CancellationToken cancellationToken) =>
            handlers.DrawStatusAsync(commandId, cancellationToken));

    var commands = app.MapGroup("/commands");
    commands.MapPost("/draw",
        (DrawCommand command, RoutingCommands handlers, CancellationToken cancellationToken) =>
            handlers.DrawAsync(command, cancellationToken));
}
else if (evacuationEnabled)
{
    queries.MapGet("/evacuation-plan", (EvacuationRouting handlers, CancellationToken cancellationToken) =>
        handlers.PlanAsync(cancellationToken));
    queries.MapGet("/distribution",
        (string node, string? profile, EvacuationRouting handlers, CancellationToken cancellationToken) =>
            handlers.DistributionAsync(node, profile, cancellationToken));
    queries.MapGet("/draws/{commandId}",
        (string commandId, RoutingQueries handlers, CancellationToken cancellationToken) =>
            handlers.DrawStatusAsync(commandId, cancellationToken));
    app.MapGroup("/commands").MapPost("/draw",
        (DrawCommand command, EvacuationRouting handlers, CancellationToken cancellationToken) =>
            handlers.DrawAsync(command, cancellationToken));
}

app.Run();
