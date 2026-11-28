using System.Text.Json;
using Cassini.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cassini.Management.Postgres;

/// <summary>
/// PostgreSQL persistence. Each update runs in one transaction that locks the incident row, applies the command
/// to the loaded state and writes only the rows that changed, including outbox and inbox entries.
/// </summary>
internal sealed class PostgresManagementPersistence : IManagementPersistence
{
    private static readonly JsonSerializerOptions Json = ManagementStore.Json;
    private readonly DbContextOptions<ManagementDbContext> options;

    public PostgresManagementPersistence(string connectionString)
    {
        options = new DbContextOptionsBuilder<ManagementDbContext>().UseNpgsql(connectionString).Options;
        try
        {
            using var context = new ManagementDbContext(options);
            context.Database.Migrate();
            using var transaction = context.Database.BeginTransaction();
            context.Database.ExecuteSqlRaw("SELECT pg_advisory_xact_lock(660406)");
            if (!context.Incidents.Any())
            {
                Write(context, Empty, ManagementState.Initial());
                context.SaveChanges();
            }
            transaction.Commit();
        }
        catch (Exception exception) when (exception is NpgsqlException or DbUpdateException or InvalidOperationException)
        {
            throw new ManagementPersistenceException(exception);
        }
    }

    private static readonly ManagementState Empty = new(new IncidentView("", [], []), []);

    public ManagementState Read()
    {
        try
        {
            using var context = new ManagementDbContext(options);
            return Load(context);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            throw new ManagementPersistenceException(exception);
        }
    }

    public IResult Update(Func<ManagementState, (ManagementState? Next, IResult Result)> apply,
        Action<ManagementState>? committed = null)
    {
        try
        {
            using var context = new ManagementDbContext(options);
            using var transaction = context.Database.BeginTransaction();
            context.Database.ExecuteSqlRaw("SELECT 1 FROM incidents FOR UPDATE");
            var current = Load(context);
            var (next, result) = apply(current);
            if (next is null)
                return result;

            Write(context, current, next);
            context.SaveChanges();
            transaction.Commit();
            committed?.Invoke(next);
            return result;
        }
        catch (Exception exception) when (exception is NpgsqlException or DbUpdateException or InvalidOperationException)
        {
            throw new ManagementPersistenceException(exception);
        }
    }

    private static ManagementState Load(ManagementDbContext context)
    {
        var incident = context.Incidents.AsNoTracking().Single();
        var cases = context.Cases.AsNoTracking().OrderBy(row => row.Ordinal)
            .Select(row => row.Payload).AsEnumerable().Select(Parse<EvacuationCase>).ToArray();
        var alerts = context.Alerts.AsNoTracking().OrderBy(row => row.Sequence)
            .Select(row => row.Payload).AsEnumerable().Select(Parse<HazardAlert>).ToArray();
        var broadcasts = context.Broadcasts.AsNoTracking().OrderBy(row => row.Sequence)
            .Select(row => row.Payload).AsEnumerable().Select(Parse<EwssBroadcast>).ToArray();
        var receipts = context.Receipts.AsNoTracking().AsEnumerable().ToDictionary(row => row.CommandId,
            row => new Receipt(row.Identity, JsonDocument.Parse(row.Result).RootElement.Clone()));
        var outbox = context.Outbox.AsNoTracking().OrderBy(row => row.Sequence)
            .Select(row => row.Payload).AsEnumerable().Select(Parse<IntegrationEvent>).ToArray();
        var inbox = context.Inbox.AsNoTracking().OrderBy(row => row.Sequence).Select(row => row.Receipt).ToArray();
        return new(new IncidentView(incident.IncidentId, alerts, cases), receipts, broadcasts, outbox, inbox);
    }

    private static void Write(ManagementDbContext context, ManagementState current, ManagementState next)
    {
        if (current.Incident.IncidentId != next.Incident.IncidentId)
            context.Incidents.Add(new IncidentRow { IncidentId = next.Incident.IncidentId });

        var known = current.Incident.Cases.ToDictionary(item => item.CaseId);
        for (var ordinal = 0; ordinal < next.Incident.Cases.Length; ordinal++)
        {
            var item = next.Incident.Cases[ordinal];
            if (known.TryGetValue(item.CaseId, out var previous) && Equals(previous, item))
                continue;
            var row = new CaseRow { CaseId = item.CaseId, Ordinal = ordinal, Version = item.Version, Payload = Serialize(item) };
            if (previous is null)
                context.Cases.Add(row);
            else
                context.Cases.Update(row);
        }

        foreach (var (alert, sequence) in Added(current.Incident.Alerts, next.Incident.Alerts))
            context.Alerts.Add(new DocumentRow { Id = alert.AlertId, Sequence = sequence, Payload = Serialize(alert) });
        foreach (var (broadcast, sequence) in Added(current.Broadcasts, next.Broadcasts))
            context.Broadcasts.Add(new BroadcastRow { Id = broadcast.BroadcastId, Sequence = sequence, Payload = Serialize(broadcast) });

        foreach (var (id, receipt) in next.Receipts.Where(pair => !current.Receipts.ContainsKey(pair.Key)))
            context.Receipts.Add(new ReceiptRow { CommandId = id, Identity = receipt.Command, Result = receipt.Result.GetRawText() });

        var pending = next.Outbox.Select(message => message.EventId).ToHashSet();
        var published = current.Outbox.Where(message => !pending.Contains(message.EventId)).Select(message => message.EventId).ToArray();
        if (published.Length > 0)
            context.Outbox.Where(row => published.Contains(row.EventId)).ExecuteDelete();
        var previouslyPending = current.Outbox.Select(message => message.EventId).ToHashSet();
        foreach (var message in next.Outbox.Where(message => !previouslyPending.Contains(message.EventId)))
            context.Outbox.Add(new OutboxRow { EventId = message.EventId, Payload = Serialize(message) });

        var kept = next.Inbox.ToHashSet();
        var pruned = current.Inbox.Where(receipt => !kept.Contains(receipt)).ToArray();
        if (pruned.Length > 0)
            context.Inbox.Where(row => pruned.Contains(row.Receipt)).ExecuteDelete();
        var processed = current.Inbox.ToHashSet();
        foreach (var receipt in next.Inbox.Where(receipt => !processed.Contains(receipt)))
            context.Inbox.Add(new InboxRow { Receipt = receipt, ProcessedAt = DateTimeOffset.UtcNow });
    }

    /// <summary>Append-only collections: the new tail of <paramref name="next"/> with its position.</summary>
    private static IEnumerable<(T Item, int Sequence)> Added<T>(T[] current, T[] next) =>
        next.Skip(current.Length).Select((item, index) => (item, current.Length + index + 1));

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static T Parse<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException($"Invalid {typeof(T).Name} row.");
}
