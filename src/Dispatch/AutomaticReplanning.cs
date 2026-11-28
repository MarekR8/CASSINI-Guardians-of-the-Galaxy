namespace Cassini.Dispatch;

internal sealed class ReplanSignal
{
    private readonly Lock gate = new();
    private readonly SemaphoreSlim pending = new(0, 1);

    internal void Notify()
    {
        lock (gate)
        {
            if (pending.CurrentCount == 0)
                pending.Release();
        }
    }

    internal Task WaitAsync(TimeSpan interval, CancellationToken cancellation) =>
        pending.WaitAsync(interval, cancellation);
}

internal sealed class AutomaticReplanning(DispatchStore store, RouteReview review, DispatchCommands commands,
    ReplanSignal signal, IConfiguration configuration, ILogger<AutomaticReplanning> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("Dispatch:AutoReplanIntervalSeconds", 5), 1, 3600));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReviewAssignments(stoppingToken);
            }
            catch (DispatchPersistenceException)
            {
                logger.LogWarning("Dispatch state is unavailable. Automatic route review will retry.");
            }
            await signal.WaitAsync(interval, stoppingToken);
        }
    }

    private async Task ReviewAssignments(CancellationToken cancellation)
    {
        var active = store.Read().Assignments.Where(assignment => assignment.Active).ToArray();
        if (active.Length == 0)
            return;

        var restrictions = await review.ReadAsync(active.Min(assignment => assignment.Route.Revision), cancellation);
        foreach (var assignment in active)
        {
            var status = RouteReview.Assess(assignment, restrictions);
            if (status.State is not ("Blocked" or "ReviewRequired"))
                continue;

            var commandId = FormattableString.Invariant($"auto-replan-{assignment.AssignmentId}-{assignment.Version}");
            await commands.Replan(new(commandId, assignment.AssignmentId, assignment.Version), cancellation,
                onlyWithdrawn: true);
        }
    }
}
