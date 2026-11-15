using System.Text.Json;
using System.Text.RegularExpressions;
using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgencyHarness.Agents;

/// <summary>Planner tool: adds one node to the agenda's task graph. Dependencies must already exist, so the graph can't contain a cycle.</summary>
public partial class AddAgendaTaskTool : IAgentTool
{
    public const int MaxTasks = 25;
    public static readonly string[] WorkerRoles = ["strategist", "copywriter", "seo", "geo", "techseo", "content", "outreach", "analyst"];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,48}$")] private static partial Regex KeyPattern();

    public string Name => "add_agenda_task";
    public string Description => "Adds a task to the agenda being planned. Add a task only after the tasks it depends on. Returns the task count.";
    public IReadOnlyDictionary<string, object> Properties => new Dictionary<string, object>
    {
        ["key"] = new { type = "string", description = "Short kebab-case id, e.g. sitemap-audit" },
        ["title"] = new { type = "string", description = "Short human-readable title" },
        ["role"] = new { type = "string", @enum = WorkerRoles },
        ["instructions"] = new { type = "string", description = "Self-contained instructions: what to produce, for whom, in what format" },
        ["depends_on"] = new { type = "array", items = new { type = "string" }, description = "Keys of tasks that must finish first (already added)" },
        ["phase"] = new { type = "string", description = "Phase name from the plan, e.g. 'Days 1–7: Technical & indexing'" },
    };
    public string[] Required => ["key", "title", "role", "instructions"];

    public async Task<string> ExecuteAsync(ToolContext ctx, JsonElement input, CancellationToken ct)
    {
        var agenda = await ctx.Db.Agendas.FirstOrDefaultAsync(a => a.PlannerJobId == ctx.Job.Id, ct)
                     ?? throw new ToolException("This job isn't planning an agenda.");
        var key = Input.Str(input, "key", 50).Trim().ToLowerInvariant();
        if (!KeyPattern().IsMatch(key)) throw new ToolException("key must be 2-50 characters of lowercase letters, digits and hyphens.");
        var role = Input.Str(input, "role", 30).Trim().ToLowerInvariant();
        if (!WorkerRoles.Contains(role)) throw new ToolException($"role must be one of: {string.Join(", ", WorkerRoles)}.");
        var existing = await ctx.Db.AgendaTasks.Where(t => t.AgendaId == agenda.Id).Select(t => t.Key).ToListAsync(ct);
        if (existing.Contains(key)) return $"Task '{key}' already exists ({existing.Count} tasks).";   // idempotent on retries
        if (existing.Count >= MaxTasks) throw new ToolException($"An agenda can have at most {MaxTasks} tasks.");

        var deps = new List<string>();
        if (input.TryGetProperty("depends_on", out var d))
        {
            if (d.ValueKind == JsonValueKind.Array) deps.AddRange(d.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
            else if (d.ValueKind == JsonValueKind.String) deps.AddRange((d.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        deps = deps.Select(x => x.Trim().ToLowerInvariant()).Distinct().ToList();
        var missing = deps.Where(x => !existing.Contains(x)).ToList();
        if (missing.Count > 0) throw new ToolException($"Unknown dependency: {string.Join(", ", missing)}. Add those tasks first.");

        ctx.Db.AgendaTasks.Add(new AgendaTask
        {
            TenantId = agenda.TenantId, AgendaId = agenda.Id, Key = key, Role = role,
            Title = Input.Str(input, "title", 160).Trim(),
            Instructions = Input.Str(input, "instructions", 6000).Trim(),
            DependsOn = string.Join(",", deps),
            Phase = (Input.OptStr(input, "phase") ?? "Tasks").Trim() is { Length: > 0 } ph ? (ph.Length > 80 ? ph[..80] : ph) : "Tasks",
            Order = existing.Count,
        });
        await ctx.Db.SaveChangesAsync(ct);
        return $"Added '{key}' ({existing.Count + 1} tasks).";
    }
}

public class ListAgendaTasksTool : IAgentTool
{
    public string Name => "list_agenda_tasks";
    public string Description => "Lists the tasks added so far to the agenda being planned (key, title, role, phase, dependencies).";
    public IReadOnlyDictionary<string, object> Properties => new Dictionary<string, object>();
    public string[] Required => [];

    public async Task<string> ExecuteAsync(ToolContext ctx, JsonElement input, CancellationToken ct)
    {
        var agenda = await ctx.Db.Agendas.AsNoTracking().FirstOrDefaultAsync(a => a.PlannerJobId == ctx.Job.Id, ct)
                     ?? throw new ToolException("This job isn't planning an agenda.");
        var tasks = await ctx.Db.AgendaTasks.AsNoTracking().Where(t => t.AgendaId == agenda.Id).OrderBy(t => t.Order)
            .Select(t => new { t.Key, t.Title, t.Role, t.Phase, t.DependsOn }).ToListAsync(ct);
        return JsonSerializer.Serialize(tasks, OpenAiChatAgentRunner.Json);
    }
}

/// <summary>Reviewer tool: records a verdict on an agenda task, plus lessons that prevent the same mistake next time.</summary>
public class SubmitReviewTool : IAgentTool
{
    public const int PassScore = 7;

    public string Name => "submit_review";
    public string Description => "Records your review of the agenda task in your instructions: score 1-10 (pass at 7+), specific issues to fix, and up to 3 reusable lessons.";
    public IReadOnlyDictionary<string, object> Properties => new Dictionary<string, object>
    {
        ["score"] = new { type = "integer", minimum = 1, maximum = 10 },
        ["issues"] = new { type = "array", items = new { type = "string" }, description = "Specific problems and how to fix them" },
        ["lessons"] = new { type = "array", items = new { type = "string" }, description = "0-3 short, reusable rules that would prevent these mistakes in future" },
    };
    public string[] Required => ["score"];

    public async Task<string> ExecuteAsync(ToolContext ctx, JsonElement input, CancellationToken ct)
    {
        var task = await ctx.Db.AgendaTasks.FirstOrDefaultAsync(t => t.ReviewJobId == ctx.Job.Id, ct)
                   ?? throw new ToolException("This job isn't reviewing an agenda task; save a review deliverable instead.");
        int score = input.TryGetProperty("score", out var s) && s.TryGetInt32(out var v) ? Math.Clamp(v, 1, 10) : 5;
        var issues = Strings(input, "issues").Take(8).ToList();
        task.QualityScore = score;
        task.QualityNotes = (score >= PassScore ? "Review passed" : "Review asked for changes") + $" ({score}/10)" +
                            (issues.Count > 0 ? ": " + string.Join(" • ", issues) : ".");
        task.UpdatedAt = DateTimeOffset.UtcNow;
        await ctx.Db.SaveChangesAsync(ct);

        var clientId = await ctx.Db.Projects.Where(p => p.Id == ctx.Job.ProjectId).Select(p => (Guid?)p.ClientId).FirstOrDefaultAsync(ct);
        foreach (var lesson in Strings(input, "lessons").Take(3))
            await SkillsAndLessons.LearnAsync(ctx.Db, ctx.Outbox, ctx.Job.TenantId, clientId, task.Role, lesson, "review", task.JobId, ct);
        return score >= PassScore ? "Recorded: passed." : "Recorded: changes requested.";
    }

    private static IEnumerable<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0)
            : [];
}
