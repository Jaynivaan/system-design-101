using System.Text.Json;
using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgencyHarness.Agents;

/// <summary>Everything a tool may touch. Tools are always scoped to the job's tenant.</summary>
public record ToolContext(HarnessDbContext Db, AgentJob Job, ClientBriefCache BriefCache, Outbox Outbox);

public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    /// <summary>JSON Schema "properties" object and required list.</summary>
    IReadOnlyDictionary<string, object> Properties { get; }
    string[] Required { get; }
    Task<string> ExecuteAsync(ToolContext ctx, JsonElement input, CancellationToken ct);
}

/// <summary>Error the model can see and recover from (returned as is_error tool_result).</summary>
public class ToolException(string message) : Exception(message);

public class ToolRegistry(IEnumerable<IAgentTool> tools)
{
    private readonly Dictionary<string, IAgentTool> _tools = tools.ToDictionary(t => t.Name);

    public IReadOnlyList<IAgentTool> For(AgentRole role) =>
        role.Tools.Where(_tools.ContainsKey).Select(n => _tools[n]).ToList();

    public IAgentTool? Find(string name) => _tools.GetValueOrDefault(name);
}

internal static class Input
{
    public static string Str(JsonElement e, string name, int maxLen = 200_000)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new ToolException($"Missing required string field '{name}'.");
        var s = v.GetString()!;
        if (s.Length > maxLen) throw new ToolException($"Field '{name}' exceeds {maxLen} characters.");
        return s;
    }

    public static string? OptStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

public class GetClientBriefTool : IAgentTool
{
    public string Name => "get_client_brief";
    public string Description => "Returns the client's brief (brand voice, audience, goals) and the project's goal. Call this before doing any work.";
    public IReadOnlyDictionary<string, object> Properties => new Dictionary<string, object>();
    public string[] Required => [];

    public async Task<string> ExecuteAsync(ToolContext ctx, JsonElement input, CancellationToken ct)
    {
        var brief = await ctx.BriefCache.GetAsync(ctx.Job.TenantId, ctx.Job.ProjectId, ct)
                    ?? throw new ToolException("Project not found.");
        return JsonSerializer.Serialize(brief, OpenAiChatAgentRunner.Json);
    }
}

public class ListDeliverablesTool : IAgentTool
{
    public string Name => "list_deliverables";
    public string Description => "Lists deliverables already saved for this project (title, kind, content preview up to 2000 chars each), newest first.";
    public IReadOnlyDictionary<string, object> Properties => new Dictionary<string, object>();
    public string[] Required => [];

    public async Task<string> ExecuteAsync(ToolContext ctx, JsonElement input, CancellationToken ct)
    {
        var items = await ctx.Db.Deliverables.AsNoTracking()
            .Where(d => d.TenantId == ctx.Job.TenantId && d.ProjectId == ctx.Job.ProjectId)
            .OrderByDescending(d => d.CreatedAt).Take(20)
            .Select(d => new { d.Title, d.Kind, d.Content })
            .ToListAsync(ct);
        return JsonSerializer.Serialize(items.Select(d => new
        {
            d.Title, d.Kind,
            preview = d.Content.Length > 2000 ? d.Content[..2000] + "…" : d.Content,
        }), OpenAiChatAgentRunner.Json);
    }
}

public class SaveDeliverableTool : IAgentTool
{
    public string Name => "save_deliverable";
    public string Description => "Saves the final work product for this job. Call once, when the work is complete. Calling again replaces the earlier version.";
    public IReadOnlyDictionary<string, object> Properties => new Dictionary<string, object>
    {
        ["title"] = new { type = "string", description = "Short title for the deliverable" },
        ["content"] = new { type = "string", description = "The full deliverable, in Markdown" },
        ["kind"] = new { type = "string", description = "One of: copy, seo, plan, analysis, review, other" },
    };
    public string[] Required => ["title", "content"];

    public async Task<string> ExecuteAsync(ToolContext ctx, JsonElement input, CancellationToken ct)
    {
        var title = Input.Str(input, "title", 300);
        var content = Input.Str(input, "content");
        var kind = Input.OptStr(input, "kind") ?? "other";

        // Idempotent under at-least-once delivery: one deliverable per job, upserted.
        var existing = await ctx.Db.Deliverables.FirstOrDefaultAsync(d => d.JobId == ctx.Job.Id, ct);
        if (existing is null)
        {
            ctx.Db.Deliverables.Add(new Deliverable
            {
                TenantId = ctx.Job.TenantId, ProjectId = ctx.Job.ProjectId, JobId = ctx.Job.Id,
                Title = title, Content = content, Kind = kind,
            });
        }
        else
        {
            existing.Title = title; existing.Content = content; existing.Kind = kind;
        }
        await ctx.Db.SaveChangesAsync(ct);
        return "Saved.";
    }
}

public class DelegateTaskTool : IAgentTool
{
    public const int MaxChildrenPerJob = 6;

    public string Name => "delegate_task";
    public string Description => "Queues a task for a specialist agent on the same project. Roles: copywriter, seo, analyst, reviewer. The task runs asynchronously; you will not see its result.";
    public IReadOnlyDictionary<string, object> Properties => new Dictionary<string, object>
    {
        ["role"] = new { type = "string", @enum = new[] { "copywriter", "seo", "analyst", "reviewer" } },
        ["instructions"] = new { type = "string", description = "Specific, self-contained instructions for the specialist" },
    };
    public string[] Required => ["role", "instructions"];

    public async Task<string> ExecuteAsync(ToolContext ctx, JsonElement input, CancellationToken ct)
    {
        var role = Input.Str(input, "role", 50).ToLowerInvariant();
        var instructions = Input.Str(input, "instructions", 20_000);
        if (!AgentRoster.TryGet(role, out _) || role == "strategist")
            throw new ToolException($"Unknown or disallowed role '{role}'.");

        // Guardrail against runaway fan-out (a delegation loop could spend the whole budget).
        var children = await ctx.Db.Jobs.CountAsync(j => j.ParentJobId == ctx.Job.Id, ct);
        if (children >= MaxChildrenPerJob)
            throw new ToolException($"Delegation limit reached ({MaxChildrenPerJob} tasks per job).");

        var child = new AgentJob
        {
            TenantId = ctx.Job.TenantId, ProjectId = ctx.Job.ProjectId, ParentJobId = ctx.Job.Id,
            AgentRole = role, Instructions = instructions, Priority = ctx.Job.Priority,
            // Deterministic idempotency key: a retried strategist run won't double-queue the same task.
            IdempotencyKey = "delegate:" + Security.Sha256($"{ctx.Job.Id}|{role}|{instructions}")[..32],
        };
        if (await ctx.Db.Jobs.AnyAsync(j => j.TenantId == child.TenantId && j.IdempotencyKey == child.IdempotencyKey, ct))
            return "Already delegated.";

        ctx.Db.Jobs.Add(child);
        ctx.Outbox.Add(ctx.Db, child.TenantId, "job.queued", new { jobId = child.Id, role, parentJobId = ctx.Job.Id });
        await ctx.Db.SaveChangesAsync(ct);
        return $"Queued {role} task {child.Id}.";
    }
}
