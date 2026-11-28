using System.Data;
using System.Text.Json;
using Cassini.Demo.SiteCatalog;
using Cassini.Sheltering.Allocation;
using Cassini.Sheltering.Catalog;
using Npgsql;

namespace Cassini.Sheltering.Infrastructure;

/// <summary>
/// Sheltering's owned PostgreSQL store (SH-T04 Stage B). Shelters, assignments, command receipts, the allocation
/// outbox and reconciliation evidence are rows of their own. Every write runs in one transaction that first locks
/// the <c>sheltering_meta</c> row, so writers in any number of service instances are serialized and always decide on
/// the latest committed state. Changed rows are written with version checks and the schema enforces one assignment
/// per case and leg and one assignment per reservation.
/// </summary>
internal sealed class PostgresShelteringPersistence : ICatalogPersistence<Shelter>, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Ordered schema migrations. Applied ones are recorded and never edited; add a new entry instead.</summary>
    private static readonly (int Version, string Name, string Sql)[] Migrations =
    [
        (1, "allocation-store", """
            CREATE TABLE sheltering_meta (
                id integer PRIMARY KEY CHECK (id = 1),
                catalog_revision integer NOT NULL CHECK (catalog_revision >= 0),
                allocation_revision integer NOT NULL CHECK (allocation_revision >= 0),
                commit_seq bigint NOT NULL
            );
            CREATE TABLE shelters (
                shelter_id text PRIMARY KEY,
                version integer NOT NULL CHECK (version > 0),
                row_version bigint NOT NULL,
                document jsonb NOT NULL
            );
            CREATE TABLE shelter_assignments (
                incident_id text NOT NULL,
                case_id text NOT NULL,
                leg_id text NOT NULL,
                version integer NOT NULL CHECK (version > 0),
                status text NOT NULL,
                shelter_id text REFERENCES shelters (shelter_id),
                reservation_id text UNIQUE,
                row_version bigint NOT NULL,
                document jsonb NOT NULL,
                PRIMARY KEY (incident_id, case_id, leg_id)
            );
            CREATE TABLE sheltering_receipts (
                scope text NOT NULL CHECK (scope IN ('catalog', 'outcome', 'allocation')),
                command_id text NOT NULL,
                fingerprint text NOT NULL,
                result jsonb NOT NULL,
                PRIMARY KEY (scope, command_id)
            );
            CREATE TABLE sheltering_outbox (
                sequence bigint PRIMARY KEY,
                event_id text NOT NULL UNIQUE,
                document jsonb NOT NULL
            );
            CREATE TABLE allocation_evidence (
                sequence bigint PRIMARY KEY,
                command_id text NOT NULL,
                incident_id text NOT NULL,
                case_id text NOT NULL,
                leg_id text NOT NULL,
                assignment_version integer NOT NULL,
                document jsonb NOT NULL,
                UNIQUE (command_id, incident_id, case_id, leg_id)
            );
            CREATE INDEX allocation_evidence_subject ON allocation_evidence (incident_id, case_id, leg_id);
            """),
    ];

    private sealed record Loaded(
        CatalogState<Shelter> State, AllocationState Allocation, long CommitSeq,
        Dictionary<string, long> ShelterRows, Dictionary<string, long> AssignmentRows);

    private readonly NpgsqlDataSource dataSource;

    internal PostgresShelteringPersistence(string connectionString, Func<CatalogState<Shelter>> initial)
    {
        dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            Run(IsolationLevel.ReadCommitted, (connection, transaction) =>
            {
                // One instance migrates and seeds; others wait on the advisory lock and then see the result.
                Execute(connection, transaction, "SELECT pg_advisory_xact_lock(660405)");
                Execute(connection, transaction, """
                    CREATE TABLE IF NOT EXISTS sheltering_schema_migrations (
                        version integer PRIMARY KEY, name text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())
                    """);
                var applied = Query(connection, transaction, "SELECT version FROM sheltering_schema_migrations",
                    reader => reader.GetInt32(0)).ToHashSet();
                foreach (var (version, name, sql) in Migrations.Where(item => !applied.Contains(item.Version)))
                {
                    Execute(connection, transaction, sql);
                    Execute(connection, transaction,
                        "INSERT INTO sheltering_schema_migrations (version, name) VALUES ($1, $2)", version, name);
                }

                if (Query(connection, transaction, "SELECT 1 FROM sheltering_meta WHERE id = 1", _ => 1).Count == 0)
                {
                    var empty = new Loaded(new(0, new(), new()), AllocationState.Empty(), 0, new(), new());
                    var seed = initial();
                    Write(connection, transaction, empty, seed);
                    Execute(connection, transaction, """
                        INSERT INTO sheltering_meta (id, catalog_revision, allocation_revision, commit_seq)
                        VALUES (1, $1, 0, 0)
                        """, seed.Revision);
                }
                return 0;
            });
        }
        catch
        {
            dataSource.Dispose();
            throw;
        }
    }

    public CatalogState<Shelter> Read() =>
        Run(IsolationLevel.RepeatableRead, (connection, transaction) => Load(connection, transaction, lockMeta: false).State);

    public TResult Update<TResult>(Func<CatalogState<Shelter>, (CatalogState<Shelter>? Next, TResult Result)> apply) =>
        Run(IsolationLevel.ReadCommitted, (connection, transaction) =>
        {
            var loaded = Load(connection, transaction, lockMeta: true);
            var (next, result) = apply(loaded.State);
            if (next is null)
                return result;

            var allocationRevision = Write(connection, transaction, loaded, next);
            var bumped = Execute(connection, transaction, """
                UPDATE sheltering_meta SET catalog_revision = $1, allocation_revision = $2, commit_seq = commit_seq + 1
                WHERE id = 1 AND commit_seq = $3
                """, next.Revision, allocationRevision, loaded.CommitSeq);
            RequireOne(bumped);
            transaction.Commit();
            return result;
        }, commits: true);

    /// <summary>Reads one consistent state; with <paramref name="lockMeta"/> it also holds the writer lock.</summary>
    private static Loaded Load(NpgsqlConnection connection, NpgsqlTransaction transaction, bool lockMeta)
    {
        var meta = Query(connection, transaction,
            "SELECT catalog_revision, allocation_revision, commit_seq FROM sheltering_meta WHERE id = 1" +
            (lockMeta ? " FOR UPDATE" : string.Empty),
            reader => (Catalog: reader.GetInt32(0), Allocation: reader.GetInt32(1), Seq: reader.GetInt64(2))).Single();

        var shelterRows = new Dictionary<string, long>(StringComparer.Ordinal);
        var shelters = new Dictionary<string, Shelter>(StringComparer.Ordinal);
        foreach (var (id, row, shelter) in Query(connection, transaction,
            "SELECT shelter_id, row_version, document::text FROM shelters",
            reader => (reader.GetString(0), reader.GetInt64(1), Parse<Shelter>(reader.GetString(2)))))
        {
            shelterRows[id] = row;
            shelters[id] = shelter;
        }

        var catalogReceipts = new Dictionary<string, CatalogReceipt<Shelter>>(StringComparer.Ordinal);
        var outcomes = new Dictionary<string, CatalogOutcome>(StringComparer.Ordinal);
        var allocationReceipts = new Dictionary<string, AllocationReceipt>(StringComparer.Ordinal);
        foreach (var (scope, id, fingerprint, result) in Query(connection, transaction,
            "SELECT scope, command_id, fingerprint, result::text FROM sheltering_receipts",
            reader => (reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse<JsonElement>(reader.GetString(3)))))
        {
            switch (scope)
            {
                case "catalog":
                    catalogReceipts[id] = new(fingerprint, result.Deserialize<CatalogWrite<Shelter>>(Json)!);
                    break;
                case "outcome":
                    outcomes[id] = new(fingerprint, result);
                    break;
                default:
                    allocationReceipts[id] = new(fingerprint, result);
                    break;
            }
        }

        var assignmentRows = new Dictionary<string, long>(StringComparer.Ordinal);
        var assignments = new Dictionary<string, ShelterAssignment>(StringComparer.Ordinal);
        foreach (var (row, assignment) in Query(connection, transaction,
            "SELECT row_version, document::text FROM shelter_assignments",
            reader => (reader.GetInt64(0), Parse<ShelterAssignment>(reader.GetString(1)))))
        {
            assignmentRows[assignment.Ref.Key] = row;
            assignments[assignment.Ref.Key] = assignment;
        }

        var outbox = Query(connection, transaction, "SELECT document::text FROM sheltering_outbox ORDER BY sequence",
            reader => Parse<OutboxEntry>(reader.GetString(0))).ToArray();
        var evidence = Query(connection, transaction, "SELECT document::text FROM allocation_evidence ORDER BY sequence",
            reader => Parse<AllocationEvidence>(reader.GetString(0))).ToArray();
        var allocation = new AllocationState(meta.Allocation, assignments, allocationReceipts, outbox, evidence);
        var state = new CatalogState<Shelter>(meta.Catalog, shelters, catalogReceipts, outcomes,
            JsonSerializer.SerializeToElement(allocation, Json));
        return new(state, allocation, meta.Seq, shelterRows, assignmentRows);
    }

    /// <summary>Writes only what changed between the loaded and the next state and returns the allocation revision.</summary>
    private static int Write(NpgsqlConnection connection, NpgsqlTransaction transaction, Loaded loaded, CatalogState<Shelter> next)
    {
        foreach (var (id, shelter) in next.Items)
        {
            var document = JsonSerializer.Serialize(shelter, Json);
            if (!loaded.State.Items.TryGetValue(id, out var previous))
                Execute(connection, transaction,
                    "INSERT INTO shelters (shelter_id, version, row_version, document) VALUES ($1, $2, 1, $3::jsonb)",
                    id, shelter.Version, document);
            else if (document != JsonSerializer.Serialize(previous, Json))
                RequireOne(Execute(connection, transaction, """
                    UPDATE shelters SET version = $2, row_version = row_version + 1, document = $3::jsonb
                    WHERE shelter_id = $1 AND row_version = $4
                    """, id, shelter.Version, document, loaded.ShelterRows[id]));
        }

        InsertReceipts(connection, transaction, "catalog", loaded.State.Receipts, next.Receipts,
            receipt => (receipt.Fingerprint, JsonSerializer.Serialize(receipt.Result, Json)));
        InsertReceipts(connection, transaction, "outcome", loaded.State.Outcomes ?? new(), next.Outcomes ?? new(),
            outcome => (outcome.Fingerprint, outcome.Result.GetRawText()));

        if (next.Aux?.Deserialize<AllocationState>(Json) is not { } allocation)
            return loaded.Allocation.Revision;

        foreach (var (key, assignment) in allocation.Assignments)
        {
            var document = JsonSerializer.Serialize(assignment, Json);
            object?[] columns = [assignment.IncidentId, assignment.CaseId, assignment.LegId, assignment.Version,
                assignment.Status, assignment.Reservation?.ShelterId, assignment.Reservation?.ReservationId, document];
            if (!loaded.Allocation.Assignments.TryGetValue(key, out var previous))
                Execute(connection, transaction, """
                    INSERT INTO shelter_assignments
                        (incident_id, case_id, leg_id, version, status, shelter_id, reservation_id, document, row_version)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, 1)
                    """, columns);
            else if (document != JsonSerializer.Serialize(previous, Json))
                RequireOne(Execute(connection, transaction, """
                    UPDATE shelter_assignments SET version = $4, status = $5, shelter_id = $6, reservation_id = $7,
                        document = $8::jsonb, row_version = row_version + 1
                    WHERE incident_id = $1 AND case_id = $2 AND leg_id = $3 AND row_version = $9
                    """, [.. columns, loaded.AssignmentRows[key]]));
        }

        InsertReceipts(connection, transaction, "allocation", loaded.Allocation.Receipts, allocation.Receipts,
            receipt => (receipt.Fingerprint, receipt.Result.GetRawText()));
        var lastEvent = loaded.Allocation.Outbox.Length == 0 ? 0 : loaded.Allocation.Outbox[^1].Sequence;
        foreach (var entry in allocation.Outbox.Where(item => item.Sequence > lastEvent))
            Execute(connection, transaction,
                "INSERT INTO sheltering_outbox (sequence, event_id, document) VALUES ($1, $2, $3::jsonb)",
                entry.Sequence, entry.Event.EventId, JsonSerializer.Serialize(entry, Json));
        var previousEvidence = loaded.Allocation.Evidence ?? [];
        var lastEvidence = previousEvidence.Length == 0 ? 0 : previousEvidence[^1].Sequence;
        foreach (var item in (allocation.Evidence ?? []).Where(item => item.Sequence > lastEvidence))
            Execute(connection, transaction, """
                INSERT INTO allocation_evidence
                    (sequence, command_id, incident_id, case_id, leg_id, assignment_version, document)
                VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb)
                """, item.Sequence, item.CommandId, item.IncidentId, item.CaseId, item.LegId, item.AssignmentVersion,
                JsonSerializer.Serialize(item, Json));
        return allocation.Revision;
    }

    /// <summary>Receipts are append-only: a new key is inserted; the primary key rejects a duplicate command.</summary>
    private static void InsertReceipts<TReceipt>(NpgsqlConnection connection, NpgsqlTransaction transaction, string scope,
        IReadOnlyDictionary<string, TReceipt> previous, IReadOnlyDictionary<string, TReceipt> next,
        Func<TReceipt, (string Fingerprint, string Result)> columns)
    {
        foreach (var (id, receipt) in next.Where(entry => !previous.ContainsKey(entry.Key)))
        {
            var (fingerprint, result) = columns(receipt);
            Execute(connection, transaction, """
                INSERT INTO sheltering_receipts (scope, command_id, fingerprint, result) VALUES ($1, $2, $3, $4::jsonb)
                """, scope, id, fingerprint, result);
        }
    }

    private T Run<T>(IsolationLevel isolation, Func<NpgsqlConnection, NpgsqlTransaction, T> work, bool commits = false)
    {
        try
        {
            using var connection = dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction(isolation);
            var result = work(connection, transaction);
            if (!commits)
                transaction.Commit();
            return result;
        }
        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.SerializationFailure)
        {
            throw new SiteCatalogError(409, "StoreConflict", "The change conflicts with committed Sheltering state. Read again.");
        }
        catch (NpgsqlException)
        {
            throw new SiteCatalogError(503, "StoreUnavailable", "The Sheltering database is unavailable.");
        }
    }

    private static int Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params object?[] values)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        return command.ExecuteNonQuery();
    }

    private static List<T> Query<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        Func<NpgsqlDataReader, T> map)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(map(reader));
        return rows;
    }

    private static void RequireOne(int rows)
    {
        if (rows != 1)
            throw new SiteCatalogError(409, "StoreVersionConflict", "A Sheltering row changed concurrently. Read again.");
    }

    private static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException("Empty Sheltering row.");

    public void Dispose() => dataSource.Dispose();
}
