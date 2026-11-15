using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgencyHarness.Infrastructure;

namespace AgencyHarness.Agents;

/// <summary>
/// One model the agents can run on. Anything with an OpenAI-compatible chat API works: Ollama, llama.cpp,
/// LM Studio, vLLM (local) or Gemini, OpenAI, OpenRouter, Groq, DeepSeek, Mistral (cloud).
/// </summary>
public class ModelProfile
{
    /// <summary>Base URL including the version segment, e.g. http://127.0.0.1:11434/v1 (Ollama).</summary>
    public string BaseUrl { get; set; } = "";
    public string ChatPath { get; set; } = "/chat/completions";
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    /// <summary>Environment variable(s) that hold the key, comma-separated; the first one set wins.</summary>
    public string? ApiKeyEnv { get; set; }
    /// <summary>Native tool calling. Off = the agent uses a plain-JSON action protocol (works with any chat model).</summary>
    public bool Tools { get; set; } = true;
    public int MaxTokens { get; set; } = 4096;
    public double Temperature { get; set; } = 0.4;
    public int TimeoutSeconds { get; set; } = 240;
    /// <summary>USD per million tokens, for the usage ledger (0 for local models).</summary>
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
    /// <summary>Where this profile came from (config, opencode).</summary>
    public string Source { get; set; } = "config";

    public string? ResolveKey()
    {
        if (!string.IsNullOrEmpty(ApiKey)) return ApiKey;
        foreach (var name in (ApiKeyEnv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } v) return v;
        return null;
    }
}

