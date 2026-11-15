using System.ComponentModel;
using AgencyHarness.Agents;
using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgencyHarness.Api;

/// <summary>
/// The harness as an MCP server, so OpenCode, Claude Code, Cursor, Claude Desktop and any other
/// MCP client can drive it. This is just another front door: it authenticates with the same
/// tenant API key (X-Api-Key or Authorization: Bearer), passes through the same per-tenant rate
/// limiter, and calls the same HarnessService as the REST API.
///
/// Tools return plain objects; the SDK serialises them as JSON text for the model.
/// </summary>
[McpServerToolType]
public class HarnessMcpTools(IHttpContextAccessor http, HarnessService svc, HarnessDbContext db)
{
    private Tenant Tenant => http.HttpContext?.Items[Auth.TenantItem] as Tenant
                             ?? throw new McpException("Not authenticated. Send your harness API key as X-Api-Key or Authorization: Bearer.");

    [McpServerTool(Name = "list_roles", ReadOnly = true), Description("Lists the agent roles you can queue jobs for (strategist, copywriter, seo, analyst, reviewer) with each role's tools.")]
    public object ListRoles() => AgentRoster.Roles.Values.Select(r => new { r.Name, r.Effort, r.Tools });

    [McpServerTool(Name = "list_clients", ReadOnly = true), Description("Lists this agency's clients with their projects (ids, names, goals) and job counts by status. Call this first to find project ids.")]
    public async Task<object> ListClients(CancellationToken ct = default)
    {
        var tid = Tenant.Id;
        var clients = await db.Clients.AsNoTracking().Where(c => c.TenantId == tid).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var projects = await db.Projects.AsNoTracking().Where(p => p.TenantId == tid).ToListAsync(ct);
        var counts = await db.Jobs.AsNoTracking().Where(j => j.TenantId == tid)
            .GroupBy(j => new { j.ProjectId, j.Status }).Select(g => new { g.Key.ProjectId, g.Key.Status, n = g.Count() }).ToListAsync(ct);
        return clients.Select(c => new
        {
            c.Id, c.Name, c.Brief,
            projects = projects.Where(p => p.ClientId == c.Id).Select(p => new
            {
                p.Id, p.Name, p.Goal,
                jobs = counts.Where(x => x.ProjectId == p.Id).ToDictionary(x => x.Status.ToString(), x => x.n),
            }),
        });
    }

