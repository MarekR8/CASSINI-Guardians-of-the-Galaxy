using Cassini.Demo.SiteCatalog;
using Cassini.Messaging;
#if MESSAGING_RABBITMQ
using Cassini.Messaging.RabbitMq;
#endif
using Cassini.Sheltering.Allocation;
using Cassini.Sheltering.Catalog;
using Cassini.Sheltering.Infrastructure;
using Cassini.Sheltering.Publication;
using Cassini.Sheltering.Reception;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5085");

var seedFile = builder.Configuration["Sheltering:SeedFile"] ?? Path.Combine(AppContext.BaseDirectory, "sample-sites.json");
var holdsFile = builder.Configuration["Sheltering:HoldsFile"];
var stateFile = builder.Configuration["Sheltering:StateFile"] ?? Path.Combine("artifacts", "sheltering", "catalog.json");
Shelter[] Seed() => ShelterSeed.WithHolds(ShelterSeed.Read(seedFile), holdsFile);
static string ShelterKey(Shelter shelter) => shelter.ShelterId;
static int ShelterVersion(Shelter shelter) => shelter.Version;
var connectionString = builder.Configuration.GetConnectionString("Sheltering");
if (string.IsNullOrWhiteSpace(connectionString))
    builder.Services.AddSingleton(new CatalogStore<Shelter>(stateFile, Seed, ShelterKey, ShelterVersion));
else
{
#if SHELTERING_POSTGRES
    builder.Services.AddSingleton(new CatalogStore<Shelter>(new PostgresShelteringPersistence(connectionString,
        () => CatalogStore<Shelter>.Initial(Seed, ShelterKey)), ShelterKey, ShelterVersion));
#else
    throw new InvalidOperationException("Build Sheltering with EnableShelteringPostgres=true to use a PostgreSQL connection.");
#endif
}
builder.Services.AddSingleton<AllocationStore>();
builder.Services.AddSingleton<IOutboxStore>(services => services.GetRequiredService<AllocationStore>());
builder.Services.AddScoped<InstructionWorkflow>();
// SH-T06 Stage B: with Integrations:Bus:Mode=live both outboxes go to RabbitMQ and the instruction board consumes events.
#if MESSAGING_RABBITMQ
var liveBus = builder.Services.AddLiveEventBus(builder.Configuration, uri => new RabbitMqEventBus(
    new RabbitMqOptions(uri, "sheltering") { RequireRoutingFor = new HashSet<string> { EventTypes.ShelterAssignmentChanged } }));
#else
var liveBus = builder.Services.AddLiveEventBus(builder.Configuration, null);
#endif
if (liveBus)
{
    builder.Services.AddSingleton<IInstructionAuthority>(services => new OwnerInstructionAuthority(
        services.GetRequiredService<AllocationStore>(), services.GetRequiredService<RoutingClient>));
    builder.Services.AddSingleton<InstructionBoard>();
    builder.Services.AddSingleton<IEventConsumer>(services => services.GetRequiredService<InstructionBoard>());
    builder.Services.AddHostedService<EventSubscription>();
}
builder.Services.AddSingleton(new OperatorDirectory(builder.Configuration["Sheltering:OperatorsFile"]));
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
// SH-T05 reception. With Sheltering:OperatorsFile, callers need a bearer token and act only within their scope.
app.MapGet("/queries/shelter-reception", (string? shelterId, string? incidentId, DateTimeOffset? at,
    ReceptionQueries handler, OperatorDirectory operators, HttpContext context) =>
    handler.GetReception(shelterId, incidentId, at, operators.Authenticate(context)));
app.MapGet("/queries/shelter-reception-events", (string? shelterId, ReceptionQueries handler,
    OperatorDirectory operators, HttpContext context) => handler.GetEvents(shelterId, operators.Authenticate(context)));
app.MapPost("/commands/confirm-shelter-admission", (ConfirmShelterAdmissionCommand command, ReceptionCommands handler,
    OperatorDirectory operators, HttpContext context) => handler.ConfirmAdmission(command, operators.Authenticate(context)));
app.MapPost("/commands/confirm-shelter-departure", (ConfirmShelterDepartureCommand command, ReceptionCommands handler,
    OperatorDirectory operators, HttpContext context) => handler.ConfirmDeparture(command, operators.Authenticate(context)));

// SH-T04 allocation (SH-W4): reads never mutate state; every write commits assignments and shelter holds together.
app.MapGet("/queries/shelter-assignment", (string? incidentId, string? caseId, string? legId, AllocationQueries handler) =>
    handler.GetAssignment(incidentId, caseId, legId));
app.MapGet("/queries/shelter-assignments", (string? incidentId, AllocationQueries handler) => handler.GetAssignments(incidentId));
app.MapGet("/queries/shelter-candidates", ([AsParameters] CandidateRequest request, AllocationQueries handler,
    CancellationToken cancellation) => handler.GetCandidates(request, cancellation));
app.MapGet("/queries/allocation-outbox", (long? after, AllocationQueries handler) => handler.GetOutbox(after));
app.MapGet("/queries/allocation-evidence", (string? incidentId, string? caseId, string? legId, AllocationQueries handler) =>
    handler.GetEvidence(incidentId, caseId, legId));
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
// SH-T06 publication: a committed booking is published through Routing as a separate, retryable step.
app.MapGet("/queries/shelter-instruction", (string? incidentId, string? caseId, string? legId, InstructionWorkflow handler,
    CancellationToken cancellation) => handler.Get(incidentId, caseId, legId, cancellation));
app.MapPost("/commands/reconcile-shelter-instruction", (ReconcileShelterInstructionCommand command,
    InstructionWorkflow handler, CancellationToken cancellation) => handler.Reconcile(command, cancellation));
if (liveBus)
    app.MapGet("/queries/shelter-instruction-board", (string? incidentId, InstructionBoard board, AllocationStore store,
        CancellationToken cancellation) => board.Read(incidentId, store.Read().State.Assignments.Values
            .Where(item => item.IncidentId == incidentId).Select(item => item.Ref.Key), cancellation));

app.Run();

static SocketsHttpHandler LocalHandler() => new()
{
    AllowAutoRedirect = false,
    UseProxy = false,
    UseCookies = false,
};
