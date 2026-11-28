using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cassini.Gateway.Link;

/// <summary>A field command held by the store-and-forward queue. The body is resent unchanged on every attempt.</summary>
internal sealed record QueuedCommand(
    string EntryId, string Service, string Operation, string CommandId, string ContentType, string BodyHash,
    DateTimeOffset QueuedAt, string State, int Attempts, DateTimeOffset? LastAttemptAt = null,
    string? LastError = null, DateTimeOffset? DeliveredAt = null, string? DeliveredOver = null, int? Status = null)
{
    internal const string Queued = "Queued";
    internal const string Delivered = "Delivered";
    internal const string Rejected = "Rejected";

    [JsonIgnore] public bool Pending => State == Queued;
}

internal sealed record FieldCommand(string Service, string Operation, string ContentType, byte[] Body);

/// <summary>What the relay did with a field command.</summary>
internal abstract record RelayOutcome
{
    internal sealed record Forwarded(UpstreamResponse Response, LinkProfile Profile, TimeSpan Transit) : RelayOutcome;
    internal sealed record Queued(QueuedCommand Entry, LinkProfile Profile) : RelayOutcome;
    internal sealed record Stored(QueuedCommand Entry, UpstreamResponse Response) : RelayOutcome;
    internal sealed record Refused(int Status, string Code, string Detail) : RelayOutcome;
}

internal sealed record LinkStatus(
    LinkProfile Profile, DateTimeOffset ProfileChangedAt, int Pending, QueuedCommand[] Entries, LinkProfile[] Profiles);

/// <summary>
/// Simulates the field link and the field terminal's store-and-forward queue. While the profile is <c>down</c>,
/// or older commands still wait, new field commands are queued and answered with <c>202 Queued</c>, never with a
/// delivery confirmation. The forwarder delivers them in order with the same body, so the owner's
/// <c>commandId</c> receipts prevent duplicates.
/// </summary>
internal sealed class LinkSimulator
{
    internal const int MaxBodyBytes = 64 * 1024;
    internal const int MaxFinishedEntries = 100;
    internal const int MaxProfileReceipts = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly IUpstreamCommands upstream;
    private readonly TimeProvider clock;
    private readonly string? path;
    private readonly SemaphoreSlim work = new(0);
    private State state;

    private sealed record StoredEntry(QueuedCommand Entry, string Body, string? Result, string? ResultContentType);

    private sealed record State(string Profile, DateTimeOffset ProfileChangedAt, List<StoredEntry> Entries,
        Dictionary<string, string> ProfileReceipts, long Sequence);

    public LinkSimulator(IUpstreamCommands upstream, TimeProvider clock, string? dataDirectory)
    {
        this.upstream = upstream;
        this.clock = clock;
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
            path = Path.Combine(dataDirectory, "link-simulator.json");
        }

