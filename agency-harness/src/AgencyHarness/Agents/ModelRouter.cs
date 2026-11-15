using System.Text.Json;
using AgencyHarness.Infrastructure;

namespace AgencyHarness.Agents;

public class ModelOptions
{
    /// <summary>Named models, e.g. "local": Ollama gemma, "gemini": Gemini flash.</summary>
    public Dictionary<string, ModelProfile> Profiles { get; set; } = new();
    /// <summary>Which models each agent role tries, in order. "*" is every other role.
    /// Names are profile names, "opencode" (OpenCode's default model), "opencode:provider/model", or "claude".</summary>
    public Dictionary<string, string[]> Routing { get; set; } = new();
    /// <summary>Path to an opencode.json/opencode.jsonc whose providers and models should be usable here.</summary>
    public string? OpenCodeConfig { get; set; }
    public int MaxTurns { get; set; } = 12;
}

/// <summary>
/// Reads OpenCode's model setup so the harness can use the same providers and models.
/// Supports the current format ("providers" → settings.baseURL, env, models; top-level "model": "provider/model")
/// and the older one ("provider" → options.baseURL/apiKey).
/// </summary>
public static class OpenCodeImport
{
    // Built-in OpenCode providers we can reach through an OpenAI-compatible API, with their usual key variables.
    private static readonly Dictionary<string, (string BaseUrl, string Env)> Known = new()
    {
        ["ollama"] = ("http://127.0.0.1:11434/v1", "OLLAMA_API_KEY"),
        ["lmstudio"] = ("http://127.0.0.1:1234/v1", "LMSTUDIO_API_KEY"),
        ["vllm"] = ("http://127.0.0.1:8000/v1", "VLLM_API_KEY"),
        ["llamacpp"] = ("http://127.0.0.1:8080/v1", "LLAMACPP_API_KEY"),
        ["openai"] = ("https://api.openai.com/v1", "OPENAI_API_KEY"),
        ["openrouter"] = ("https://openrouter.ai/api/v1", "OPENROUTER_API_KEY"),
        ["groq"] = ("https://api.groq.com/openai/v1", "GROQ_API_KEY"),
        ["google"] = ("https://generativelanguage.googleapis.com/v1beta/openai", "GEMINI_API_KEY,GOOGLE_GENERATIVE_AI_API_KEY,GOOGLE_API_KEY"),
        ["deepseek"] = ("https://api.deepseek.com/v1", "DEEPSEEK_API_KEY"),
        ["mistral"] = ("https://api.mistral.ai/v1", "MISTRAL_API_KEY"),
        ["together"] = ("https://api.together.xyz/v1", "TOGETHER_API_KEY"),
        ["fireworks"] = ("https://api.fireworks.ai/inference/v1", "FIREWORKS_API_KEY"),
        ["cerebras"] = ("https://api.cerebras.ai/v1", "CEREBRAS_API_KEY"),
    };

    public record Result(Dictionary<string, ModelProfile> Profiles, string? DefaultModel, List<string> Notes);

