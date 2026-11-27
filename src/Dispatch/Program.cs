using Cassini.Dispatch;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5084");

builder.Services.AddSingleton(new DispatchStore(builder.Configuration["Demo:DataDirectory"]));
builder.Services.AddSingleton<DispatchQueries>();
builder.Services.AddSingleton<DispatchCommands>();
builder.Services.AddHttpClient<RoutingClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Routing:BaseUrl"] ?? "http://127.0.0.1:5083");
    client.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddHttpClient<SituationClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Situation:BaseUrl"] ?? "http://127.0.0.1:5082");
    client.Timeout = TimeSpan.FromSeconds(3);
});

var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));

var queries = app.MapGroup("/queries");
queries.MapGet("/assignments", (DispatchQueries handler, CancellationToken cancellation) =>
    handler.GetAssignments(cancellation));

var commands = app.MapGroup("/commands");
commands.MapPost("/assign", (AssignUnit command, DispatchCommands handler, CancellationToken cancellation) =>
    handler.AssignUnit(command, cancellation));
commands.MapPost("/replan", (ReplanAssignment command, DispatchCommands handler, CancellationToken cancellation) =>
    handler.Replan(command, cancellation));
commands.MapPost("/progress", (ReportProgress command, DispatchCommands handler) => handler.ReportProgress(command));

app.Run();
