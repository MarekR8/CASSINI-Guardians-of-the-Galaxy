using Cassini.Management;
using Cassini.Management.Grouping;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5081");

builder.Services.AddSingleton(new ManagementStore(builder.Configuration["Demo:DataDirectory"]));
builder.Services.AddSingleton<IncidentQueries>();
builder.Services.AddSingleton<IncidentCommands>();

var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

var queries = app.MapGroup("/queries");
queries.MapGet("/incident", (IncidentQueries handler) => handler.GetIncident());
queries.MapGet("/cases/{caseId}", (string caseId, IncidentQueries handler) =>
    handler.GetCase(caseId) is { } item
        ? Results.Ok(item)
        : Results.NotFound(new { code = "CaseNotFound", detail = "Unknown demo case." }));

var commands = app.MapGroup("/commands");
commands.MapPost("/alert", (RecordAlert command, IncidentCommands handler) => handler.RecordAlert(command));
commands.MapPost("/progress", (RecordProgress command, IncidentCommands handler) => handler.RecordProgress(command));

if (app.Configuration["Grouping:EvidenceFile"] is { Length: > 0 } evidenceFile)
    app.MapGrouping(GroupingEvidence.Load(evidenceFile));

app.Run();
