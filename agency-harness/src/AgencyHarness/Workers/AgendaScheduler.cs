using System.Text;
using AgencyHarness.Agents;
using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgencyHarness.Workers;

public class AgendaOptions
{
    public TimeSpan Tick { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>How many times a task is redone after failing the quality gate or review.</summary>
    public int MaxRevisions { get; set; } = 2;
    public int MaxSwarmSize { get; set; } = 8;
}

/// <summary>
/// Runs agendas as task graphs (DAGs) over the ordinary job queue.
///
/// Each tick, per agenda:
///   planning → queue the planner once; when it finishes, start running the graph.
///   running  → advance tasks whose job finished (gate → review → done, or revise), block tasks whose
///              dependency failed, then launch ready tasks up to the agenda's swarm size.
///
/// Design: the scheduler owns no in-memory state; every decision is re-derived from the database, so a
/// restart or crash just resumes. State changes are compare-and-swap updates, and every job it queues has
/// a deterministic idempotency key (agenda:task:revision), so a double tick can't double-launch work.
/// The swarm size is a bulkhead that keeps one agenda from flooding the workers or the model quota.
/// </summary>
public class AgendaScheduler(IServiceScopeFactory scopes, Outbox outbox, QualityGate gate, AgendaOptions o,
    TimeProvider clock, ILogger<AgendaScheduler> log)
{
    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarnessDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<Api.HarnessService>();
        var agendas = await db.Agendas.Where(a => a.Status == AgendaStatus.Planning || a.Status == AgendaStatus.Running || a.Status == AgendaStatus.Paused)
            .ToListAsync(ct);
        foreach (var agenda in agendas)
        {
            try { await AdvanceAsync(db, svc, agenda, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Agenda {Agenda} tick failed", agenda.Id);   // one bad agenda never stops the others
            }
        }
    }

    private async Task AdvanceAsync(HarnessDbContext db, Api.HarnessService svc, Agenda agenda, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == agenda.TenantId, ct);

        if (agenda.Status == AgendaStatus.Planning)
        {
            if (agenda.PlannerJobId is null)
            {
                var r = await svc.QueueJobAsync(tenant, agenda.ProjectId, "planner",
                    $"Plan the agenda \"{agenda.Title}\" (swarm size {agenda.SwarmSize}).\n\n<plan>\n{agenda.Plan}\n</plan>", 3, $"agenda:{agenda.Id}:planner", ct);
                agenda.PlannerJobId = r.Job?.Id ?? r.ExistingJobId;
                Touch(agenda);
                await db.SaveChangesAsync(ct);
                return;
            }
            var planner = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == agenda.PlannerJobId, ct);
            if (planner.Status == JobStatus.Succeeded)
            {
                var count = await db.AgendaTasks.CountAsync(t => t.AgendaId == agenda.Id, ct);
                agenda.Status = count > 0 ? AgendaStatus.Running : AgendaStatus.Failed;
                agenda.LastError = count > 0 ? null : "The planner didn't create any tasks.";
                Touch(agenda);
                outbox.Add(db, agenda.TenantId, count > 0 ? "agenda.running" : "agenda.failed", new { agendaId = agenda.Id, tasks = count, agenda.LastError });
                await db.SaveChangesAsync(ct);
            }
            else if (planner.Status is JobStatus.DeadLettered or JobStatus.Cancelled)
            {
                agenda.Status = AgendaStatus.Failed;
                agenda.LastError = "Planning failed: " + planner.LastError;
                Touch(agenda);
                outbox.Add(db, agenda.TenantId, "agenda.failed", new { agendaId = agenda.Id, agenda.LastError });
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        var tasks = await db.AgendaTasks.Where(t => t.AgendaId == agenda.Id).OrderBy(t => t.Order).ToListAsync(ct);
        var jobIds = tasks.SelectMany(t => new[] { t.JobId, t.ReviewJobId }).OfType<Guid>().ToList();
        var jobs = await db.Jobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, ct);

        // 1. Advance tasks whose work or review finished.
        foreach (var t in tasks.Where(t => t.Status == AgendaTaskStatus.Running && t.JobId is { } id && jobs.ContainsKey(id)))
        {
            var job = jobs[t.JobId!.Value];
            if (job.Status == JobStatus.Succeeded)
            {
                var check = await gate.CheckAsync(db, job, ct);
                t.Summary = job.Output;
                if (!check.Pass && t.Revisions < o.MaxRevisions)
                    await ReviseAsync(db, svc, tenant, agenda, t, "the automatic quality checks", check.Issues.Select(i => i.Problem), ct);
                else if (agenda.ReviewGate && !check.Placeholder)
                {
                    t.QualityNotes = check.Notes;
                    await StartReviewAsync(db, svc, tenant, agenda, t, job, ct);
                }
                else Complete(db, agenda, t, check.Pass ? check.Notes : "Accepted with unresolved issues: " + check.Notes);
            }
            else if (job.Status is JobStatus.DeadLettered or JobStatus.Cancelled)
            {
                t.Status = AgendaTaskStatus.Failed;
                t.QualityNotes = "The job failed: " + job.LastError;
                Touch(t);
                outbox.Add(db, agenda.TenantId, "agenda.task.failed", new { agendaId = agenda.Id, task = t.Key, error = job.LastError });
            }
        }
        foreach (var t in tasks.Where(t => t.Status == AgendaTaskStatus.Reviewing && t.ReviewJobId is { } id && jobs.ContainsKey(id)))
        {
            var review = jobs[t.ReviewJobId!.Value];
            if (review.Status == JobStatus.Succeeded)
            {
                var passed = t.QualityScore is null or >= SubmitReviewTool.PassScore;   // no verdict recorded = don't block on the reviewer
                if (!passed && t.Revisions < o.MaxRevisions)
                    await ReviseAsync(db, svc, tenant, agenda, t, "the reviewer", [t.QualityNotes ?? "The reviewer asked for changes."], ct);
                else Complete(db, agenda, t, t.QualityNotes ?? "Reviewed.");
            }
            else if (review.Status is JobStatus.DeadLettered or JobStatus.Cancelled)
                Complete(db, agenda, t, (t.QualityNotes ?? "") + " (Review unavailable; accepted after automatic checks.)");
        }

        // 2. A failed or blocked dependency blocks everything downstream.
        var byKey = tasks.ToDictionary(t => t.Key);
        bool changed;
        do
        {
            changed = false;
            foreach (var t in tasks.Where(t => t.Status == AgendaTaskStatus.Pending))
                if (t.Dependencies.Any(d => byKey.TryGetValue(d, out var dep) && dep.Status is AgendaTaskStatus.Failed or AgendaTaskStatus.Blocked))
                {
                    t.Status = AgendaTaskStatus.Blocked; Touch(t); changed = true;
                }
        } while (changed);

        // 3. Launch ready tasks, up to the swarm size.
        if (agenda.Status == AgendaStatus.Running)
        {
            var active = tasks.Count(t => t.Status is AgendaTaskStatus.Running or AgendaTaskStatus.Reviewing);
            var ready = tasks.Where(t => t.Status == AgendaTaskStatus.Pending &&
                                         t.Dependencies.All(d => byKey.TryGetValue(d, out var dep) && dep.Status == AgendaTaskStatus.Done)).ToList();
            foreach (var t in ready.Take(Math.Max(0, Math.Min(agenda.SwarmSize, o.MaxSwarmSize) - active)))
            {
                var r = await svc.QueueJobAsync(tenant, agenda.ProjectId, t.Role, WorkInstructions(agenda, t, byKey), 5,
                    $"agenda:{t.Id}:r{t.Revisions}", ct);
                t.JobId = r.Job?.Id ?? r.ExistingJobId;
                if (t.JobId is null)
                {
                    t.Status = AgendaTaskStatus.Failed;
                    t.QualityNotes = "Couldn't queue the task: " + (r.Message ?? string.Join("; ", r.Errors?.SelectMany(e => e.Value) ?? []));
                    Touch(t);
                    continue;
                }
                t.Status = AgendaTaskStatus.Running;
                Touch(t);
                outbox.Add(db, agenda.TenantId, "agenda.task.started", new { agendaId = agenda.Id, task = t.Key, role = t.Role, jobId = t.JobId });
            }
        }

        // 4. Finished?
        if (tasks.Count > 0 && tasks.All(t => t.Status is AgendaTaskStatus.Done or AgendaTaskStatus.Failed or AgendaTaskStatus.Blocked))
        {
            var failed = tasks.Count(t => t.Status != AgendaTaskStatus.Done);
            agenda.Status = AgendaStatus.Done;
            agenda.CompletedAt = clock.GetUtcNow();
            agenda.LastError = failed > 0 ? $"{failed} of {tasks.Count} tasks didn't complete." : null;
            outbox.Add(db, agenda.TenantId, "agenda.done", new { agendaId = agenda.Id, done = tasks.Count - failed, failed });
        }
        Touch(agenda);
        await db.SaveChangesAsync(ct);
    }

