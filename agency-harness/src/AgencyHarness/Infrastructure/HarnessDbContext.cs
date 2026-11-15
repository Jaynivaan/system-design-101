using AgencyHarness.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgencyHarness.Infrastructure;

public class HarnessDbContext(DbContextOptions<HarnessDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<AgentJob> Jobs => Set<AgentJob>();
    public DbSet<Deliverable> Deliverables => Set<Deliverable>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<UsageRecord> Usage => Set<UsageRecord>();
    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();
    public DbSet<Agenda> Agendas => Set<Agenda>();
    public DbSet<AgendaTask> AgendaTasks => Set<AgendaTask>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<Lesson> Lessons => Set<Lesson>();
    public DbSet<Feedback> Feedback => Set<Feedback>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // SQLite cannot order/compare DateTimeOffset natively; store as UTC ticks
        // so the queue's "ORDER BY priority, next_attempt_at" works in SQL.
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var prop in entity.GetProperties())
        {
            if (prop.ClrType == typeof(DateTimeOffset))
                prop.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion
                    .ValueConverter<DateTimeOffset, long>(
                        v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero)));
            if (prop.ClrType == typeof(DateTimeOffset?))
                prop.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion
                    .ValueConverter<DateTimeOffset?, long?>(
                        v => v.HasValue ? v.Value.UtcTicks : null,
                        v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null));
            if (prop.ClrType == typeof(decimal))
                prop.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion
                    .ValueConverter<decimal, double>(v => (double)v, v => (decimal)v));
        }

        // Indexes are the "data structures that power your database" (SD101):
        // each one exists to serve a specific hot query.
        b.Entity<Tenant>().HasIndex(t => t.ApiKeyHash).IsUnique();
        b.Entity<Client>().HasIndex(c => new { c.TenantId, c.CreatedAt });
        b.Entity<Project>().HasIndex(p => new { p.TenantId, p.ClientId });

        b.Entity<AgentJob>(j =>
        {
            j.Property(x => x.Status).HasConversion<string>();
            j.Property(x => x.Version).IsConcurrencyToken();
            // The dequeue query: WHERE status IN (...) AND next_attempt_at <= now ORDER BY priority, next_attempt_at
            j.HasIndex(x => new { x.Status, x.Priority, x.NextAttemptAt });
            j.HasIndex(x => new { x.TenantId, x.ProjectId, x.CreatedAt });
        });

        b.Entity<Deliverable>().HasIndex(d => new { d.TenantId, d.ProjectId });
        b.Entity<OutboxMessage>().HasIndex(o => new { o.DeliveredAt, o.NextAttemptAt });
        b.Entity<UsageRecord>().HasIndex(u => new { u.TenantId, u.CreatedAt });
        b.Entity<IdempotencyRecord>().HasKey(i => new { i.TenantId, i.Key });

        // Swarms, skills and learning.
        b.Entity<Agenda>(a => { a.Property(x => x.Status).HasConversion<string>(); a.HasIndex(x => new { x.TenantId, x.Status }); });
        b.Entity<AgendaTask>(t =>
        {
            t.Property(x => x.Status).HasConversion<string>();
            t.HasIndex(x => new { x.AgendaId, x.Status });
            t.HasIndex(x => new { x.AgendaId, x.Key }).IsUnique();   // the planner can't create the same task twice
            t.HasIndex(x => x.JobId);
            t.HasIndex(x => x.ReviewJobId);
        });
        b.Entity<Skill>().HasIndex(x => new { x.TenantId, x.Name });
        b.Entity<Lesson>().HasIndex(x => new { x.TenantId, x.Fingerprint });
        b.Entity<Feedback>().HasIndex(x => new { x.TenantId, x.DeliverableId });
    }
}

/// <summary>
/// Additive schema sync: EnsureCreated only builds a brand-new database, so on an existing one this
/// creates any tables and indexes that newer versions added (IF NOT EXISTS), leaving existing data alone.
/// New features only add tables, never columns, so this is all an upgrade needs.
/// </summary>
public static class SchemaSync
{
    public static int Apply(HarnessDbContext db)
    {
        var script = db.Database.GenerateCreateScript();
        int applied = 0;
        foreach (var raw in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var stmt = raw.Trim();
            if (stmt.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase))
                stmt = "CREATE TABLE IF NOT EXISTS " + stmt["CREATE TABLE ".Length..];
            else if (stmt.StartsWith("CREATE UNIQUE INDEX ", StringComparison.OrdinalIgnoreCase))
                stmt = "CREATE UNIQUE INDEX IF NOT EXISTS " + stmt["CREATE UNIQUE INDEX ".Length..];
            else if (stmt.StartsWith("CREATE INDEX ", StringComparison.OrdinalIgnoreCase))
                stmt = "CREATE INDEX IF NOT EXISTS " + stmt["CREATE INDEX ".Length..];
            else continue;
            db.Database.ExecuteSqlRaw(stmt);
            applied++;
        }
        return applied;
    }
}
