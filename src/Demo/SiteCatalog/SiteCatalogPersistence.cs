using System.Text.Json;
#if SITE_CATALOG_STAGE_B
using Npgsql;
using NpgsqlTypes;
#endif

namespace Cassini.Demo.SiteCatalog;

internal sealed partial class CatalogStore<T> where T : class
{
    internal CatalogStore(string owner, string? path, string? connectionString, Func<T[]> seed,
        Func<T, string> key, Func<T, int> version)
        : this(CreatePersistence(owner, path, connectionString, () => Initial(seed, key)), key, version, owner)
    {
    }

    private static ICatalogPersistence<T> CreatePersistence(string owner, string? path, string? connectionString,
        Func<CatalogState<T>> initial)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("The site catalog requires durable storage.");
            return new FileCatalogPersistence<T>(path, initial);
        }
#if SITE_CATALOG_STAGE_B
        return new PostgresCatalogSnapshot<T>(connectionString, owner,
            () => new FileCatalogPersistence<T>(path, initial).Read());
#else
        throw new InvalidOperationException("Build with EnableSiteCatalogStageB=true to use a catalog database.");
#endif
    }
}

#if SITE_CATALOG_STAGE_B
internal sealed class PostgresCatalogSnapshot<T> : ICatalogPersistence<T>, IDisposable where T : class
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource database;
    private readonly string owner;

    internal PostgresCatalogSnapshot(string connectionString, string owner, Func<CatalogState<T>> initial)
    {
        this.owner = owner;
        database = NpgsqlDataSource.Create(connectionString);
        try
        {
            using var initialize = database.CreateCommand(
                "INSERT INTO cassini_site_catalog (owner, snapshot) VALUES ($1, $2) ON CONFLICT (owner) DO NOTHING");
            initialize.Parameters.AddWithValue(owner);
            initialize.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(initial(), Json));
            initialize.ExecuteNonQuery();
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    public CatalogState<T> Read()
    {
        using var connection = database.OpenConnection();
        return Read(connection);
    }

    public TResult Update<TResult>(Func<CatalogState<T>, (CatalogState<T>? Next, TResult Result)> apply)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var (next, result) = apply(Read(connection, transaction));
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

    private CatalogState<T> Read(NpgsqlConnection connection, NpgsqlTransaction? transaction = null)
    {
        var query = "SELECT snapshot FROM cassini_site_catalog WHERE owner = $1";
        if (transaction is not null)
            query += " FOR UPDATE";
        using var select = new NpgsqlCommand(query, connection, transaction);
        select.Parameters.AddWithValue(owner);
        return select.ExecuteScalar() is string snapshot
            ? JsonSerializer.Deserialize<CatalogState<T>>(snapshot, Json) ?? throw new InvalidDataException("Invalid site catalog.")
            : throw new InvalidDataException("The site catalog has no database snapshot.");
    }

    public void Dispose() => database.Dispose();
}
#endif
