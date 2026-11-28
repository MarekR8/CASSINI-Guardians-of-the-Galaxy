using Situation;
using Cassini.Demo;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5082");

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8 * 1024 * 1024);
var network = DemoNetwork.Load(builder.Configuration["Demo:Scenario"]);
var city = network is null ? null : CityNetwork.Load(builder.Configuration["Demo:CityDirectory"]);
builder.Services.AddSingleton(new SituationStore(builder.Configuration["Situation:StateFile"], network));
builder.Services.AddSingleton(new ReviewNetwork(network, city));
builder.Services.AddSingleton<SituationQueries>();
builder.Services.AddSingleton<SituationCommands>();

var app = builder.Build();
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
app.MapPost("/commands/closure", (ClosureCommand command, SituationCommands commands) => commands.SetClosure(command));
app.MapPost("/commands/import-copernicus", (ImportCopernicusCommand command, SituationCommands commands) =>
    commands.ImportCopernicus(command));
app.MapPost("/commands/import-flypix", (ImportReviewCommand command, SituationCommands commands) =>
    commands.ImportReview(command));
app.MapPost("/commands/review-decision", (ReviewDecisionCommand command, SituationCommands commands) =>
    commands.DecideReview(command));

app.Run();
