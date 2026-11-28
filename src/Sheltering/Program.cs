using Cassini.Demo.SiteCatalog;
using Cassini.Sheltering.Catalog;
using Cassini.Sheltering.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5085");

var seedFile = builder.Configuration["Sheltering:SeedFile"] ?? Path.Combine(AppContext.BaseDirectory, "sample-sites.json");
var stateFile = builder.Configuration["Sheltering:StateFile"] ?? Path.Combine("artifacts", "sheltering", "catalog.json");
var authenticateCatalog = SiteCatalogAccess.Configure(builder);
var incidentId = builder.Configuration["Sheltering:IncidentId"] ?? (authenticateCatalog ? null : "incident-42");
if (!SiteRules.IsId(incidentId))
    throw new InvalidOperationException("Configure Sheltering:IncidentId for the shelter catalog.");
builder.Services.AddSingleton(_ => new CatalogStore<Shelter>("sheltering", stateFile,
    builder.Configuration.GetConnectionString("ShelterCatalog"),
    () => ShelterSeed.Read(seedFile), shelter => shelter.ShelterId, shelter => shelter.Version));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ShelterCommands>();
builder.Services.AddScoped<ShelterQueries>();
builder.Services.AddHttpClient<SituationClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Situation:BaseUrl"] ?? "http://127.0.0.1:5082");
    client.Timeout = TimeSpan.FromSeconds(3);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseProxy = false,
    UseCookies = false,
});

var app = builder.Build();
_ = app.Services.GetRequiredService<CatalogStore<Shelter>>();
if (authenticateCatalog)
    app.UseAuthentication();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (SiteCatalogError error)
    {
        await Results.Problem(statusCode: error.Status, title: error.Code, detail: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code }).ExecuteAsync(context);
    }
});

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (CatalogStore<Shelter> catalog) =>
{
    var (revision, _) = catalog.Read();
    return Results.Ok(new { status = "ready", revision });
});
app.MapGet("/queries/shelter-availability", (DateTimeOffset? at, bool? eligibleOnly, ShelterQueries handler,
    CancellationToken cancellation) => handler.GetShelterAvailability(at, eligibleOnly ?? false, cancellation));
app.MapPost("/commands/configure-shelter", (ConfigureShelterCommand command, ShelterCommands handler,
    SiteCatalogAccess access, HttpContext context, CancellationToken cancellation) =>
    handler.ConfigureShelter(command, access.GetActor(context, incidentId, command.ShelterId),
        incidentId!, cancellation));

app.Run();
