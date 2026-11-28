namespace Cassini.Gateway.Link;

internal sealed record ChangeLinkProfile(string? CommandId, string? Profile);

/// <summary>Drains the store-and-forward queue whenever the link is available.</summary>
internal sealed class LinkForwarder(LinkSimulator link) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(8);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromMilliseconds(250);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await link.DeliverNextAsync(stoppingToken))
                {
                    backoff = TimeSpan.FromMilliseconds(250);
                    continue;
                }

                var pending = link.Status().Pending > 0 && link.Profile.Available;
                await link.WaitForWorkAsync(pending ? backoff : Idle, stoppingToken);
                if (pending)
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}

internal static class LinkEndpoints
{
    /// <summary>Requests with this header travel over the simulated field link; others are local to the coordinator.</summary>
    internal const string Header = "X-Cassini-Link";
    internal const string FieldValue = "field";

    internal static bool IsFieldRequest(HttpContext context) =>
        string.Equals(context.Request.Headers[Header], FieldValue, StringComparison.OrdinalIgnoreCase);

    internal static void MapLinkSimulator(this WebApplication app)
    {
        app.MapGet("/api/link/queries/link-status", (LinkSimulator link) => link.Status());
        app.MapPost("/api/link/commands/link-profile", (ChangeLinkProfile command, LinkSimulator link) =>
            link.SetProfile(command.CommandId, command.Profile, out var status) is { } refused
                ? Results.Json(new { code = refused.Code, detail = refused.Detail }, statusCode: refused.Status)
                : Results.Ok(status));
    }

    internal static async Task RelayFieldCommand(HttpContext context, LinkSimulator link, string service, string operation)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        var outcome = await link.SubmitAsync(
            new FieldCommand(service, operation, context.Request.ContentType!, buffer.ToArray()), context.RequestAborted);

        context.Response.Headers[Header] = link.Profile.Name;
        switch (outcome)
        {
            case RelayOutcome.Forwarded forwarded:
                context.Response.Headers["X-Cassini-Link-Transit-Ms"] =
                    ((int)forwarded.Transit.TotalMilliseconds).ToString();
                await Write(context, forwarded.Response);
                break;
            case RelayOutcome.Stored stored:
                context.Response.Headers["X-Cassini-Link-Replay"] = stored.Entry.EntryId;
                await Write(context, stored.Response);
                break;
            case RelayOutcome.Queued queued:
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                await context.Response.WriteAsJsonAsync(new
                {
                    status = "Queued",
                    detail = "Held for delivery over the field link. Delivery is not confirmed yet.",
                    entry = queued.Entry,
                    profile = queued.Profile.Name,
                }, context.RequestAborted);
                break;
            case RelayOutcome.Refused refused:
                context.Response.StatusCode = refused.Status;
                await context.Response.WriteAsJsonAsync(new { code = refused.Code, detail = refused.Detail },
                    context.RequestAborted);
                break;
        }
    }

    private static async Task Write(HttpContext context, UpstreamResponse response)
    {
        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }
}
