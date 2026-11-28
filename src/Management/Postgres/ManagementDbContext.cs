using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cassini.Management.Postgres;

/// <summary>
/// Management tables in <c>management_db</c>. Aggregates are stored as JSON documents keyed by their IDs;
/// the outbox and inbox share the transaction of the state change.
/// </summary>
internal sealed class ManagementDbContext(DbContextOptions<ManagementDbContext> options) : DbContext(options)
{
    public DbSet<IncidentRow> Incidents => Set<IncidentRow>();
    public DbSet<CaseRow> Cases => Set<CaseRow>();
    public DbSet<DocumentRow> Alerts => Set<DocumentRow>();
    public DbSet<BroadcastRow> Broadcasts => Set<BroadcastRow>();
    public DbSet<ReceiptRow> Receipts => Set<ReceiptRow>();
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();
    public DbSet<InboxRow> Inbox => Set<InboxRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<IncidentRow>(entity =>
        {
            entity.ToTable("incidents");
            entity.HasKey(row => row.IncidentId);
        });
        model.Entity<CaseRow>(entity =>
        {
            entity.ToTable("cases");
            entity.HasKey(row => row.CaseId);
            entity.Property(row => row.Payload).HasColumnType("jsonb");
        });
        model.Entity<DocumentRow>(entity =>
        {
            entity.ToTable("alerts");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Payload).HasColumnType("jsonb");
            entity.HasIndex(row => row.Sequence).IsUnique();
        });
        model.Entity<BroadcastRow>(entity =>
        {
            entity.ToTable("ewss_broadcasts");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Payload).HasColumnType("jsonb");
            entity.HasIndex(row => row.Sequence).IsUnique();
        });
        model.Entity<ReceiptRow>(entity =>
        {
            entity.ToTable("command_receipts");
            entity.HasKey(row => row.CommandId);
            entity.Property(row => row.Result).HasColumnType("jsonb");
        });
        model.Entity<OutboxRow>(entity =>
        {
            entity.ToTable("outbox");
            entity.HasKey(row => row.EventId);
            entity.Property(row => row.Sequence).UseIdentityAlwaysColumn();
            entity.Property(row => row.Payload).HasColumnType("jsonb");
            entity.HasIndex(row => row.Sequence).IsUnique();
        });
        model.Entity<InboxRow>(entity =>
        {
            entity.ToTable("inbox");
            entity.HasKey(row => row.Receipt);
            entity.Property(row => row.Sequence).UseIdentityAlwaysColumn();
        });
    }
}

internal sealed class IncidentRow
{
    public required string IncidentId { get; set; }
}

internal sealed class CaseRow
{
    public required string CaseId { get; set; }
    public int Ordinal { get; set; }
    public int Version { get; set; }
    public required string Payload { get; set; }
}

internal sealed class DocumentRow
{
    public required string Id { get; set; }
    public int Sequence { get; set; }
    public required string Payload { get; set; }
}

internal sealed class BroadcastRow
{
    public required string Id { get; set; }
    public int Sequence { get; set; }
    public required string Payload { get; set; }
}

internal sealed class ReceiptRow
{
    public required string CommandId { get; set; }
    public required string Identity { get; set; }
    public required string Result { get; set; }
}

internal sealed class OutboxRow
{
    public required string EventId { get; set; }
    public long Sequence { get; set; }
    public required string Payload { get; set; }
}

internal sealed class InboxRow
{
    public required string Receipt { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

/// <summary>Used by <c>dotnet ef migrations</c>; the connection is never opened at design time.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ManagementDbContext>
{
    public ManagementDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ManagementDbContext>()
        .UseNpgsql("Host=localhost;Database=management_db").Options);
}