    public static Result Load(string path)
    {
        var notes = new List<string>();
        var profiles = new Dictionary<string, ModelProfile>();
        if (!File.Exists(path)) { notes.Add($"OpenCode config not found at {path}."); return new(profiles, null, notes); }

        using var doc = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        string? defaultModel = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

        var providers = root.TryGetProperty("providers", out var ps) ? ps : root.TryGetProperty("provider", out var legacy) ? legacy : default;
        var declared = new Dictionary<string, (string? BaseUrl, string? Key, string? Env, List<string> Models)>();
        if (providers.ValueKind == JsonValueKind.Object)
            foreach (var prov in providers.EnumerateObject())
            {
                var v = prov.Value;
                var settings = v.TryGetProperty("settings", out var s) ? s : v.TryGetProperty("options", out var o) ? o : default;
                string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                var env = v.TryGetProperty("env", out var envs) && envs.ValueKind == JsonValueKind.Array
                    ? string.Join(',', envs.EnumerateArray().Select(x => x.GetString()).OfType<string>()) : null;
                var models = v.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Object
                    ? ms.EnumerateObject().Select(x => x.Name).ToList() : new List<string>();
                declared[prov.Name] = (Str(settings, "baseURL"), Str(settings, "apiKey"), env, models);
            }

        void Add(string provider, string model)
        {
            var name = $"opencode:{provider}/{model}";
            if (profiles.ContainsKey(name)) return;
            declared.TryGetValue(provider, out var d);
            var known = Known.TryGetValue(provider, out var k) ? k : default;
            var baseUrl = d.BaseUrl ?? known.BaseUrl;
            if (provider == "anthropic") { notes.Add($"{provider}/{model}: use the built-in \"claude\" route instead."); return; }
            if (string.IsNullOrEmpty(baseUrl)) { notes.Add($"{provider}/{model}: no OpenAI-compatible endpoint known; add settings.baseURL in OpenCode."); return; }
            // OpenCode writes keys as "{env:NAME}"; read the variable, never store the value.
            string? key = null, keyEnv = d.Env ?? known.Env;
            if (d.Key is { } raw)
            {
                if (raw.StartsWith("{env:") && raw.EndsWith('}')) keyEnv = raw[5..^1];
                else key = raw;
            }
            profiles[name] = new ModelProfile { BaseUrl = baseUrl, Model = model, ApiKey = key, ApiKeyEnv = keyEnv, Source = "opencode" };
        }

        foreach (var (prov, d) in declared) foreach (var model in d.Models) Add(prov, model);
        if (defaultModel is { } dm && dm.IndexOf('/') is var slash and > 0) Add(dm[..slash], dm[(slash + 1)..]);
        return new(profiles, defaultModel is null ? null : $"opencode:{defaultModel}", notes);
    }
}

/// <summary>
/// Runs each job on the first healthy model in its role's chain. A model that's down, rate-limited,
/// too slow or rejecting requests is skipped (and its circuit opens for a while), so one bad model
/// never stalls the agency. If every model fails transiently the job goes back on the queue to retry.
/// </summary>
public class RoutedAgentRunner(ModelCatalog catalog, ILogger<RoutedAgentRunner> log) : IAgentRunner
{
    public async Task<AgentRunResult> RunAsync(AgentRole role, ToolContext ctx, IReadOnlyList<IAgentTool> tools,
        Func<CancellationToken, Task> heartbeat, CancellationToken ct)
    {
        var chain = catalog.ChainFor(role.Name);
        if (chain.Count == 0) throw new PermanentAgentException("No model is configured for this role (Harness:Models).");
        Exception? lastTransient = null, lastPermanent = null;
        foreach (var entry in chain)
        {
            if (!entry.Breaker.TryAcquire()) { log.LogInformation("Skipping {Model}: circuit open", entry.Name); continue; }
            try
            {
                var result = await entry.Runner.RunAsync(role, ctx, tools, heartbeat, ct);
                entry.Breaker.RecordSuccess();
                await EnsureDeliverableAsync(ctx, tools, role, result, ct);
                return result;
            }
            catch (RetryableAgentException e)
            {
                entry.Breaker.RecordFailure(); lastTransient = e;
                log.LogWarning("{Model} failed ({Error}); trying the next model", entry.Name, e.Message);
            }
            catch (PermanentAgentException e)
            {
                lastPermanent = e;   // e.g. this model rejects tool schemas: another model may still work
                log.LogWarning("{Model} can't do this job ({Error}); trying the next model", entry.Name, e.Message);
            }
        }
        if (lastTransient is not null) throw new RetryableAgentException("Every model failed for now: " + lastTransient.Message, lastTransient);
        throw lastPermanent ?? new RetryableAgentException("Every model's circuit is open; retrying later.");
    }

