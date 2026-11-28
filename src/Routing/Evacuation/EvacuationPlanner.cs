using System.Diagnostics;

namespace Cassini.Routing.Evacuation;

internal sealed record EvacuationPlan(
    long Revision, string[] Closed, EvacuationModel Model, EvacuationResult Result, DateTimeOffset ComputedAt,
    double DurationSeconds);

/// <summary>
/// Keeps one evacuation plan per Situation revision. A plan is computed in the background when the revision or its
/// closures change; until it is ready, queries answer PlanCalculating instead of returning stale guidance.
/// </summary>
internal sealed class EvacuationPlanner(EvacuationCase input, ILogger<EvacuationPlanner> log)
{
    private readonly object gate = new();
    private EvacuationPlan? current;
    private long requested = -1;
    private string? failure;

    internal EvacuationCase Input => input;

    internal EvacuationPlan Require(SituationSnapshot snapshot)
    {
        lock (gate)
        {
            if (current is { } plan && plan.Revision == snapshot.Revision)
                return plan;
            Start(snapshot);
            if (failure is { } reason)
                throw new RoutingException(503, "PlanFailed", "Nie udało się przeliczyć planu ewakuacji. " + reason);
        }
        throw new RoutingException(503, "PlanCalculating",
            "Plan ewakuacji jest przeliczany dla bieżącej rewizji sytuacji. Spróbuj ponownie za chwilę.");
    }

    internal void Start(SituationSnapshot snapshot)
    {
        lock (gate)
        {
            if (current?.Revision == snapshot.Revision || requested == snapshot.Revision)
                return;
            requested = snapshot.Revision;
            failure = null;
        }
        var closed = snapshot.ClosedSegments.Order(StringComparer.Ordinal).ToArray();
        _ = Task.Run(() => Compute(snapshot.Revision, closed));
    }

    private void Compute(long revision, string[] closed)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var model = new EvacuationModel(input.Graph, input.People, input.Deadlines, input.Shelters, input.Params, closed);
            var result = model.Run();
            var plan = new EvacuationPlan(revision, closed, model, result, DateTimeOffset.UtcNow, watch.Elapsed.TotalSeconds);
            lock (gate)
            {
                if (requested == revision)
                    current = plan;
            }
            log.LogInformation("Evacuation plan for revision {Revision} ready in {Seconds:F1} s ({Closed} closures).",
                revision, plan.DurationSeconds, closed.Length);
        }
        catch (Exception exception)
        {
            log.LogError(exception, "Evacuation plan for revision {Revision} failed.", revision);
            lock (gate)
            {
                if (requested == revision)
                {
                    failure = exception.Message;
                    requested = -1;
                }
            }
        }
    }
}

/// <summary>Follows the Situation revision so that a new plan starts as soon as closures change.</summary>
internal sealed class EvacuationPlanRefresher(EvacuationPlanner planner, IServiceProvider services,
    ILogger<EvacuationPlanRefresher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var snapshot = await scope.ServiceProvider.GetRequiredService<SituationGateway>().ReadAsync(stoppingToken);
                planner.Start(snapshot);
            }
            catch (RoutingException exception)
            {
                log.LogDebug("Situation not available for planning: {Message}", exception.Message);
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
