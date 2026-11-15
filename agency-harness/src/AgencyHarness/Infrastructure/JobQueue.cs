using AgencyHarness.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgencyHarness.Infrastructure;

/// <summary>
/// A durable work queue on top of the database.
///
/// SD101 concepts in play:
///  * Competing consumers: many workers poll; a compare-and-swap UPDATE ensures
///    only one wins each job (on Postgres you'd use SELECT ... FOR UPDATE SKIP LOCKED).
///  * Visibility timeout / lease: a claimed job is invisible until the lease
///    expires; a crashed worker's job becomes claimable again automatically.
///  * At-least-once delivery: handlers must be idempotent, so deliverables are
///    keyed by job id and outbox events carry the job id.
///  * Priority + delayed retry: ORDER BY priority, next_attempt_at.
/// </summary>
public class JobQueue(HarnessDbContext db, TimeProvider clock)
{
    public async Task<AgentJob?> TryClaimAsync(string workerId, TimeSpan lease, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // Read a small batch of candidates, then race for one with a conditional UPDATE.
        var candidates = await db.Jobs.AsNoTracking()
            .Where(j => ((j.Status == JobStatus.Queued || j.Status == JobStatus.Failed) && j.NextAttemptAt <= now)
                     || (j.Status == JobStatus.Running && j.LeaseExpiresAt < now))
            .OrderBy(j => j.Priority).ThenBy(j => j.NextAttemptAt)
            .Select(j => new { j.Id, j.Version })
            .Take(5)
            .ToListAsync(ct);

        foreach (var c in candidates)
        {
            var newVersion = Guid.NewGuid();
            var won = await db.Jobs
                .Where(j => j.Id == c.Id && j.Version == c.Version)   // CAS on version
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, JobStatus.Running)
                    .SetProperty(j => j.LeasedBy, workerId)
                    .SetProperty(j => j.LeaseExpiresAt, now + lease)
                    .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                    .SetProperty(j => j.UpdatedAt, now)
                    .SetProperty(j => j.Version, newVersion), ct);

            if (won == 1)
                return await db.Jobs.FirstAsync(j => j.Id == c.Id, ct);
        }
        return null;
    }

    /// <summary>Heartbeat: long-running agent work extends its lease so it isn't stolen.</summary>
    public Task<int> ExtendLeaseAsync(Guid jobId, string workerId, TimeSpan lease, CancellationToken ct)
    {
        var until = clock.GetUtcNow() + lease;
        return db.Jobs.Where(j => j.Id == jobId && j.LeasedBy == workerId && j.Status == JobStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, until), ct);
    }
}