    private void Complete(HarnessDbContext db, Agenda agenda, AgendaTask t, string notes)
    {
        t.Status = AgendaTaskStatus.Done;
        t.QualityNotes = notes;
        t.CompletedAt = clock.GetUtcNow();
        Touch(t);
        outbox.Add(db, agenda.TenantId, "agenda.task.done", new { agendaId = agenda.Id, task = t.Key, t.QualityScore, revisions = t.Revisions });
    }

    private async Task ReviseAsync(HarnessDbContext db, Api.HarnessService svc, Tenant tenant, Agenda agenda, AgendaTask t, string by,
        IEnumerable<string> problems, CancellationToken ct)
    {
        t.Revisions++;
        var list = problems.ToList();
        t.QualityNotes = $"Revision {t.Revisions} requested by {by}: " + string.Join(" • ", list);
        var byKey = (await db.AgendaTasks.AsNoTracking().Where(x => x.AgendaId == agenda.Id).ToListAsync(ct)).ToDictionary(x => x.Key);
        var instructions = WorkInstructions(agenda, t, byKey) +
                           $"\n\n## This is revision {t.Revisions}. Fix these problems found by {by}:\n" + string.Join("\n", list.Select(p => "- " + p));
        var r = await svc.QueueJobAsync(tenant, agenda.ProjectId, t.Role, instructions, 4, $"agenda:{t.Id}:r{t.Revisions}", ct);
        t.JobId = r.Job?.Id ?? r.ExistingJobId;
        t.ReviewJobId = null;
        t.QualityScore = null;
        t.Status = AgendaTaskStatus.Running;
        Touch(t);
        outbox.Add(db, agenda.TenantId, "agenda.task.revising", new { agendaId = agenda.Id, task = t.Key, revision = t.Revisions, by, problems = list });
    }