    /// <summary>Small models sometimes write the work as their final answer instead of calling save_deliverable. Keep it either way.</summary>
    private static async Task EnsureDeliverableAsync(ToolContext ctx, IReadOnlyList<IAgentTool> tools, AgentRole role, AgentRunResult r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Summary)) return;
        var exists = ctx.Db.Deliverables.Any(d => d.JobId == ctx.Job.Id);
        if (exists || tools.FirstOrDefault(t => t.Name == "save_deliverable") is not { } save) return;
        var title = $"{role.Name}: {(ctx.Job.Instructions.Length > 60 ? ctx.Job.Instructions[..60] + "…" : ctx.Job.Instructions)}";
        await save.ExecuteAsync(ctx, JsonSerializer.SerializeToElement(new { title, content = r.Summary, kind = "other" }), ct);
    }
}

/// <summary>Builds and holds the model chains (and their circuit breakers) from configuration.</summary>
public class ModelCatalog
{
    public record Entry(string Name, IAgentRunner Runner, CircuitBreaker Breaker, ModelProfile? Profile);

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Dictionary<string, string[]> _routing;
    public IReadOnlyList<string> Notes { get; }
    public IReadOnlyDictionary<string, string[]> Routing => _routing;
    public IReadOnlyCollection<Entry> Entries => _entries.Values;

    public ModelCatalog(ModelOptions o, IHttpClientFactory http, TimeProvider clock, ILoggerFactory lf, IServiceProvider sp)
    {
        var notes = new List<string>();
        var profiles = new Dictionary<string, ModelProfile>(o.Profiles);
        string? openCodeDefault = null;
        if (!string.IsNullOrEmpty(o.OpenCodeConfig))
        {
            var imp = OpenCodeImport.Load(o.OpenCodeConfig);
            foreach (var (k, v) in imp.Profiles) profiles.TryAdd(k, v);
            openCodeDefault = imp.DefaultModel;
            notes.AddRange(imp.Notes);
        }
        var client = http.CreateClient("models");
        var log = lf.CreateLogger("Models");
        foreach (var (name, p) in profiles)
            _entries[name] = new Entry(name, new OpenAiChatAgentRunner(client, name, p, o.MaxTurns, log), new CircuitBreaker(3, TimeSpan.FromSeconds(60), clock), p);
        if (sp.GetService<ClaudeAgentRunner>() is { } claude)
            _entries["claude"] = new Entry("claude", claude, new CircuitBreaker(3, TimeSpan.FromSeconds(60), clock), null);
        if (openCodeDefault is not null && _entries.TryGetValue(openCodeDefault, out var def)) _entries["opencode"] = def with { Name = "opencode" };

        _routing = new Dictionary<string, string[]>(o.Routing);
        // "default" means the same as "*" (handy in environment variables, where * is awkward).
        if (!_routing.ContainsKey("*") && _routing.Remove("default", out var dflt)) _routing["*"] = dflt;
        if (!_routing.ContainsKey("*"))
        {
            // Sensible default: OpenCode's model, then every configured model, then Claude.
            var order = new List<string>();
            if (_entries.ContainsKey("opencode")) order.Add("opencode");
            order.AddRange(o.Profiles.Keys);
            if (_entries.ContainsKey("claude")) order.Add("claude");
            _routing["*"] = order.Distinct().ToArray();
        }
        foreach (var name in _routing.Values.SelectMany(x => x).Distinct().Where(n => !_entries.ContainsKey(n)))
            notes.Add($"Routing names \"{name}\", which isn't a configured model; it will be skipped.");
        Notes = notes;
    }

    public bool HasAnyModel => _entries.Count > 0;

    public IReadOnlyList<Entry> ChainFor(string role) =>
        (_routing.TryGetValue(role, out var r) ? r : _routing["*"]).Where(_entries.ContainsKey).Select(n => _entries[n]).ToList();
}
