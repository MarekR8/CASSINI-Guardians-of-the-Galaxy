using System.Security.Cryptography;
using System.Text.Json;

namespace Cassini.Demo.SiteCatalog;

internal sealed record CatalogWrite<T>(int Revision, T Item);
internal sealed record CatalogReceipt<T>(string Fingerprint, CatalogWrite<T> Result, CatalogActor? Actor = null);
internal sealed record CatalogOutcome(string Fingerprint, JsonElement Result);
internal sealed record CatalogActor(string Issuer, string Subject, string Role)
{
    internal static CatalogActor Demo { get; } = new("cassini-offline", "exercise", "demo");
    internal bool CanConfigureSite => Role is "coordinator" or "demo";
}
internal sealed record CatalogAudit<T>(int CatalogRevision, string CommandId, CatalogActor Actor, DateTimeOffset At,
    string SubjectId, int SubjectVersion, T? Before, T After);
internal sealed record CatalogOutboxEntry(string RoutingKey, CatalogEvent Event, DateTimeOffset? PublishedAt = null);
internal sealed record CatalogEvent(Guid EventId, string Type, string Producer, string SubjectId, int SubjectVersion,
    DateTimeOffset OccurredAt, string CorrelationId, string CausationId, JsonElement Payload);
internal sealed record SiteChange(string Type, string RoutingKey, string SubjectId, object Payload);

/// <summary>Everything one catalog commits at once. Aux is an optional owner-defined document (see Mutate).</summary>
internal sealed record CatalogState<T>(
    int Revision, Dictionary<string, T> Items, Dictionary<string, CatalogReceipt<T>> Receipts,
    Dictionary<string, CatalogOutcome>? Outcomes = null, JsonElement? Aux = null,
    CatalogAudit<T>[]? Audit = null, CatalogOutboxEntry[]? Outbox = null);

/// <summary>
/// Where a catalog keeps its state. <c>Update</c> runs <c>apply</c> on the current committed state and commits the
/// returned state atomically; a null state or an exception commits nothing.
/// </summary>
internal interface ICatalogPersistence<T> where T : class
{
    CatalogState<T> Read();
    TResult Update<TResult>(Func<CatalogState<T>, (CatalogState<T>? Next, TResult Result)> apply);
}

/// <summary>Single-process state in memory, saved to a JSON file with an atomic replace.</summary>
internal sealed class FileCatalogPersistence<T> : ICatalogPersistence<T> where T : class
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string? path;
    private CatalogState<T> state;

    internal FileCatalogPersistence(string? path, Func<CatalogState<T>> initial)
    {
        this.path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        state = this.path is not null && File.Exists(this.path)
            ? JsonSerializer.Deserialize<CatalogState<T>>(File.ReadAllBytes(this.path), Json)
                ?? throw new InvalidDataException("The site catalog is empty.")
            : initial();
    }

    public CatalogState<T> Read()
    {
        lock (gate)
            return state;
    }

    public TResult Update<TResult>(Func<CatalogState<T>, (CatalogState<T>? Next, TResult Result)> apply)
    {
        lock (gate)
        {
            var (next, result) = apply(state);
            if (next is not null)
            {
                if (path is not null)
                    Persist(path, next);
                state = next;
            }
            return result;
        }
    }

    private static void Persist(string path, CatalogState<T> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, next, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}

