using System.Text.Json;
using Npgsql;

namespace Cassini.Dispatch;

internal sealed class PostgresDispatchPersistence : IDispatchPersistence, IDisposable
{
    private const string CreateTables = """
        CREATE TABLE IF NOT EXISTS dispatch_state (
            id integer PRIMARY KEY CHECK (id = 1),
            payload jsonb NOT NULL
        );
        CREATE TABLE IF NOT EXISTS dispatch_active_booking (
            unit_id text PRIMARY KEY,
            request_id text NOT NULL UNIQUE,
            assignment_id text NOT NULL UNIQUE
        );
        """;

    private readonly NpgsqlDataSource dataSource;

    internal PostgresDispatchPersistence(string connectionString, string? dataDirectory)
    {
        dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            using var connection = dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var bootstrap = new NpgsqlCommand("SELECT pg_advisory_xact_lock(660404);\n" + CreateTables,
                connection, transaction);
            bootstrap.ExecuteNonQuery();

            using var hasState = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM dispatch_state WHERE id = 1)",
                connection, transaction);
            if (hasState.ExecuteScalar() is false)
            {
                var initial = DispatchStore.InitialState(dataDirectory);
                using var seed = new NpgsqlCommand("INSERT INTO dispatch_state (id, payload) VALUES (1, $1::jsonb)",
                    connection, transaction);
                seed.Parameters.AddWithValue(JsonSerializer.Serialize(initial, DispatchStore.Json));
                seed.ExecuteNonQuery();
                SaveBookings(connection, transaction, initial.View);
            }
            transaction.Commit();
        }
        catch
        {
            dataSource.Dispose();
            throw;
        }
    }

    public DispatchState Read()
    {
        try
        {
            using var command = dataSource.CreateCommand("SELECT payload::text FROM dispatch_state WHERE id = 1");
            return ReadState(command);
        }
        catch (NpgsqlException exception)
        {
            throw new DispatchPersistenceException(exception);
        }
    }

    public IResult Update(Func<DispatchState, (DispatchState? Next, IResult Result)> apply)
    {
        try
        {
            using var connection = dataSource.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var read = new NpgsqlCommand("SELECT payload::text FROM dispatch_state WHERE id = 1 FOR UPDATE",
                connection, transaction);
            var (next, result) = apply(ReadState(read));
            if (next is not null)
            {
                SaveBookings(connection, transaction, next.View);
                using var update = new NpgsqlCommand("UPDATE dispatch_state SET payload = $1::jsonb WHERE id = 1",
                    connection, transaction);
                update.Parameters.AddWithValue(JsonSerializer.Serialize(next, DispatchStore.Json));
                update.ExecuteNonQuery();
            }
            transaction.Commit();
            return result;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return DispatchStore.Error("BookingConflict", "An active booking for this unit or request already exists.", 409);
        }
        catch (NpgsqlException exception)
        {
            throw new DispatchPersistenceException(exception);
        }
    }

    private static DispatchState ReadState(NpgsqlCommand command) =>
        JsonSerializer.Deserialize<DispatchState>((string)command.ExecuteScalar()!, DispatchStore.Json)
        ?? throw new InvalidDataException("The Dispatch database state is empty.");

    private static void SaveBookings(NpgsqlConnection connection, NpgsqlTransaction transaction, DispatchView view)
    {
        using var clear = new NpgsqlCommand("DELETE FROM dispatch_active_booking", connection, transaction);
        clear.ExecuteNonQuery();
        var active = view.Assignments.Where(item => item.Active).ToArray();
        if (active.Length == 0)
            return;

        using var insert = new NpgsqlCommand("""
            INSERT INTO dispatch_active_booking (unit_id, request_id, assignment_id)
            SELECT * FROM unnest($1::text[], $2::text[], $3::text[])
            """, connection, transaction);
        insert.Parameters.AddWithValue(active.Select(item => item.UnitId).ToArray());
        insert.Parameters.AddWithValue(active.Select(item => item.RequestId).ToArray());
        insert.Parameters.AddWithValue(active.Select(item => item.AssignmentId).ToArray());
        insert.ExecuteNonQuery();
    }

    public void Dispose() => dataSource.Dispose();
}
