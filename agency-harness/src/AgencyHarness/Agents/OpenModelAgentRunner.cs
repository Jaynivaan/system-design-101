using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgencyHarness.Infrastructure;

namespace AgencyHarness.Agents;

public class OpenModelOptions
{
    /// <summary>Base URL of any OpenAI-compatible server, e.g. Ollama at http://127.0.0.1:11434/v1.</summary>
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "gemma4:e4b";
    /// <summary>Optional bearer token. Local servers such as Ollama don't need one.</summary>
    public string ApiKey { get; set; } = "";
    public int MaxTurns { get; set; } = 12;
    public int MaxTokensPerTurn { get; set; } = 4000;
    /// <summary>Local models are slow on a laptop GPU; a cold start can take a minute.</summary>
    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// The same agent loop as <see cref="ClaudeAgentRunner"/>, over the OpenAI chat-completions format
/// that free open-weight model servers speak (Ollama, llama.cpp, vLLM, LM Studio). Metered at zero cost.
/// </summary>
public class OpenModelAgentRunner(HttpClient http, OpenModelOptions options, CircuitBreaker breaker,
    ILogger<OpenModelAgentRunner> log) : IAgentRunner
{
    public async Task<AgentRunResult> RunAsync(AgentRole role, ToolContext ctx, IReadOnlyList<IAgentTool> tools,
        Func<CancellationToken, Task> heartbeat, CancellationToken ct)
    {
        var toolDefs = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = JsonSerializer.SerializeToNode(t.Properties),
                    ["required"] = JsonSerializer.SerializeToNode(t.Required),
                },
            },
        }).ToArray());

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = role.SystemPrompt },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"""
                    Job {ctx.Job.Id} for your role ({role.Name}).

                    <instructions>
                    {ctx.Job.Instructions}
                    </instructions>
                    """,
            },
        };

        long inTok = 0, outTok = 0;
        string servedModel = options.Model;

        for (var turn = 0; turn < options.MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            await heartbeat(ct);

            var request = new JsonObject
            {
                ["model"] = options.Model,
                ["messages"] = messages.DeepClone(),
                ["tools"] = toolDefs.DeepClone(),
                ["max_tokens"] = options.MaxTokensPerTurn,
            };
            var resp = await CallAsync(request, ct);

            servedModel = resp["model"]?.GetValue<string>() ?? servedModel;
            inTok += resp["usage"]?["prompt_tokens"]?.GetValue<long>() ?? 0;
            outTok += resp["usage"]?["completion_tokens"]?.GetValue<long>() ?? 0;

            var choice = resp["choices"]?[0] ?? throw new RetryableAgentException("Model returned no choices.");
            var message = choice["message"]?.AsObject() ?? throw new RetryableAgentException("Model returned no message.");
            var text = message["content"]?.GetValue<string>() ?? "";
            var calls = message["tool_calls"]?.AsArray() ?? [];

            if (calls.Count == 0)
            {
                if (choice["finish_reason"]?.GetValue<string>() == "length")
                    throw new PermanentAgentException("Response hit max_tokens before finishing.");
                Telemetry.TokensUsed.Add(inTok + outTok);
                return new AgentRunResult(text.Trim(), servedModel, inTok, outTok, 0m);
            }

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = text,
                ["tool_calls"] = calls.DeepClone(),
            });
            for (var i = 0; i < calls.Count; i++)
            {
                var call = calls[i]!;
                var id = call["id"]?.GetValue<string>() ?? $"call_{turn}_{i}";
                var content = await RunToolAsync(ctx, tools, call["function"], ct);
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = content });
            }
        }

        throw new PermanentAgentException($"Agent exceeded {options.MaxTurns} turns without finishing.");
    }

    private async Task<JsonNode> CallAsync(JsonObject request, CancellationToken ct)
    {
        if (!breaker.TryAcquire()) throw new RetryableAgentException("Circuit open", new CircuitOpenException());
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(options.ApiKey))
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

            using var resp = await http.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500)
            {
                breaker.RecordFailure();
                throw new RetryableAgentException($"Model server returned {(int)resp.StatusCode}: {Trim(body)}");
            }
            if (!resp.IsSuccessStatusCode)
                throw new PermanentAgentException($"Model server returned {(int)resp.StatusCode}: {Trim(body)}");

            breaker.RecordSuccess();
            return JsonNode.Parse(body) ?? throw new RetryableAgentException("Empty response from model server.");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException
                                  || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            breaker.RecordFailure();
            log.LogWarning(e, "Transient model error");
            throw new RetryableAgentException(e.Message, e);
        }
    }

    private static async Task<string> RunToolAsync(ToolContext ctx, IReadOnlyList<IAgentTool> tools, JsonNode? fn,
        CancellationToken ct)
    {
        var name = fn?["name"]?.GetValue<string>() ?? "";
        // Only tools granted to this role can run, even if the model names another one.
        var tool = tools.FirstOrDefault(t => t.Name == name);
        try
        {
            if (tool is null) throw new ToolException($"Unknown tool '{name}'.");
            // Arguments arrive as a JSON string; some servers send an object instead.
            var args = fn?["arguments"];
            var json = args is JsonValue v && v.TryGetValue<string>(out var s) ? s : args?.ToJsonString() ?? "{}";
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return await tool.ExecuteAsync(ctx, doc.RootElement.Clone(), ct);
        }
        catch (ToolException e)
        {
            return "Error: " + e.Message;
        }
        catch (JsonException)
        {
            return $"Error: the arguments for '{name}' were not valid JSON.";
        }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