// The two owner services each instantiate this store with their own domain type and persistence.
internal sealed partial class CatalogStore<T> : IDisposable where T : class
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ICatalogPersistence<T> persistence;
    private readonly Func<T, string> key;
    private readonly Func<T, int> version;
    private readonly string? owner;

    internal CatalogStore(string? path, Func<T[]> seed, Func<T, string> key, Func<T, int> version)
        : this(new FileCatalogPersistence<T>(path, () => Initial(seed, key)), key, version)
    {
    }

    internal CatalogStore(ICatalogPersistence<T> persistence, Func<T, string> key, Func<T, int> version,
        string? owner = null)
    {
        this.persistence = persistence;
        this.key = key;
        this.version = version;
        this.owner = owner;
    }

    internal static CatalogState<T> Initial(Func<T[]> seed, Func<T, string> key) =>
        new(0, seed().ToDictionary(key, StringComparer.Ordinal), new(StringComparer.Ordinal));

    internal (int Revision, T[] Items) Read()
    {
        var state = persistence.Read();
        return (state.Revision, state.Items.Values.ToArray());
    }

    internal CatalogOutboxEntry[] ReadOutbox() => persistence.Read().Outbox ?? [];

    internal string[] MarkCatalogPublished(IReadOnlyCollection<string> eventIds, DateTimeOffset at) =>
        persistence.Update<string[]>(state =>
        {
            var otherIds = eventIds.ToHashSet(StringComparer.Ordinal);
            var changed = false;
            var confirmed = (state.Outbox ?? []).Select(entry =>
            {
                if (!otherIds.Remove(entry.Event.EventId.ToString("D")) || entry.PublishedAt is not null)
                    return entry;
                changed = true;
                return entry with { PublishedAt = at };
            }).ToArray();
            return (changed ? state with { Outbox = confirmed } : null, otherIds.ToArray());
        });

    internal CatalogWrite<T> Save<TCommand>(string? commandId, int? expectedVersion, string key,
        TCommand command, CatalogActor actor, DateTimeOffset at, Func<T?, int, T> change,
        Func<T?, T, SiteChange> describe)
    {
        if (!SiteRules.IsId(commandId) || expectedVersion is null or < 0)
            throw new SiteCatalogError(400, "InvalidCommand", "Specify a commandId and a non-negative expectedVersion.");

        var fingerprint = Fingerprint(command);
        return persistence.Update<CatalogWrite<T>>(state =>
        {
            if (state.Outcomes?.ContainsKey(commandId!) == true)
                throw Conflict();
            if (state.Receipts.TryGetValue(commandId!, out var receipt))
            {
                if ((receipt.Actor ?? CatalogActor.Demo) != actor)
                    throw new SiteCatalogError(403, "SiteScopeForbidden", "The command receipt belongs to another actor.");
                return (null, receipt.Fingerprint == fingerprint ? receipt.Result : throw Conflict());
            }

            var previous = state.Items.GetValueOrDefault(key);
            var currentVersion = previous is null ? 0 : version(previous);
            if (expectedVersion != currentVersion)
                throw new SiteCatalogError(409, "VersionConflict", "Read the current site before updating it.");

            var item = change(previous, checked(currentVersion + 1));
            var result = new CatalogWrite<T>(checked(state.Revision + 1), item);
            var fact = describe(previous, item);
            var audit = new CatalogAudit<T>(result.Revision, commandId!, actor, at, fact.SubjectId, version(item), previous, item);
            var message = new CatalogEvent(Guid.NewGuid(), fact.Type,
                owner ?? throw new InvalidOperationException("A site catalog event requires its owner."),
                fact.SubjectId, version(item), at, commandId!, commandId!, JsonSerializer.SerializeToElement(fact.Payload, Json));
            return (state with
            {
                Revision = result.Revision,
                Items = WithItems(state.Items, [item]),
                Receipts = new Dictionary<string, CatalogReceipt<T>>(state.Receipts, StringComparer.Ordinal)
                {
                    [commandId!] = new(fingerprint, result, actor),
                },
                Audit = [.. state.Audit ?? [], audit],
                Outbox = [.. state.Outbox ?? [], new(fact.RoutingKey, message)],
            }, result);
        });
    }

    /// <summary>
    /// Apply one command that may change several items atomically. The callback sees the current items under the
    /// store lock, checks versions itself and returns the changed items with their new versions. Replaying the
    /// commandId returns the stored result; an empty change records the receipt without a new revision.
    /// </summary>
    internal TResult Change<TCommand, TResult>(string? commandId, TCommand command,
        Func<IReadOnlyDictionary<string, T>, int, (T[] Changed, TResult Result)> change)
    {
        if (!SiteRules.IsId(commandId))
            throw new SiteCatalogError(400, "InvalidCommand", "Specify a commandId.");

        var fingerprint = Fingerprint(command);
        return persistence.Update<TResult>(state =>
        {
            if (state.Receipts.ContainsKey(commandId!))
                throw Conflict();
            if (state.Outcomes?.TryGetValue(commandId!, out var outcome) == true)
                return (null, outcome.Fingerprint == fingerprint ? outcome.Result.Deserialize<TResult>(Json)! : throw Conflict());

            var (changed, result) = change(state.Items, checked(state.Revision + 1));
            return (state with
            {
                Revision = changed.Length > 0 ? state.Revision + 1 : state.Revision,
                Items = WithItems(state.Items, changed),
                Outcomes = new Dictionary<string, CatalogOutcome>(state.Outcomes ?? new(), StringComparer.Ordinal)
                {
                    [commandId!] = new(fingerprint, JsonSerializer.SerializeToElement(result, Json)),
                },
            }, result);
        });
    }

    /// <summary>Read the items and the auxiliary document from one committed state.</summary>
    internal (int Revision, IReadOnlyDictionary<string, T> Items, TAux? Aux) ReadWith<TAux>() where TAux : class
    {
        var state = persistence.Read();
        return (state.Revision, state.Items, state.Aux?.Deserialize<TAux>(Json));
    }

    /// <summary>
    /// Change items and the auxiliary document in one atomic commit. The callback owns its idempotency and version
    /// checks (kept in the auxiliary document). The catalog revision is unchanged: it tracks configuration and reports,
    /// while the owner of the auxiliary document keeps its own revision.
    /// </summary>
    internal TResult Mutate<TAux, TResult>(Func<IReadOnlyDictionary<string, T>, TAux?, (T[] Changed, TAux Aux, TResult Result)> change)
        where TAux : class => persistence.Update<TResult>(state =>
        {
            var (changed, aux, result) = change(state.Items, state.Aux?.Deserialize<TAux>(Json));
            return (state with { Items = WithItems(state.Items, changed), Aux = JsonSerializer.SerializeToElement(aux, Json) },
                result);
        });

    private Dictionary<string, T> WithItems(Dictionary<string, T> items, IEnumerable<T> changed)
    {
        var next = new Dictionary<string, T>(items, StringComparer.Ordinal);
        foreach (var item in changed)
            next[key(item)] = item;
        return next;
    }

    private static string Fingerprint<TCommand>(TCommand command) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, Json)));

    private static SiteCatalogError Conflict() =>
        new(409, "CommandConflict", "This commandId was used with different inputs.");

    public void Dispose() => (persistence as IDisposable)?.Dispose();
}

internal static class SiteSeed
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static T[] Read<T>(string path, string section)
    {
        using var seed = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!seed.RootElement.TryGetProperty(section, out var records) || records.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Missing '{section}' in the site fixture.");
        return records.Deserialize<T[]>(Json) ?? throw new InvalidDataException($"Invalid '{section}' in the site fixture.");
    }
}