        state = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(path), Json)
                ?? throw new InvalidDataException("Invalid link simulator state.")
            : new(LinkProfiles.Terrestrial.Name, clock.GetUtcNow(), [], [], 0);
    }

    public LinkProfile Profile
    {
        get
        {
            lock (gate)
                return LinkProfiles.Find(state.Profile) ?? LinkProfiles.Terrestrial;
        }
    }

    public LinkStatus Status()
    {
        lock (gate)
        {
            var entries = state.Entries.Select(item => item.Entry).ToArray();
            return new(LinkProfiles.Find(state.Profile)!, state.ProfileChangedAt,
                entries.Count(entry => entry.Pending), entries, LinkProfiles.All);
        }
    }

    public RelayOutcome.Refused? SetProfile(string? commandId, string? name, out LinkStatus status)
    {
        status = null!;
        if (string.IsNullOrWhiteSpace(commandId) || commandId.Length > 128)
            return new(400, "CommandIdRequired", "Provide a command ID of at most 128 characters.");
        if (LinkProfiles.Find(name) is not { } profile)
            return new(400, "UnknownProfile", "Use terrestrial, iris2, govsatcom or down.");

        lock (gate)
        {
            if (state.ProfileReceipts.TryGetValue(commandId, out var previous))
            {
                if (previous != profile.Name)
                    return new(409, "CommandIdConflict", "This command ID was used for another profile.");
            }
            else
            {
                var receipts = new Dictionary<string, string>(state.ProfileReceipts) { [commandId] = profile.Name };
                if (receipts.Count > MaxProfileReceipts)
                    receipts.Remove(receipts.Keys.First());
                Save(state with
                {
                    Profile = profile.Name,
                    ProfileChangedAt = profile.Name == state.Profile ? state.ProfileChangedAt : clock.GetUtcNow(),
                    ProfileReceipts = receipts,
                });
            }
        }

        work.Release();
        status = Status();
        return null;
    }

    public async Task<RelayOutcome> SubmitAsync(FieldCommand command, CancellationToken cancellation)
    {
        if (command.Body.Length > MaxBodyBytes)
            return new RelayOutcome.Refused(413, "FieldCommandTooLarge", $"Field commands are limited to {MaxBodyBytes} bytes.");
        if (ReadCommandId(command.Body) is not { } commandId)
            return new RelayOutcome.Refused(400, "CommandIdRequired", "A field command needs a commandId to be queued safely.");

        var hash = Convert.ToHexString(SHA256.HashData(command.Body));
        LinkProfile profile;
        StoredEntry? known;
        lock (gate)
        {
            profile = LinkProfiles.Find(state.Profile)!;
            known = state.Entries.LastOrDefault(item => item.Entry.Service == command.Service &&
                item.Entry.Operation == command.Operation && item.Entry.CommandId == commandId);
            if (known is not null && known.Entry.BodyHash != hash)
                return new RelayOutcome.Refused(409, "CommandIdConflict", "This command ID was used for another request.");
            if (known is { Entry.Pending: true })
                return new RelayOutcome.Queued(known.Entry, profile);
            if (known is { Result: { } result } && !profile.Available)
                return new RelayOutcome.Stored(known.Entry, new(known.Entry.Status!.Value, known.ResultContentType,
                    Encoding.UTF8.GetBytes(result)));

            if (!profile.Available || state.Entries.Any(item => item.Entry.Pending))
                return new RelayOutcome.Queued(Enqueue(command, commandId, hash), profile);
        }

        var transit = profile.TransitTime(command.Body.Length);
        await Task.Delay(transit, clock, cancellation);
        var response = await upstream.SendAsync(command.Service, command.Operation, command.ContentType, command.Body,
            cancellation);
        if (response is null || response.Status >= 500)
        {
            lock (gate)
                return new RelayOutcome.Queued(Enqueue(command, commandId, hash, response is null
                    ? "Service unreachable." : $"Service answered {response.Status}."), profile);
        }

        if (known is null)
        {
            // Record the delivery so a retry while the link is down can replay the owner's answer.
            lock (gate)
            {
                var sequence = state.Sequence + 1;
                var entry = new QueuedCommand($"link-{sequence}", command.Service, command.Operation, commandId,
                    command.ContentType, hash, clock.GetUtcNow(), QueuedCommand.Queued, 1, clock.GetUtcNow());
                var body = Encoding.UTF8.GetString(command.Body);
                Save(state with
                {
                    Entries = Prune([.. state.Entries, Finish(entry, body, response, profile)]), Sequence = sequence,
                });
            }
        }
        return new RelayOutcome.Forwarded(response, profile, transit);
    }

    /// <summary>Attempts the oldest queued command. Returns false when nothing could be delivered now.</summary>
    public async Task<bool> DeliverNextAsync(CancellationToken cancellation)
    {
        StoredEntry? next;
        LinkProfile profile;
        lock (gate)
        {
            profile = LinkProfiles.Find(state.Profile)!;
            next = state.Entries.FirstOrDefault(item => item.Entry.Pending);
            if (next is null || !profile.Available)
                return false;
        }

        var body = Encoding.UTF8.GetBytes(next.Body);
        await Task.Delay(profile.TransitTime(body.Length), clock, cancellation);
        var response = await upstream.SendAsync(next.Entry.Service, next.Entry.Operation, next.Entry.ContentType, body,
            cancellation);
        var now = clock.GetUtcNow();
        lock (gate)
        {
            var index = state.Entries.FindIndex(item => item.Entry.EntryId == next.Entry.EntryId);
            if (index < 0 || !state.Entries[index].Entry.Pending)
                return true;

            var entry = state.Entries[index].Entry with { Attempts = next.Entry.Attempts + 1, LastAttemptAt = now };
            var delivered = response is { Status: < 500 };
            var stored = delivered
                ? Finish(entry, next.Body, response!, profile)
                : new StoredEntry(entry with
                {
                    LastError = response is null ? "Service unreachable." : $"Service answered {response.Status}.",
                }, next.Body, null, null);

            var entries = new List<StoredEntry>(state.Entries) { [index] = stored };
            Save(state with { Entries = Prune(entries) });
            return delivered;
        }
    }

    private StoredEntry Finish(QueuedCommand entry, string body, UpstreamResponse response, LinkProfile profile) =>
        new(entry with
        {
            State = response.Status < 400 ? QueuedCommand.Delivered : QueuedCommand.Rejected,
            DeliveredAt = clock.GetUtcNow(), DeliveredOver = profile.Name, Status = response.Status, LastError = null,
        }, body, Encoding.UTF8.GetString(response.Body), response.ContentType);

    /// <summary>Waits until a profile change or a newly queued command, or until the timeout passes.</summary>
    public Task WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellation) =>
        work.WaitAsync(timeout, cancellation);

    private QueuedCommand Enqueue(FieldCommand command, string commandId, string hash, string? lastError = null)
    {
        var sequence = state.Sequence + 1;
        var entry = new QueuedCommand($"link-{sequence}", command.Service, command.Operation, commandId,
            command.ContentType, hash, clock.GetUtcNow(), QueuedCommand.Queued, 0, LastError: lastError);
        var stored = new StoredEntry(entry, Encoding.UTF8.GetString(command.Body), null, null);
        Save(state with { Entries = Prune([.. state.Entries, stored]), Sequence = sequence });
        work.Release();
        return entry;
    }

    /// <summary>Keeps every pending command and the most recent finished ones.</summary>
    private static List<StoredEntry> Prune(List<StoredEntry> entries)
    {
        var excess = entries.Count(item => !item.Entry.Pending) - MaxFinishedEntries;
        if (excess <= 0)
            return entries;

        var kept = new List<StoredEntry>(entries.Count - excess);
        foreach (var item in entries)
        {
            if (!item.Entry.Pending && excess > 0)
                excess--;
            else
                kept.Add(item);
        }
        return kept;
    }

    private void Save(State next)
    {
        if (path is not null)
        {
            var temporaryPath = path + ".tmp";
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(next, Json));
            File.Move(temporaryPath, path, overwrite: true);
        }
        state = next;
    }

    private static string? ReadCommandId(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("commandId", out var id) &&
                   id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 128 } value
                ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
