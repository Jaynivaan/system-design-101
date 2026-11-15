using AgencyHarness.Agents;
using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgencyHarness.Api;

public enum QueueOutcome { Accepted, Replayed, Invalid, NotFound, KeyConflict, KeyInFlight }

public record QueueResult(QueueOutcome Outcome, AgentJob? Job = null, Guid? ExistingJobId = null,
    IDictionary<string, string[]>? Errors = null, string? Message = null);

/// <summary>
/// Application service shared by every front door: REST (/v1), MCP (/mcp), and anything added later.
/// Each front door only translates its protocol; validation, tenant scoping, idempotency and the
/// transactional outbox live here once (SD101: one source of truth behind the API gateway).
/// </summary>
public class HarnessService(HarnessDbContext db, Outbox outbox, ClientBriefCache briefs, TimeProvider clock)
{
    public async Task<QueueResult> QueueJobAsync(Tenant tenant, Guid projectId, string? role, string? instructions,
        int? priority, string? idempotencyKey, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string[]>();
        if (!AgentRoster.TryGet(role ?? "", out _)) errors["role"] = [$"Must be one of: {string.Join(", ", AgentRoster.Roles.Keys)}."];
        if (string.IsNullOrWhiteSpace(instructions)) errors["instructions"] = ["Required."];
        else if (instructions.Length > 20_000) errors["instructions"] = ["Max 20000 characters."];
        if (idempotencyKey is { Length: > 128 }) errors["idempotencyKey"] = ["Max 128 characters."];
        if (errors.Count > 0) return new(QueueOutcome.Invalid, Errors: errors);

        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.TenantId == tenant.Id, ct))
            return new(QueueOutcome.NotFound, Message: "Project not found.");

        // Same key + same body → same job; same key + different body → conflict.
        var requestHash = Security.Sha256($"{projectId}|{role}|{instructions}|{priority}");
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var prior = await db.IdempotencyKeys.AsNoTracking()
                .FirstOrDefaultAsync(i => i.TenantId == tenant.Id && i.Key == idempotencyKey, ct);
            if (prior is not null)
                return prior.RequestHash == requestHash
                    ? new(QueueOutcome.Replayed, ExistingJobId: prior.ResourceId)
                    : new(QueueOutcome.KeyConflict, Message: "Idempotency-Key reused with a different request body.");
        }

        var job = new AgentJob
        {
            TenantId = tenant.Id, ProjectId = projectId, AgentRole = role!.ToLowerInvariant(),
            Instructions = instructions!, Priority = Math.Clamp(priority ?? 5, 0, 9), IdempotencyKey = idempotencyKey,
        };
        db.Jobs.Add(job);
        if (!string.IsNullOrEmpty(idempotencyKey))
            db.IdempotencyKeys.Add(new IdempotencyRecord { TenantId = tenant.Id, Key = idempotencyKey, RequestHash = requestHash, ResourceId = job.Id });
        outbox.Add(db, tenant.Id, "job.queued", new { jobId = job.Id, projectId, role = job.AgentRole });

        try
        {
            await db.SaveChangesAsync(ct);   // job + idempotency record + event commit atomically
        }
        catch (DbUpdateException) when (!string.IsNullOrEmpty(idempotencyKey))
        {
            return new(QueueOutcome.KeyInFlight, Message: "A request with this Idempotency-Key is in progress; retry shortly.");
        }
        return new(QueueOutcome.Accepted, job);
    }

    public async Task<Client> CreateClientAsync(Tenant tenant, string name, string? brief, CancellationToken ct = default)
    {
        var c = new Client { TenantId = tenant.Id, Name = name.Trim(), Brief = brief ?? "" };
        db.Clients.Add(c);
        await db.SaveChangesAsync(ct);
        return c;
    }

    public async Task<Client?> UpdateBriefAsync(Tenant tenant, Guid clientId, string brief, CancellationToken ct = default)
    {
        var c = await db.Clients.FirstOrDefaultAsync(x => x.Id == clientId && x.TenantId == tenant.Id, ct);
        if (c is null) return null;
        c.Brief = brief;
        await db.SaveChangesAsync(ct);
        await briefs.InvalidateClientAsync(db, tenant.Id, clientId, ct);   // write, then invalidate
        return c;
    }

    public async Task<Project?> CreateProjectAsync(Tenant tenant, Guid clientId, string name, string? goal, CancellationToken ct = default)
    {
        if (!await db.Clients.AnyAsync(c => c.Id == clientId && c.TenantId == tenant.Id, ct)) return null;
        var p = new Project { TenantId = tenant.Id, ClientId = clientId, Name = name, Goal = goal ?? "" };
        db.Projects.Add(p);
        await db.SaveChangesAsync(ct);
        return p;
    }

    public Task<AgentJob?> GetJobAsync(Tenant tenant, Guid jobId, CancellationToken ct = default) =>
        db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.TenantId == tenant.Id, ct);

    public async Task<bool> RedriveAsync(Tenant tenant, Guid jobId, CancellationToken ct = default)
    {
        var newVersion = Guid.NewGuid();
        var n = await db.Jobs.Where(j => j.Id == jobId && j.TenantId == tenant.Id && j.Status == JobStatus.DeadLettered)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued)
                                      .SetProperty(j => j.Attempts, 0)
                                      .SetProperty(j => j.NextAttemptAt, clock.GetUtcNow())
                                      .SetProperty(j => j.CompletedAt, (DateTimeOffset?)null)
                                      .SetProperty(j => j.Version, newVersion), ct);
        return n == 1;
    }

    public async Task<bool> CancelAsync(Tenant tenant, Guid jobId, CancellationToken ct = default)
    {
        var newVersion = Guid.NewGuid();
        var n = await db.Jobs.Where(j => j.Id == jobId && j.TenantId == tenant.Id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Failed))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled)
                                      .SetProperty(j => j.UpdatedAt, clock.GetUtcNow())
                                      .SetProperty(j => j.Version, newVersion), ct);
        return n == 1;
    }
}
