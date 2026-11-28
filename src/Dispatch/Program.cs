using Cassini.Dispatch;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5084");

var dataDirectory = builder.Configuration["Demo:DataDirectory"];
var connectionString = builder.Configuration.GetConnectionString("Dispatch");
if (string.IsNullOrWhiteSpace(connectionString))
    builder.Services.AddSingleton<IDispatchPersistence>(_ => new FileDispatchPersistence(dataDirectory));
else
{
#if DISPATCH_POSTGRES
    builder.Services.AddSingleton<IDispatchPersistence>(_ => new PostgresDispatchPersistence(connectionString, dataDirectory));
#else
    throw new InvalidOperationException("Build Dispatch with EnableDispatchPostgres=true to use a PostgreSQL connection.");
#endif
}
builder.Services.AddSingleton<DispatchStore>();
builder.Services.AddSingleton(new UnitPositionPolicy(
    builder.Configuration["Integrations:Egnos:FixtureFile"] ?? Path.Combine(AppContext.BaseDirectory, "unit-positions.json"),
    builder.Configuration.GetValue("Integrations:Egnos:MaxHpl", 25d)));
builder.Services.AddSingleton<DispatchQueries>();
builder.Services.AddSingleton<DispatchCommands>();
builder.Services.AddSingleton<RouteReview>();
builder.Services.AddSingleton<UnitRanking>();
if (builder.Configuration.GetValue("Dispatch:AutoReplan", true))
{
    builder.Services.AddSingleton<ReplanSignal>();
    builder.Services.AddHostedService<AutomaticReplanning>();
    if (builder.Configuration.GetConnectionString("DispatchRabbitMq") is { Length: > 0 } rabbitMq)
    {
#if DISPATCH_RABBITMQ
        if (!Uri.TryCreate(rabbitMq, UriKind.Absolute, out var broker) || broker.Scheme is not ("amqp" or "amqps"))
            throw new InvalidOperationException("Configure DispatchRabbitMq with a valid amqp or amqps URI.");
        builder.Services.AddHostedService(services => new RabbitMqRouteNotifications(broker,
            services.GetRequiredService<ReplanSignal>(),
            services.GetRequiredService<ILogger<RabbitMqRouteNotifications>>()));
#else
        throw new InvalidOperationException("Build Dispatch with EnableDispatchRabbitMq=true to use RabbitMQ.");
#endif
    }
}
builder.Services.AddHttpClient<ManagementClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Management:BaseUrl"] ?? "http://127.0.0.1:5081");
    client.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddHttpClient<RoutingClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Routing:BaseUrl"] ?? "http://127.0.0.1:5083");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient<SituationClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Situation:BaseUrl"] ?? "http://127.0.0.1:5082");
    client.Timeout = TimeSpan.FromSeconds(3);
});

var app = builder.Build();
_ = app.Services.GetRequiredService<DispatchStore>();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (DispatchPersistenceException)
    {
        await DispatchStore.Error("StateUnavailable", "Dispatch could not access its state.", 503).ExecuteAsync(context);
    }
});
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (DispatchStore store) =>
{
    _ = store.Read();
    return Results.Ok(new { status = "ready" });
});

var queries = app.MapGroup("/queries");
queries.MapGet("/assignments", (DispatchQueries handler, CancellationToken cancellation) =>
    handler.GetAssignments(cancellation));
queries.MapGet("/unit-candidates", (string? requestId, UnitRanking ranking, CancellationToken cancellation) =>
    ranking.GetCandidates(requestId, cancellation));

var commands = app.MapGroup("/commands");
commands.MapPost("/request", (RequestAssistance command, DispatchCommands handler, CancellationToken cancellation) =>
    handler.RequestAssistance(command, cancellation));
commands.MapPost("/assign", (AssignUnit command, DispatchCommands handler, CancellationToken cancellation) =>
    handler.AssignUnit(command, cancellation));
commands.MapPost("/replan", (ReplanAssignment command, DispatchCommands handler, CancellationToken cancellation) =>
    handler.Replan(command, cancellation));
commands.MapPost("/progress", (ReportProgress command, DispatchCommands handler, CancellationToken cancellation) =>
    handler.ReportProgress(command, cancellation));

app.Run();
