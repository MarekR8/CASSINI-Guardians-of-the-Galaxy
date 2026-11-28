using System.Text;
using Cassini.Gateway.Link;

var directory = Path.Combine(Path.GetTempPath(), $"cassini-gateway-{Guid.NewGuid():N}");
try
{
    var upstream = new FakeUpstream();
    var link = new LinkSimulator(upstream, TimeProvider.System, directory);
    Check(link.Profile.Name == "terrestrial", "The link starts on the terrestrial profile.");

    var forwarded = await link.SubmitAsync(Command("cmd-1"), default);
    Check(forwarded is RelayOutcome.Forwarded { Response.Status: 200 } && upstream.Received.Count == 1,
        "An available link forwards immediately.");

    Check(link.SetProfile("p-down", "down", out _) is null, "The profile can be switched to down.");
    Check(link.SetProfile("p-down", "iris2", out _) is { Code: "CommandIdConflict" },
        "A profile command ID cannot be reused for another profile.");
    Check(await link.SubmitAsync(Command("cmd-2"), default) is RelayOutcome.Queued &&
          await link.SubmitAsync(Command("cmd-3"), default) is RelayOutcome.Queued,
        "A down link queues field commands.");
    Check(await link.SubmitAsync(Command("cmd-1"), default) is RelayOutcome.Stored,
        "A retry of a delivered command returns the stored result while the link is down.");
    Check(await link.SubmitAsync(Command("cmd-2", "other"), default) is RelayOutcome.Refused { Status: 409 },
        "A queued command ID cannot carry another body.");
    Check(await link.SubmitAsync(new("management", "alert", "application/json", "{}"u8.ToArray()), default)
        is RelayOutcome.Refused { Code: "CommandIdRequired" }, "A field command needs a command ID.");
    Check(!await link.DeliverNextAsync(default), "Nothing is delivered while the link is down.");

    // The queue survives a Gateway restart.
    link = new LinkSimulator(upstream, TimeProvider.System, directory);
    Check(link.Status() is { Pending: 2, Profile.Name: "down" }, "Queued commands and the profile survive a restart.");

    link.SetProfile("p-iris2", "iris2", out _);
    upstream.Next = null;
    Check(!await link.DeliverNextAsync(default) && link.Status().Pending == 2,
        "An unreachable service leaves the command queued for retry.");
    upstream.Next = 503;
    Check(!await link.DeliverNextAsync(default), "A 5xx response is retried later.");
    upstream.Next = 200;
    Check(await link.DeliverNextAsync(default), "The oldest command is delivered first.");
    upstream.Next = 409;
    Check(await link.DeliverNextAsync(default), "An owner rejection still ends delivery.");
    var entries = link.Status().Entries;
    Check(entries.Single(entry => entry.CommandId == "cmd-2") is { State: "Delivered", Attempts: 3, DeliveredOver: "iris2" },
        "Delivery counts every attempt with the same body.");
    Check(entries.Single(entry => entry.CommandId == "cmd-3") is { State: "Rejected", Status: 409 },
        "A rejected command records the owner's answer.");
    Check(upstream.Received.Count(body => body.Contains("cmd-2")) == 3 &&
          upstream.Received.Where(body => body.Contains("cmd-2")).Distinct().Count() == 1,
        "Retries resend the identical body with the same command ID.");

    upstream.Next = null;
    Check(await link.SubmitAsync(Command("cmd-4"), default) is RelayOutcome.Queued { Entry.LastError: not null },
        "An unreachable service queues a command sent over an available link.");

    var transit = LinkProfiles.Govsatcom.TransitTime(2_000);
    Check(transit.TotalMilliseconds is >= 600 and < 700 && LinkProfiles.Down.TransitTime(10) == TimeSpan.Zero,
        "GOVSATCOM adds the GEO round trip and throughput delay.");

    Console.WriteLine("Gateway link simulator tests passed: queue, restart, retries, ordering, replay.");
}
finally
{
    if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
}

static FieldCommand Command(string id, string payload = "same") => new("management", "progress", "application/json",
    Encoding.UTF8.GetBytes($$"""{"commandId":"{{id}}","payload":"{{payload}}"}"""));

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
}

internal sealed class FakeUpstream : IUpstreamCommands
{
    internal int? Next { get; set; } = 200;
    internal List<string> Received { get; } = [];

    public Task<UpstreamResponse?> SendAsync(string service, string operation, string contentType, byte[] body,
        CancellationToken cancellation)
    {
        Received.Add(Encoding.UTF8.GetString(body));
        return Task.FromResult(Next is { } status
            ? new UpstreamResponse(status, "application/json", """{"ok":true}"""u8.ToArray())
            : null);
    }
}
