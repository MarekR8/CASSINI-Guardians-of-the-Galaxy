using System.Security.Cryptography;
using System.Text.Json;
#if SITE_CATALOG_STAGE_B
using Npgsql;
using NpgsqlTypes;
#endif

namespace Cassini.Demo.SiteCatalog;

internal sealed record CatalogWrite<T>(int Revision, T Item);

// The two owner services each instantiate this store with their own domain type and file.
internal sealed class CatalogStore<T> : IDisposable where T : class
{
    private sealed record Receipt(string Fingerprint, CatalogWrite<T> Result, CatalogActor? Actor = null);
    private sealed record Audit(int CatalogRevision, string CommandId, CatalogActor Actor, DateTimeOffset At,
        string SubjectId, int SubjectVersion, T? Before, T After);
    private sealed record State(int Revision, Dictionary<string, T> Items, Dictionary<string, Receipt> Receipts,
        Audit[]? Audit = null, CatalogOutboxEntry[]? Outbox = null);
    private sealed record CatalogOutboxEntry(string RoutingKey, CatalogEvent Event, DateTimeOffset? PublishedAt = null);
    private sealed record CatalogEvent(Guid EventId, string Type, string Producer, string SubjectId, int SubjectVersion,
        DateTimeOffset OccurredAt, string CorrelationId, string CausationId, JsonElement Payload);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object gate = new();
    private readonly string? path;
    private readonly Func<T, int> version;
    private readonly string owner;
    private State state;
#if SITE_CATALOG_STAGE_B
    private readonly NpgsqlDataSource? database;
#endif

    internal CatalogStore(string owner, string? path, string? connectionString, Func<T[]> seed, Func<T, string> key,
        Func<T, int> version)
    {
        this.owner = owner;
        this.path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        if (this.path is null && string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("The site catalog requires durable storage.");
        this.version = version;
        state = this.path is not null && File.Exists(this.path)
            ? JsonSerializer.Deserialize<State>(File.ReadAllBytes(this.path), Json)
                ?? throw new InvalidDataException("The site catalog is empty.")
            : new State(0, seed().ToDictionary(key, StringComparer.Ordinal), new(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

#if SITE_CATALOG_STAGE_B
        database = NpgsqlDataSource.Create(connectionString);
        try
        {
            using var initialize = database.CreateCommand(
                "INSERT INTO cassini_site_catalog (owner, snapshot) VALUES ($1, $2) ON CONFLICT (owner) DO NOTHING");
            initialize.Parameters.AddWithValue(owner);
            initialize.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(state, Json));
            initialize.ExecuteNonQuery();
        }
        catch
        {
            database.Dispose();
            throw;
        }
#else
        throw new InvalidOperationException("Build with EnableSiteCatalogStageB=true to use a catalog database.");
#endif
    }

    internal (int Revision, T[] Items) Read()
    {
#if SITE_CATALOG_STAGE_B
        if (database is not null)
        {
            using var connection = database.OpenConnection();
            var current = ReadDatabase(connection);
            return (current.Revision, current.Items.Values.ToArray());
        }
#endif
        lock (gate)
            return (state.Revision, state.Items.Values.ToArray());
    }

    internal CatalogWrite<T> Save<TCommand>(string? commandId, int? expectedVersion, string key, TCommand command,
        CatalogActor actor, DateTimeOffset at, Func<T?, int, T> change, Func<T?, T, SiteChange> describe)
    {
        if (!SiteRules.IsId(commandId) || expectedVersion is null or < 0)
            throw new SiteCatalogError(400, "InvalidCommand", "Specify a commandId and a non-negative expectedVersion.");

        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, Json)));
#if SITE_CATALOG_STAGE_B
        if (database is not null)
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var (next, result) = Apply(ReadDatabase(connection, transaction));
            if (next is not null)
            {
                using var update = new NpgsqlCommand(
                    "UPDATE cassini_site_catalog SET snapshot = $2 WHERE owner = $1", connection, transaction);
                update.Parameters.AddWithValue(owner);
                update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(next, Json));
                update.ExecuteNonQuery();
            }
            transaction.Commit();
            return result;
        }
#endif
        lock (gate)
        {
            var (next, result) = Apply(state);
            if (next is null)
                return result;
            Persist(path!, next);
            state = next;
            return result;
        }

        (State? Next, CatalogWrite<T> Result) Apply(State current)
        {
            if (current.Receipts.TryGetValue(commandId!, out var receipt))
            {
                if ((receipt.Actor ?? CatalogActor.Demo) != actor)
                    throw SiteCatalogAccess.Forbidden();
                if (receipt.Fingerprint != fingerprint)
                    throw new SiteCatalogError(409, "CommandConflict", "This commandId was used with different inputs.");
                return (null, receipt.Result);
            }

            var previous = current.Items.GetValueOrDefault(key);
            var currentVersion = previous is null ? 0 : version(previous);
            if (expectedVersion != currentVersion)
                throw new SiteCatalogError(409, "VersionConflict", "Read the current site before updating it.");

            var item = change(previous, checked(currentVersion + 1));
            var result = new CatalogWrite<T>(checked(current.Revision + 1), item);
            var fact = describe(previous, item);
            var audit = new Audit(result.Revision, commandId!, actor, at, fact.SubjectId, version(item), previous, item);
            var message = new CatalogEvent(Guid.NewGuid(), fact.Type, owner, fact.SubjectId, version(item), at,
                commandId!, commandId!, JsonSerializer.SerializeToElement(fact.Payload, Json));
            var next = new State(result.Revision, new Dictionary<string, T>(current.Items, StringComparer.Ordinal)
            {
                [key] = item,
            }, new Dictionary<string, Receipt>(current.Receipts, StringComparer.Ordinal)
            {
                [commandId!] = new Receipt(fingerprint, result, actor),
            }, [.. current.Audit ?? [], audit], [.. current.Outbox ?? [], new(fact.RoutingKey, message)]);
            return (next, result);
        }
    }

#if SITE_CATALOG_STAGE_B
    private State ReadDatabase(NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
    {
        var query = "SELECT snapshot FROM cassini_site_catalog WHERE owner = $1";
        if (transaction is not null)
            query += " FOR UPDATE";
        using var select = new NpgsqlCommand(query, connection, transaction);
        select.Parameters.AddWithValue(owner);
        return select.ExecuteScalar() is string snapshot
            ? JsonSerializer.Deserialize<State>(snapshot, Json) ?? throw new InvalidDataException("Invalid site catalog.")
            : throw new InvalidDataException("The site catalog has no database snapshot.");
    }
#endif

    public void Dispose()
    {
#if SITE_CATALOG_STAGE_B
        database?.Dispose();
#endif
    }

    private static void Persist(string path, State next)
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

internal sealed record SiteChange(string Type, string RoutingKey, string SubjectId, object Payload);

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
