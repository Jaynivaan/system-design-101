using AgencyHarness.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AgencyHarness.Infrastructure;

public record ClientBrief(string Client, string Brief, string Project, string ProjectGoal);

/// <summary>
/// Cache-aside (SD101: "Top caching strategies"). Every agent turn starts by reading
/// the brief, so it's the hottest read in the system. On a miss we load from the DB
/// and populate the cache with a TTL; writes invalidate the entry explicitly.
/// Swap IMemoryCache for IDistributedCache (Redis) when you run more than one node.
/// </summary>
public class ClientBriefCache(IMemoryCache cache, IServiceScopeFactory scopes)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private static string Key(Guid tenantId, Guid projectId) => $"brief:{tenantId}:{projectId}";

    public async Task<ClientBrief?> GetAsync(Guid tenantId, Guid projectId, CancellationToken ct)
    {
        if (cache.TryGetValue(Key(tenantId, projectId), out ClientBrief? hit))
        {
            Telemetry.CacheHits.Add(1);
            return hit;
        }
        Telemetry.CacheMisses.Add(1);

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarnessDbContext>();
        var brief = await (from p in db.Projects.AsNoTracking()
                           join c in db.Clients.AsNoTracking() on p.ClientId equals c.Id
                           where p.Id == projectId && p.TenantId == tenantId
                           select new ClientBrief(c.Name, c.Brief, p.Name, p.Goal)).FirstOrDefaultAsync(ct);
        if (brief is not null) cache.Set(Key(tenantId, projectId), brief, Ttl);
        return brief;
    }

    /// <summary>Invalidate every project of a client after its brief changes.</summary>
    public async Task InvalidateClientAsync(HarnessDbContext db, Guid tenantId, Guid clientId, CancellationToken ct)
    {
        var projectIds = await db.Projects.Where(p => p.TenantId == tenantId && p.ClientId == clientId)
            .Select(p => p.Id).ToListAsync(ct);
        foreach (var id in projectIds) cache.Remove(Key(tenantId, id));
    }
}

/// <summary>Adds events to the transactional outbox (same SaveChanges as the state change).</summary>
public class Outbox
{
    public void Add(HarnessDbContext db, Guid tenantId, string eventType, object data)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid(), type = eventType, createdAt = DateTimeOffset.UtcNow, data,
        });
        db.Outbox.Add(new OutboxMessage { TenantId = tenantId, EventType = eventType, Payload = payload });
    }
}
