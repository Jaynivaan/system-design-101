using System.Text.Json;
using AgencyHarness.Infrastructure;

namespace AgencyHarness.Agents;

public record AgentRunResult(string Summary, string Model, long InputTokens, long OutputTokens, decimal CostUsd);

/// <summary>Transient failure (rate limit, 5xx, network, open circuit): retry with backoff.</summary>
public class RetryableAgentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Permanent failure (bad request, refusal, budget exceeded): dead-letter for a human.</summary>
public class PermanentAgentException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The seam between the harness and the model. The worker owns queueing, retries,
/// leases, budgets and events; the runner owns one agent conversation.
/// </summary>
public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(AgentRole role, ToolContext ctx, IReadOnlyList<IAgentTool> tools,
        Func<CancellationToken, Task> heartbeat, CancellationToken ct);
}

/// <summary>Per-million-token prices used for metering. Keep in config in production.</summary>
public static class Pricing
{
    private static readonly Dictionary<string, (decimal In, decimal Out)> PerMillion = new()
    {
        ["claude-opus-5-5"] = (4m, 20m),
        ["claude-sonnet-5-5"] = (2m, 10m),
        ["claude-haiku-5-5"] = (0.10m, 0.50m),
        ["claude-opus-4-8"] = (5m, 25m),
        ["scripted"] = (0m, 0m),
    };

    public static decimal Cost(string model, long input, long output)
    {
        var p = PerMillion.TryGetValue(model, out var v) ? v : (In: 4m, Out: 20m);
        return (input * p.In + output * p.Out) / 1_000_000m;
    }
}

/// <summary>
/// Offline runner used when no ANTHROPIC_API_KEY is configured (local dev, CI, demos).
/// It drives the real tools, so the whole pipeline (queue, tools, deliverables,
/// outbox, webhooks, metering) is exercised without network calls.
/// </summary>
public class ScriptedAgentRunner : IAgentRunner
{
    public async Task<AgentRunResult> RunAsync(AgentRole role, ToolContext ctx, IReadOnlyList<IAgentTool> tools,
        Func<CancellationToken, Task> heartbeat, CancellationToken ct)
    {
        IAgentTool Tool(string n) => tools.First(t => t.Name == n);
        var briefJson = await Tool("get_client_brief").ExecuteAsync(ctx, default, ct);
        var b = JsonSerializer.Deserialize<ClientBrief>(briefJson)!;
        var brief = $"{b.Client}: {b.Brief}\n\n**Project:** {b.Project}. {b.ProjectGoal}";
        await heartbeat(ct);

        if (role.Name == "strategist")
        {
            foreach (var r in new[] { "copywriter", "seo" })
            {
                var input = JsonSerializer.SerializeToElement(new { role = r, instructions = $"[{r}] {ctx.Job.Instructions}" });
                await Tool("delegate_task").ExecuteAsync(ctx, input, ct);
            }
        }

        var content = $"""
            # {role.Name} draft (scripted runner)

            **Task:** {ctx.Job.Instructions}

            **Client:** {brief}

            _This is a placeholder from the scripted runner. Connect a model (Harness:Models: a local model, Gemini, or your OpenCode models) to have a real agent write it._
            """;
        await Tool("save_deliverable").ExecuteAsync(ctx,
            JsonSerializer.SerializeToElement(new { title = $"{role.Name}: {Trim(ctx.Job.Instructions, 60)}", content, kind = "other" }), ct);

        return new AgentRunResult($"Scripted {role.Name} run complete.", "scripted", 0, 0, 0m);
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