    [McpServerTool(Name = "create_client"), Description("Creates a client with a brief (brand voice, audience, goals) and a first project. Returns the client and project ids.")]
    public async Task<object> CreateClient(
        [Description("Client name")] string name,
        [Description("Brief: brand voice, audience, goals")] string brief,
        [Description("Name of the first project")] string projectName,
        [Description("Project goal, ideally measurable")] string? projectGoal = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200) throw new McpException("name is required (max 200 characters).");
        var c = await svc.CreateClientAsync(Tenant, name, brief, ct);
        var p = await svc.CreateProjectAsync(Tenant, c.Id, string.IsNullOrWhiteSpace(projectName) ? "First project" : projectName, projectGoal, ct);
        return new { clientId = c.Id, projectId = p!.Id };
    }

    [McpServerTool(Name = "update_client_brief", Idempotent = true), Description("Replaces a client's brief. Agents pick up the change on their next job.")]
    public async Task<object> UpdateBrief([Description("Client id")] Guid clientId, [Description("The new brief")] string brief, CancellationToken ct = default)
    {
        var c = await svc.UpdateBriefAsync(Tenant, clientId, brief, ct) ?? throw new McpException("Client not found.");
        return new { c.Id, c.Name, updated = true };
    }

    [McpServerTool(Name = "queue_job"), Description("Queues work for an agent on a project. Returns immediately with the job id; agents run asynchronously, so poll get_job. Pass idempotencyKey to make retries safe.")]
    public async Task<object> QueueJob(
        [Description("Project id (from list_clients)")] Guid projectId,
        [Description("Agent role: strategist, copywriter, seo, analyst or reviewer")] string role,
        [Description("Specific instructions for the agent")] string instructions,
        [Description("0 = most urgent, 9 = least; default 5")] int? priority = null,
        [Description("Optional key; resending the same key and body returns the original job instead of a duplicate")] string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var r = await svc.QueueJobAsync(Tenant, projectId, role, instructions, priority, idempotencyKey, ct);
        return r.Outcome switch
        {
            QueueOutcome.Accepted => new { status = "queued", jobId = r.Job!.Id, role = r.Job.AgentRole },
            QueueOutcome.Replayed => new { status = "already_queued", jobId = r.ExistingJobId!.Value, role },
            QueueOutcome.Invalid => throw new McpException("Invalid request: " + string.Join("; ", r.Errors!.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}"))),
            _ => throw new McpException(r.Message ?? "Could not queue the job."),
        };
    }

    [McpServerTool(Name = "get_job", ReadOnly = true), Description("Gets a job's status (Queued, Running, Failed=retrying, Succeeded, DeadLettered, Cancelled), attempts, last error, summary, token use and cost.")]
    public async Task<object> GetJob([Description("Job id")] Guid jobId, CancellationToken ct = default)
    {
        var j = await svc.GetJobAsync(Tenant, jobId, ct) ?? throw new McpException("Job not found.");
        return Endpoints.JobView(j);
    }

    [McpServerTool(Name = "list_jobs", ReadOnly = true), Description("Lists the most recent jobs on a project, newest first. Optionally filter by status, e.g. DeadLettered to see what needs attention.")]
    public async Task<object> ListJobs([Description("Project id")] Guid projectId,
        [Description("Optional status filter: Queued, Running, Failed, Succeeded, DeadLettered, Cancelled")] string? status = null,
        [Description("Max results, 1-50, default 20")] int? limit = null, CancellationToken ct = default)
    {
        var tid = Tenant.Id;
        var q = db.Jobs.AsNoTracking().Where(j => j.TenantId == tid && j.ProjectId == projectId);
        if (Enum.TryParse<JobStatus>(status, true, out var s)) q = q.Where(j => j.Status == s);
        var items = await q.OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(limit ?? 20, 1, 50)).ToListAsync(ct);
        return items.Select(Endpoints.JobView);
    }

    [McpServerTool(Name = "get_deliverables", ReadOnly = true), Description("Returns the deliverables agents saved on a project (title, kind, Markdown content), newest first. Content over maxChars is cut.")]
    public async Task<object> GetDeliverables([Description("Project id")] Guid projectId,
        [Description("Max characters of content per deliverable, default 6000")] int? maxChars = null, CancellationToken ct = default)
    {
        var tid = Tenant.Id;
        var cap = Math.Clamp(maxChars ?? 6000, 200, 50_000);
        var items = await db.Deliverables.AsNoTracking().Where(d => d.TenantId == tid && d.ProjectId == projectId)
            .OrderByDescending(d => d.CreatedAt).Take(20).ToListAsync(ct);
        return items.Select(d => new
        {
            d.Id, d.JobId, d.Title, d.Kind, d.CreatedAt,
            content = d.Content.Length > cap ? d.Content[..cap] + "\n…[truncated]" : d.Content,
        });
    }

    [McpServerTool(Name = "retry_job"), Description("Moves a dead-lettered job back to the queue with its attempts reset.")]
    public async Task<object> RetryJob([Description("Job id")] Guid jobId, CancellationToken ct = default) =>
        await svc.RedriveAsync(Tenant, jobId, ct)
            ? new { jobId, status = "requeued" }
            : throw new McpException("Only dead-lettered jobs can be retried.");

    [McpServerTool(Name = "cancel_job", Destructive = true), Description("Cancels a job that is queued or waiting to retry. Running jobs can't be cancelled.")]
    public async Task<object> CancelJob([Description("Job id")] Guid jobId, CancellationToken ct = default) =>
        await svc.CancelAsync(Tenant, jobId, ct)
            ? new { jobId, status = "cancelled" }
            : throw new McpException("Job not found, or it's not queued or retrying.");

    [McpServerTool(Name = "get_usage", ReadOnly = true), Description("Returns this month's spend, the monthly budget, and token totals for the agency.")]
    public async Task<object> GetUsage(TimeProvider clock, CancellationToken ct = default)
    {
        var t = Tenant;
        var now = clock.GetUtcNow();
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = await db.Usage.AsNoTracking().Where(u => u.TenantId == t.Id && u.CreatedAt >= start).ToListAsync(ct);
        return new
        {
            month = $"{now:yyyy-MM}", jobs = rows.Count, costUsd = rows.Sum(r => r.CostUsd), budgetUsd = t.MonthlyBudgetUsd,
            inputTokens = rows.Sum(r => r.InputTokens), outputTokens = rows.Sum(r => r.OutputTokens),
        };
    }
}
