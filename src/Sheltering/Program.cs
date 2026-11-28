using Cassini.Demo.SiteCatalog;
using Cassini.Sheltering.Allocation;
using Cassini.Sheltering.Catalog;
using Cassini.Sheltering.Infrastructure;
using Cassini.Sheltering.Reception;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5085");

var seedFile = builder.Configuration["Sheltering:SeedFile"] ?? Path.Combine(AppContext.BaseDirectory, "sample-sites.json");
var holdsFile = builder.Configuration["Sheltering:HoldsFile"];
var stateFile = builder.Configuration["Sheltering:StateFile"] ?? Path.Combine("artifacts", "sheltering", "catalog.json");
builder.Services.AddSingleton(new CatalogStore<Shelter>(stateFile,
    () => ShelterSeed.WithHolds(ShelterSeed.Read(seedFile), holdsFile), shelter => shelter.ShelterId, shelter => shelter.Version));
builder.Services.AddSingleton<AllocationStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AllocationCommands>();
builder.Services.AddScoped<AllocationQueries>();
builder.Services.AddScoped<ShelterCommands>();
builder.Services.AddScoped<ShelterQueries>();
builder.Services.AddScoped<ReceptionCommands>();
builder.Services.AddScoped<ReceptionQueries>();
builder.Services.AddHttpClient<SituationClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Situation:BaseUrl"] ?? "http://127.0.0.1:5082");
    client.Timeout = TimeSpan.FromSeconds(3);
}).ConfigurePrimaryHttpMessageHandler(LocalHandler);
builder.Services.AddHttpClient<RoutingClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Routing:BaseUrl"] ?? "http://127.0.0.1:5083");
    client.Timeout = TimeSpan.FromSeconds(5);
}).ConfigurePrimaryHttpMessageHandler(LocalHandler);
builder.Services.AddHttpClient<GroupingClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Management:BaseUrl"] ?? "http://127.0.0.1:5081");
    client.Timeout = TimeSpan.FromSeconds(3);
    if (builder.Configuration["Grouping:ReadToken"] is { Length: > 0 } token)
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
}).ConfigurePrimaryHttpMessageHandler(LocalHandler);

var app = builder.Build();
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
    CancellationToken cancellation) => handler.ConfigureShelter(command, cancellation));
app.MapGet("/queries/shelter-reception", (string? shelterId, string? incidentId, DateTimeOffset? at,
    ReceptionQueries handler) => handler.GetReception(shelterId, incidentId, at));
app.MapGet("/queries/shelter-reception-events", (string? shelterId, ReceptionQueries handler) =>
    handler.GetEvents(shelterId));
app.MapPost("/commands/confirm-shelter-admission", (ConfirmShelterAdmissionCommand command, ReceptionCommands handler) =>
    handler.ConfirmAdmission(command));
app.MapPost("/commands/confirm-shelter-departure", (ConfirmShelterDepartureCommand command, ReceptionCommands handler) =>
    handler.ConfirmDeparture(command));

// SH-T04 allocation (SH-W4): reads never mutate state; every write commits assignments and shelter holds together.
app.MapGet("/queries/shelter-assignment", (string? incidentId, string? caseId, string? legId, AllocationQueries handler) =>
    handler.GetAssignment(incidentId, caseId, legId));
app.MapGet("/queries/shelter-assignments", (string? incidentId, AllocationQueries handler) => handler.GetAssignments(incidentId));
app.MapGet("/queries/shelter-candidates", ([AsParameters] CandidateRequest request, AllocationQueries handler,
    CancellationToken cancellation) => handler.GetCandidates(request, cancellation));
app.MapGet("/queries/allocation-outbox", (long? after, AllocationQueries handler) => handler.GetOutbox(after));
app.MapPost("/commands/open-shelter-assignment", (OpenShelterAssignmentCommand command, AllocationCommands handler,
    CancellationToken cancellation) => handler.Open(command, cancellation));
app.MapPost("/commands/reserve-shelter-places", (ReserveShelterPlacesCommand command, AllocationCommands handler,
    CancellationToken cancellation) => handler.Reserve(command, cancellation));
app.MapPost("/commands/reassign-shelter-places", (ReassignShelterPlacesCommand command, AllocationCommands handler,
    CancellationToken cancellation) => handler.Reassign(command, cancellation));
app.MapPost("/commands/invalidate-shelter-assignment", (InvalidateShelterAssignmentCommand command,
    AllocationCommands handler, CancellationToken cancellation) => handler.Invalidate(command, cancellation));
app.MapPost("/commands/release-shelter-reservation", (ReleaseShelterReservationCommand command,
    AllocationCommands handler, CancellationToken cancellation) => handler.Release(command, cancellation));

app.Run();

static SocketsHttpHandler LocalHandler() => new()
{
    AllowAutoRedirect = false,
    UseProxy = false,
    UseCookies = false,
};
