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
    }
}
