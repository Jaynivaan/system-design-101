using System.Text.Json;
using AgencyHarness.Infrastructure;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace AgencyHarness.Agents;

public class ClaudeOptions
{
    public string Model { get; set; } = "claude-opus-5-5";
    public int MaxTurns { get; set; } = 12;
    public int MaxTokensPerTurn { get; set; } = 16000;
    /// <summary>Server-side refusal fallback: a declined request is re-served by another model in the same call.</summary>
    public bool UseServerSideFallback { get; set; } = true;
}

/// <summary>
/// Manual agent loop over the Claude Messages API: call → run tools → append results → repeat.
/// We own the loop (rather than the SDK's tool runner) because the harness needs hooks
/// between turns: lease heartbeats, a turn cap, and per-turn cost accounting.
/// </summary>
public class ClaudeAgentRunner(AnthropicClient client, ClaudeOptions options, CircuitBreaker breaker,
    ILogger<ClaudeAgentRunner> log) : IAgentRunner
{
    public async Task<AgentRunResult> RunAsync(AgentRole role, ToolContext ctx, IReadOnlyList<IAgentTool> tools,
        Func<CancellationToken, Task> heartbeat, CancellationToken ct)
    {
        List<BetaToolUnion> toolDefs = tools.Select(t => (BetaToolUnion)new BetaTool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = new()
            {
                Properties = t.Properties.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
                Required = t.Required,
            },
        }).ToList();

        List<BetaMessageParam> messages =
        [
            new BetaUserMessageParam
            {
                Content = $"""
                    Job {ctx.Job.Id} for your role ({role.Name}).

                    <instructions>
                    {ctx.Job.Instructions}
                    </instructions>
                    """,
            },
        ];

        long inTok = 0, outTok = 0;
        decimal cost = 0;
        string servedModel = options.Model;

        for (var turn = 0; turn < options.MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            await heartbeat(ct);

            var p = new MessageCreateParams
            {
                Model = options.Model,
                MaxTokens = options.MaxTokensPerTurn,
                System = role.SystemPrompt,
                Tools = toolDefs,
                Messages = messages,
                OutputConfig = new BetaOutputConfig { Effort = ParseEffort(role.Effort) },
            };
            if (options.UseServerSideFallback)
                p = p with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };

            BetaMessage resp = await CallAsync(p, ct);

            servedModel = resp.Model;
            inTok += resp.Usage.InputTokens;
            outTok += resp.Usage.OutputTokens;
            cost += Pricing.Cost(resp.Model, resp.Usage.InputTokens, resp.Usage.OutputTokens);
            Telemetry.TokensUsed.Add(resp.Usage.InputTokens + resp.Usage.OutputTokens);

            if (resp.StopReason == "refusal")
                throw new PermanentAgentException($"Model declined the request ({resp.StopDetails?.Category}).");

            // Echo the assistant turn back verbatim (thinking signatures must be preserved).
            List<BetaContentBlockParam> assistant = [];
            List<BetaContentBlockParam> results = [];
            var text = new System.Text.StringBuilder();

            // With server-side fallback, a `fallback` block marks where another model took over.
            // Echo it in place; thinking/tool_use blocks before the last boundary must be dropped.
            var lastFallback = -1;
            for (var i = 0; i < resp.Content.Count; i++)
                if (resp.Content[i].TryPickFallback(out _)) lastFallback = i;

            for (var i = 0; i < resp.Content.Count; i++)
            {
                var block = resp.Content[i];
                var beforeBoundary = i < lastFallback;
                if (block.TryPickFallback(out var fb))
                    assistant.Add(new BetaFallbackBlockParam { From = new() { Model = fb.From.Model }, To = new() { Model = fb.To.Model } });
                else if (beforeBoundary && !block.TryPickText(out _))
                    continue;
                else if (block.TryPickText(out var t))
                {
                    assistant.Add(new BetaTextBlockParam { Text = t.Text });
                    text.Append(t.Text);
                }
                else if (block.TryPickThinking(out var th))
                    assistant.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                else if (block.TryPickRedactedThinking(out var rt))
                    assistant.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                else if (block.TryPickToolUse(out var tu))
                {
                    assistant.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                    results.Add(await RunToolAsync(ctx, tools, tu, ct));
                }
            }

            if (resp.StopReason != "tool_use" || results.Count == 0)
            {
                if (resp.StopReason == "max_tokens")
                    throw new PermanentAgentException("Response hit max_tokens before finishing.");
                return new AgentRunResult(text.ToString().Trim(), servedModel, inTok, outTok, cost);
            }

            // All tool results go back in ONE user message (keeps parallel tool use working).
            messages.Add(new BetaAssistantMessageParam { Content = assistant });
            messages.Add(new BetaUserMessageParam { Content = results });
        }

        throw new PermanentAgentException($"Agent exceeded {options.MaxTurns} turns without finishing.");
    }

    private async Task<BetaMessage> CallAsync(MessageCreateParams p, CancellationToken ct)
    {
        if (!breaker.TryAcquire()) throw new RetryableAgentException("Circuit open", new CircuitOpenException());
        try
        {
            var resp = await client.Beta.Messages.Create(p, ct);
            breaker.RecordSuccess();
            return resp;
        }
        // Most specific first: retryable upstream trouble vs. our own bad request.
        catch (Exception e) when (e is AnthropicRateLimitException or AnthropicOverloadedException
                                       or Anthropic5xxException or AnthropicIOException
                                       or AnthropicRequestTimeoutException or HttpRequestException
                                  || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            breaker.RecordFailure();
            log.LogWarning(e, "Transient model error");
            throw new RetryableAgentException(e.Message, e);
        }
        catch (Anthropic4xxException e)
        {
            throw new PermanentAgentException(e.Message, e);
        }
    }

    private static async Task<BetaToolResultBlockParam> RunToolAsync(ToolContext ctx, IReadOnlyList<IAgentTool> tools,
        BetaToolUseBlock tu, CancellationToken ct)
    {
        string content;
        bool isError = false;
        // Only tools granted to this role can run, even if the model names another one.
        var tool = tools.FirstOrDefault(t => t.Name == tu.Name);
        try
        {
            if (tool is null) throw new ToolException($"Unknown tool '{tu.Name}'.");
            var input = JsonSerializer.SerializeToElement(tu.Input);
            content = await tool.ExecuteAsync(ctx, input, ct);
        }
        catch (ToolException e)
        {
            content = e.Message;
            isError = true;
        }
        return new BetaToolResultBlockParam { ToolUseID = tu.ID, Content = content, IsError = isError };
    }

    private static Effort ParseEffort(string e) => e switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };
}