    private async Task StartReviewAsync(HarnessDbContext db, Api.HarnessService svc, Tenant tenant, Agenda agenda, AgendaTask t, AgentJob job, CancellationToken ct)
    {
        var work = await db.Deliverables.AsNoTracking().Where(d => d.JobId == job.Id).Select(d => d.Content).FirstOrDefaultAsync(ct) ?? job.Output ?? "";
        if (work.Length > 12_000) work = work[..12_000] + "\n…[truncated]";
        var instructions = $"""
            Review agenda task "{t.Title}" ({t.Role}) for the agenda "{agenda.Title}".

            <task_instructions>
            {t.Instructions}
            </task_instructions>

            <work>
            {work}
            </work>

            Call submit_review with a score (pass at {SubmitReviewTool.PassScore}+), specific issues, and up to 3 reusable lessons.
            """;
        var r = await svc.QueueJobAsync(tenant, agenda.ProjectId, "reviewer", instructions, 4, $"agenda:{t.Id}:review{t.Revisions}", ct);
        t.ReviewJobId = r.Job?.Id ?? r.ExistingJobId;
        t.Status = AgendaTaskStatus.Reviewing;
        Touch(t);
        outbox.Add(db, agenda.TenantId, "agenda.task.reviewing", new { agendaId = agenda.Id, task = t.Key });
    }

    /// <summary>Each task gets the agenda's goal plus what its dependencies produced.</summary>
    private static string WorkInstructions(Agenda agenda, AgendaTask t, IReadOnlyDictionary<string, AgendaTask> byKey)
    {
        var sb = new StringBuilder();
        sb.Append($"Agenda: {agenda.Title}\nPhase: {t.Phase}\nTask: {t.Title}\n\n{t.Instructions}");
        var deps = t.Dependencies.Where(byKey.ContainsKey).Select(d => byKey[d]).ToList();
        if (deps.Count > 0)
        {
            sb.Append("\n\n## Builds on (finished tasks — use list_deliverables to read their full work)\n");
            foreach (var d in deps) sb.Append($"- {d.Title} ({d.Role}): {Trim(d.Summary)}\n");
        }
        return sb.ToString();
    }

    private static string Trim(string? s) => string.IsNullOrEmpty(s) ? "done" : s.Length > 400 ? s[..400] + "…" : s;
    private void Touch(Agenda a) => a.UpdatedAt = clock.GetUtcNow();
    private void Touch(AgendaTask t) => t.UpdatedAt = clock.GetUtcNow();
}

public class AgendaWorker(AgendaScheduler scheduler, AgendaOptions o, ILogger<AgendaWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await scheduler.TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogError(e, "Agenda scheduler tick failed"); }
            try { await Task.Delay(o.Tick, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
