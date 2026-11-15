using System.Diagnostics;
using AgencyHarness.Agents;
using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgencyHarness.Workers;

public class WorkerOptions
{
    public int Concurrency { get; set; } = 4;
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan IdlePoll { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryBase { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan RetryMax { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Processes one job end to end. Kept separate from the BackgroundService so tests
/// can drive it deterministically.
/// </summary>
public class JobProcessor(IServiceScopeFactory scopes, IAgentRunner runner, ToolRegistry registry,
    ClientBriefCache briefs, Outbox outbox, WorkerOptions opts, TimeProvider clock, ILogger<JobProcessor> log)
{
    public async Task<bool> ProcessNextAsync(string workerId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarnessDbContext>();
        var queue = new JobQueue(db, clock);

        var job = await queue.TryClaimAsync(workerId, opts.Lease, ct);
        if (job is null) return false;

        using var _ = log.BeginScope("job={JobId} tenant={TenantId} role={Role} attempt={Attempt}",
            job.Id, job.TenantId, job.AgentRole, job.Attempts);
        var sw = Stopwatch.StartNew();

        try
        {
            if (!AgentRoster.TryGet(job.AgentRole, out var role))
                throw new PermanentAgentException($"Unknown agent role '{job.AgentRole}'.");

            await EnforceBudgetAsync(db, job, ct);

            var ctx = new ToolContext(db, job, briefs, outbox);
            var result = await runner.RunAsync(role, ctx, registry.For(role),
                hbCt => queue.ExtendLeaseAsync(job.Id, workerId, opts.Lease, hbCt), ct);

            var now = clock.GetUtcNow();
            job.Status = JobStatus.Succeeded;
            job.Output = result.Summary;
            job.InputTokens += result.InputTokens;
            job.OutputTokens += result.OutputTokens;
            job.CostUsd += result.CostUsd;
            job.LastError = null;
            Release(job, now);
            job.CompletedAt = now;

            db.Usage.Add(new UsageRecord
            {
                TenantId = job.TenantId, JobId = job.Id, Model = result.Model,
                InputTokens = result.InputTokens, OutputTokens = result.OutputTokens, CostUsd = result.CostUsd,
            });
            outbox.Add(db, job.TenantId, "job.succeeded", new { jobId = job.Id, job.ProjectId, role = job.AgentRole, summary = result.Summary, costUsd = result.CostUsd });
            await SaveFencedAsync(db, job, ct);

            Telemetry.JobsCompleted.Add(1, new KeyValuePair<string, object?>("role", job.AgentRole));
            log.LogInformation("Job succeeded in {Ms} ms, cost ${Cost}", sw.ElapsedMilliseconds, result.CostUsd);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: leave the job leased; the lease expires and another worker picks it up.
            throw;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost the lease mid-run (fenced out). The new owner will finish the job.
        }
        catch (PermanentAgentException e)
        {
            await FailAsync(db, job, e.Message, retryable: false, ct);
        }
        catch (Exception e)
        {
            // Unknown errors are treated as transient: retry with backoff, then dead-letter.
            await FailAsync(db, job, e.Message, retryable: true, ct);
        }
        finally
        {
            Telemetry.JobDurationMs.Record(sw.Elapsed.TotalMilliseconds);
        }
        return true;
    }

    private async Task EnforceBudgetAsync(HarnessDbContext db, AgentJob job, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == job.TenantId, ct);
        var spent = (await db.Usage.AsNoTracking()
                .Where(u => u.TenantId == job.TenantId && u.CreatedAt >= monthStart)
                .Select(u => u.CostUsd).ToListAsync(ct)).Sum();
        if (spent >= tenant.MonthlyBudgetUsd)
            throw new PermanentAgentException($"Monthly budget of ${tenant.MonthlyBudgetUsd} reached (spent ${spent:0.00}).");
    }

    private async Task FailAsync(HarnessDbContext db, AgentJob job, string error, bool retryable, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        job.LastError = error.Length > 2000 ? error[..2000] : error;
        Release(job, now);

        if (retryable && job.Attempts < job.MaxAttempts)
        {
            job.Status = JobStatus.Failed;
            job.NextAttemptAt = now + Backoff.Compute(job.Attempts, opts.RetryBase, opts.RetryMax);
            Telemetry.JobsFailed.Add(1);
            log.LogWarning("Job failed (attempt {Attempt}/{Max}), retrying at {At}: {Error}",
                job.Attempts, job.MaxAttempts, job.NextAttemptAt, error);
        }
        else
        {
            job.Status = JobStatus.DeadLettered;
            job.CompletedAt = now;
            outbox.Add(db, job.TenantId, "job.dead_lettered", new { jobId = job.Id, job.ProjectId, role = job.AgentRole, error = job.LastError });
            Telemetry.JobsDeadLettered.Add(1);
            log.LogError("Job dead-lettered: {Error}", error);
        }

        try { await SaveFencedAsync(db, job, ct); }
        catch (DbUpdateConcurrencyException) { /* lease was lost; the new owner decides */ }
    }

    private static void Release(AgentJob job, DateTimeOffset now)
    {
        job.LeasedBy = null;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = now;
    }

    /// <summary>
    /// Fencing: the job row carries a version token set at claim time. If our lease
    /// expired and another worker re-claimed the job, the version changed and this
    /// save fails, so a "zombie" worker can never overwrite the new owner's result.
    /// </summary>
    private async Task SaveFencedAsync(HarnessDbContext db, AgentJob job, CancellationToken ct)
    {
        job.Version = Guid.NewGuid();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            log.LogWarning("Lost the lease on job {JobId}; discarding this worker's result", job.Id);
            throw;
        }
    }
}

/// <summary>Runs N competing consumers in-process. Scale out by running more instances.</summary>
public class AgentWorker(JobProcessor processor, WorkerOptions opts, ILogger<AgentWorker> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, opts.Concurrency).Select(i => LoopAsync($"{Environment.MachineName}-{i}", stoppingToken)));

    private async Task LoopAsync(string workerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await processor.ProcessNextAsync(workerId, ct))
                    await Task.Delay(opts.IdlePoll, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e)
            {
                log.LogError(e, "Worker {Worker} loop error", workerId);
                await Task.Delay(opts.IdlePoll, ct).ContinueWith(_ => { });
            }
        }
    }
}
