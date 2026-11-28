using Situation;
using Situation.AssemblyPoints;
using Cassini.Demo;
using Cassini.Demo.SiteCatalog;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5082");

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8 * 1024 * 1024);
var scenario = builder.Configuration["Demo:Scenario"] is { Length: > 0 } configured ? configured : "sample";
var valencia = scenario == "valencia";
var directory = builder.Configuration["Demo:CityDirectory"];
if (scenario is not ("sample" or "valencia"))
    throw new InvalidDataException($"Unknown demo scenario '{scenario}'.");
var city = valencia ? CityNetwork.Load(directory, scenario) : null;
var population = new MockGhslEstimator(Path.Combine(AppContext.BaseDirectory, "population-estimate.json"));
builder.Services.AddSingleton(new SituationStore(builder.Configuration["Situation:StateFile"], population,
    scenario, valencia ? Path.Combine(directory!, "hazard-seed.json") : null));
builder.Services.AddSingleton(new ReviewNetwork(city));
builder.Services.AddSingleton<SituationQueries>();
builder.Services.AddSingleton<SituationCommands>();
var assemblyPointsSeed = scenario == "sample" ? builder.Configuration["AssemblyPoints:SeedFile"] : null;
var assemblyPointsEnabled = !string.IsNullOrWhiteSpace(assemblyPointsSeed);
var authenticateCatalog = false;
if (assemblyPointsEnabled)
{
    authenticateCatalog = SiteCatalogAccess.Configure(builder);
    builder.Services.AddAssemblyPoints(assemblyPointsSeed!,
        builder.Configuration["AssemblyPoints:StateFile"] ?? Path.Combine("artifacts", "sheltering", "assembly-points.json"),
        builder.Configuration.GetConnectionString("AssemblyPointCatalog"));
}
builder.Services.AddSingleton<ICopernicusSource, MockCopernicusSource>();
builder.Services.AddSingleton<IFlyPixSource, MockFlyPixSource>();
builder.Services.AddSingleton<ICitySource, MockCitySource>();
var integrationArea = IntegrationArea.ForScenario(scenario);

var app = builder.Build();
if (assemblyPointsEnabled)
    _ = app.Services.GetRequiredService<CatalogStore<AssemblyPoint>>();
if (authenticateCatalog)
    app.UseAuthentication();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (SituationError error)
    {
        await Results.Problem(statusCode: error.Status, title: error.Code, detail: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code }).ExecuteAsync(context);
    }
});

app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", (SituationQueries queries) => Results.Ok(new { status = "ok", queries.Snapshot().Revision }));
app.MapGet("/queries/snapshot", (SituationQueries queries) => queries.Snapshot());
app.MapGet("/queries/integration-copernicus", (ICopernicusSource source, CancellationToken cancelled) =>
    source.GetDelineation(integrationArea, cancelled));
app.MapGet("/queries/integration-sentinel-1", (ICopernicusSource source, CancellationToken cancelled) =>
    source.GetWaterMask(integrationArea, cancelled));
app.MapGet("/queries/integration-copernicus-dem", (ICopernicusSource source, CancellationToken cancelled) =>
    source.GetLowElevationArea(integrationArea, cancelled));
app.MapGet("/queries/integration-flypix", (IFlyPixSource source, CancellationToken cancelled) =>
    source.GetDetections(integrationArea, cancelled));
app.MapGet("/queries/city-data", (ICitySource source, CancellationToken cancelled) =>
    source.GetData(integrationArea, cancelled));
app.MapGet("/queries/city-map", (ICitySource source, CancellationToken cancelled) =>
    source.GetMap(integrationArea, cancelled));
app.MapGet("/queries/closed-roads", (SituationQueries queries) =>
{
    var snapshot = queries.Snapshot();
    return new { snapshot.Revision, roads = snapshot.ClosedSegments.Select(id => city?.Road(id))
        .OfType<CityRoad>().Select(road => new { road.Id, road.Geometry }) };
});
app.MapPost("/commands/closure", (ClosureCommand command, SituationCommands commands) => commands.SetClosure(command));
app.MapPost("/commands/import-copernicus", (ImportCopernicusCommand command, SituationCommands commands) =>
    commands.ImportCopernicus(command));
app.MapPost("/commands/import-flypix", (ImportReviewCommand command, SituationCommands commands) =>
    commands.ImportReview(command));
app.MapPost("/commands/review-decision", (ReviewDecisionCommand command, SituationCommands commands) =>
    commands.DecideReview(command));
if (assemblyPointsEnabled)
    app.MapAssemblyPoints();

app.Run();
