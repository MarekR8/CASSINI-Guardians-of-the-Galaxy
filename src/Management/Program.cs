using Cassini.Management;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5081");

var ewssMode = builder.Configuration["Integrations:Ewss:Mode"] ?? "mock";
if (ewssMode != "mock")
    throw new InvalidOperationException(
        "Integrations:Ewss:Mode supports only 'mock': EWSS has no public broadcast interface.");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IManagementPersistence>(
    new FileManagementPersistence(builder.Configuration["Demo:DataDirectory"]));
builder.Services.AddSingleton<ManagementStore>();
builder.Services.AddSingleton<IEwssBroadcaster, MockEwssBroadcaster>();
builder.Services.AddHttpClient<ISituationPopulation, SituationPopulationClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Situation:BaseUrl"] ?? "http://127.0.0.1:5082");
    client.Timeout = TimeSpan.FromSeconds(2);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });
builder.Services.AddSingleton<IncidentQueries>();
builder.Services.AddSingleton<IncidentCommands>();
builder.Services.AddSingleton<EwssCommands>();

var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

var queries = app.MapGroup("/queries");
queries.MapGet("/incident", (IncidentQueries handler, CancellationToken cancellation) =>
    handler.GetIncident(cancellation));
queries.MapGet("/cases/{caseId}", (string caseId, IncidentQueries handler) =>
    handler.GetCase(caseId) is { } item
        ? Results.Ok(item)
        : Results.NotFound(new { code = "CaseNotFound", detail = "Unknown demo case." }));
queries.MapGet("/ewss-alerts", (ManagementStore store) => new { alerts = store.Read().Broadcasts });

var commands = app.MapGroup("/commands");
commands.MapPost("/alert", (RecordAlert command, IncidentCommands handler) => handler.RecordAlert(command));
commands.MapPost("/progress", (RecordProgress command, IncidentCommands handler) => handler.RecordProgress(command));
commands.MapPost("/ewss-alert", (BroadcastEwssAlert command, EwssCommands handler) => handler.Broadcast(command));

app.Run();