/// <summary>
/// The agent loop over an OpenAI-compatible chat API: call → run the tools the model asked for → repeat
/// until it answers. For models without native tool calling it switches to a JSON action protocol
/// ({"tool": ..., "input": ...} / {"final": ...}) so even small local models can do agent work.
/// </summary>
public class OpenAiChatAgentRunner(HttpClient http, string profileName, ModelProfile p, int maxTurns, ILogger log) : IAgentRunner
{
    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<AgentRunResult> RunAsync(AgentRole role, ToolContext ctx, IReadOnlyList<IAgentTool> tools,
        Func<CancellationToken, Task> heartbeat, CancellationToken ct)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = role.SystemPrompt + (p.Tools ? "" : JsonProtocol(tools)) },
            new JsonObject { ["role"] = "user", ["content"] = $"Job {ctx.Job.Id} for your role ({role.Name}).\n\n<instructions>\n{ctx.Job.Instructions}\n</instructions>" },
        };
        long inTok = 0, outTok = 0;
        int malformed = 0;

        for (var turn = 0; turn < maxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            await heartbeat(ct);
            var body = new JsonObject
            {
                ["model"] = p.Model,
                ["messages"] = messages.DeepClone(),
                ["max_tokens"] = p.MaxTokens,
                ["temperature"] = p.Temperature,
                ["stream"] = false,
            };
            if (p.Tools) body["tools"] = ToolDefs(tools);

            var (message, usage) = await CallAsync(body, ct);
            inTok += usage.In; outTok += usage.Out;
            var content = message["content"]?.GetValue<string>() ?? "";

            if (p.Tools && message["tool_calls"] is JsonArray calls && calls.Count > 0)
            {
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content, ["tool_calls"] = calls.DeepClone() });
                foreach (var call in calls)
                {
                    var id = call?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
                    var name = call?["function"]?["name"]?.GetValue<string>() ?? "";
                    var args = call?["function"]?["arguments"];
                    var argsText = args is JsonValue v && v.TryGetValue<string>(out var s) ? s : args?.ToJsonString() ?? "{}";
                    var result = await RunToolAsync(ctx, tools, name, argsText, ct);
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["name"] = name, ["content"] = result });
                }
                continue;
            }

            if (!p.Tools)
            {
                var action = ParseAction(content);
                if (action is { Tool: { } toolName })
                {
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    var result = await RunToolAsync(ctx, tools, toolName, action.Input ?? "{}", ct);
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = $"Result of {toolName}:\n{result}\n\nContinue. Reply with one JSON object." });
                    continue;
                }
                if (action is null && ++malformed <= 2)
                {
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = "That wasn't a single JSON object. Reply with exactly one JSON object: {\"tool\": ..., \"input\": {...}} or {\"final\": \"...\"}." });
                    continue;
                }
                content = action?.Final ?? content;
            }

            return new AgentRunResult(content.Trim(), $"{profileName} ({p.Model})", inTok, outTok,
                (inTok * p.InputPerMillion + outTok * p.OutputPerMillion) / 1_000_000m);
        }
        throw new PermanentAgentException($"{profileName} exceeded {maxTurns} turns without finishing.");
    }

    private async Task<(JsonNode Message, (long In, long Out) Usage)> CallAsync(JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, p.BaseUrl.TrimEnd('/') + p.ChatPath)
        {
            Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json"),
        };
        if (p.ResolveKey() is { } key) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(p.TimeoutSeconds));

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new RetryableAgentException($"{profileName} timed out after {p.TimeoutSeconds}s."); }
        catch (HttpRequestException e) { throw new RetryableAgentException($"{profileName} unreachable: {e.Message}", e); }
        using var _ = resp;
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)resp.StatusCode >= 500)
            throw new RetryableAgentException($"{profileName} returned {(int)resp.StatusCode}: {Trim(text)}");
        if (!resp.IsSuccessStatusCode)
            throw new PermanentAgentException($"{profileName} rejected the request ({(int)resp.StatusCode}): {Trim(text)}");

        try
        {
            var root = JsonNode.Parse(text)!;
            var message = root["choices"]![0]!["message"]!;
            long pin = root["usage"]?["prompt_tokens"]?.GetValue<long>() ?? 0;
            long pout = root["usage"]?["completion_tokens"]?.GetValue<long>() ?? 0;
            Telemetry.TokensUsed.Add(pin + pout);
            return (message, (pin, pout));
        }
        catch (Exception e) when (e is JsonException or NullReferenceException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            throw new RetryableAgentException($"{profileName} returned an unexpected response: {Trim(text)}");
        }
    }

    private async Task<string> RunToolAsync(ToolContext ctx, IReadOnlyList<IAgentTool> tools, string name, string argsJson, CancellationToken ct)
    {
        var tool = tools.FirstOrDefault(t => t.Name == name);   // only tools granted to this role
        if (tool is null) return $"Error: unknown tool '{name}'. Available: {string.Join(", ", tools.Select(t => t.Name))}.";
        JsonElement input;
        try { input = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson).RootElement.Clone(); }
        catch (JsonException) { return "Error: tool arguments were not valid JSON."; }
        try { return await tool.ExecuteAsync(ctx, input, ct); }
        catch (ToolException e) { return "Error: " + e.Message; }
        finally { log.LogDebug("{Profile} ran tool {Tool}", profileName, name); }
    }

    private static JsonArray ToolDefs(IReadOnlyList<IAgentTool> tools) => new(tools.Select(t => (JsonNode)new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = JsonSerializer.SerializeToNode(t.Properties, Json),
                ["required"] = new JsonArray(t.Required.Select(r => (JsonNode)r).ToArray()),
            },
        },
    }).ToArray());

    private static string JsonProtocol(IReadOnlyList<IAgentTool> tools)
    {
        var sb = new StringBuilder("\n\nYou act by replying with exactly ONE JSON object and nothing else.\n");
        sb.Append("To use a tool: {\"tool\": \"<name>\", \"input\": {<arguments>}}\n");
        sb.Append("When completely done: {\"final\": \"<two-sentence summary for the account manager>\"}\n\nTools:\n");
        foreach (var t in tools)
            sb.Append($"- {t.Name}: {t.Description} Arguments: {JsonSerializer.Serialize(t.Properties, Json)}; required: [{string.Join(", ", t.Required)}]\n");
        return sb.ToString();
    }

    public record JsonAction(string? Tool, string? Input, string? Final);

    /// <summary>Find the first JSON object in a model reply (tolerates code fences and chatter around it).</summary>
    public static JsonAction? ParseAction(string content)
    {
        var start = content.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0; bool inStr = false, esc = false;
            for (int i = start; i < content.Length; i++)
            {
                var c = content[i];
                if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(content[start..(i + 1)]);
                        var r = doc.RootElement;
                        if (r.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String)
                            return new JsonAction(tool.GetString(), r.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}", null);
                        if (r.TryGetProperty("final", out var fin))
                            return new JsonAction(null, null, fin.ValueKind == JsonValueKind.String ? fin.GetString() : fin.GetRawText());
                    }
                    catch (JsonException) { }
                    break;
                }
            }
            start = content.IndexOf('{', start + 1);
        }
        return null;
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
