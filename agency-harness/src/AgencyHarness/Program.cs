using System.Threading.RateLimiting;
using AgencyHarness.Agents;
using AgencyHarness.Api;
using AgencyHarness.Infrastructure;
using AgencyHarness.Voice;
using AgencyHarness.Workers;
using Anthropic;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// Hosting platforms (Render, Railway, Cloud Run, Heroku-style) tell the app which port to use.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://+:{port}");

// Behind a TLS-terminating proxy or load balancer, trust X-Forwarded-For / X-Forwarded-Proto
// so logs show the real client and the app knows the original request was HTTPS.
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                         | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();   // the platform's proxy addresses aren't known in advance
    o.KnownProxies.Clear();
});

// ---------- Persistence ----------
builder.Services.AddDbContext<HarnessDbContext>(o =>
    o.UseSqlite(cfg.GetConnectionString("Harness") ?? "Data Source=harness.db"));

// ---------- Core services ----------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ClientBriefCache>();
builder.Services.AddScoped<AgencyHarness.Api.HarnessService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Outbox>();
builder.Services.AddSingleton(_ => new CircuitBreaker(
    failureThreshold: cfg.GetValue("Harness:Circuit:FailureThreshold", 5),
    openDuration: TimeSpan.FromSeconds(cfg.GetValue("Harness:Circuit:OpenSeconds", 30)),
    TimeProvider.System));

// ---------- Agents ----------
builder.Services.AddSingleton<IAgentTool, GetClientBriefTool>();
builder.Services.AddSingleton<IAgentTool, ListDeliverablesTool>();
builder.Services.AddSingleton<IAgentTool, SaveDeliverableTool>();
builder.Services.AddSingleton<IAgentTool, DelegateTaskTool>();
builder.Services.AddSingleton<IAgentTool, AddAgendaTaskTool>();
builder.Services.AddSingleton<IAgentTool, ListAgendaTasksTool>();
builder.Services.AddSingleton<IAgentTool, SubmitReviewTool>();
builder.Services.AddSingleton<SkillsAndLessons>();
builder.Services.AddSingleton<QualityGate>();
builder.Services.AddSingleton<ToolRegistry>();

var claudeOptions = cfg.GetSection("Harness:Claude").Get<ClaudeOptions>() ?? new ClaudeOptions();
builder.Services.AddSingleton(claudeOptions);
var forceScripted = cfg.GetValue("Harness:ForceScriptedRunner", false);
var useClaude = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) && !forceScripted;
if (useClaude)
{
    builder.Services.AddSingleton(_ => new AnthropicClient());   // reads ANTHROPIC_API_KEY
    builder.Services.AddSingleton<ClaudeAgentRunner>();
}

// Models the agents run on: local (Ollama, llama.cpp, LM Studio, vLLM), cloud (Gemini, OpenAI, OpenRouter…),
// models imported from your OpenCode config, and Claude, chained per role with fallbacks.
var modelOptions = cfg.GetSection("Harness:Models").Get<ModelOptions>() ?? new ModelOptions();
builder.Services.AddSingleton(modelOptions);
builder.Services.AddHttpClient("models", c => c.Timeout = Timeout.InfiniteTimeSpan);   // each profile sets its own timeout
builder.Services.AddSingleton<ModelCatalog>();
var useRouter = !forceScripted && (modelOptions.Profiles.Count > 0 || !string.IsNullOrEmpty(modelOptions.OpenCodeConfig));
if (useRouter) builder.Services.AddSingleton<IAgentRunner, RoutedAgentRunner>();
else if (useClaude) builder.Services.AddSingleton<IAgentRunner>(sp => sp.GetRequiredService<ClaudeAgentRunner>());
else builder.Services.AddSingleton<IAgentRunner, ScriptedAgentRunner>();

// ---------- Workers ----------
builder.Services.AddSingleton(cfg.GetSection("Harness:Worker").Get<WorkerOptions>() ?? new WorkerOptions());
builder.Services.AddSingleton<JobProcessor>();
builder.Services.AddSingleton(cfg.GetSection("Harness:Agendas").Get<AgendaOptions>() ?? new AgendaOptions());
builder.Services.AddSingleton<AgendaScheduler>();
builder.Services.AddSingleton(sp => new AgencyHarness.Api.AgendaOptionsView(sp.GetRequiredService<AgendaOptions>().MaxSwarmSize));
builder.Services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(10));
if (cfg.GetValue("Harness:RunWorkers", true))
{
    builder.Services.AddHostedService<AgentWorker>();
    builder.Services.AddHostedService<AgendaWorker>();
    builder.Services.AddSingleton<OutboxDispatcher>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
}

// ---------- Rate limiting: token bucket per tenant ----------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("per-tenant", http =>
    {
        var tenant = http.Items[Auth.TenantItem] as AgencyHarness.Domain.Tenant;
        var perMinute = tenant?.RateLimitPerMinute ?? 30;
        return RateLimitPartition.GetTokenBucketLimiter(tenant?.Id.ToString() ?? "anonymous", _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = perMinute,                 // burst size
            TokensPerPeriod = perMinute,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
});

// ---------- MCP: the same harness, for OpenCode, Claude Code, Cursor and other MCP clients ----------
// Stateless Streamable HTTP: no server-side session, so any node behind a load balancer can answer.
builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "agency-harness", Version = "1.0.0" })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<HarnessMcpTools>();

// ---------- Voice engine (phone calls through your own models) ----------
builder.AddVoice();
builder.AddVoiceLab();

builder.Services.AddProblemDetails();
builder.Logging.AddSimpleConsole(o => { o.IncludeScopes = true; o.SingleLine = true; });

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HarnessDbContext>();
    db.Database.EnsureCreated();   // swap for EF migrations before production
    if (db.Database.IsSqlite()) SchemaSync.Apply(db);   // adds tables from newer versions to an existing database
    BuiltinSkills.SeedAsync(db).GetAwaiter().GetResult();
    if (db.Database.IsSqlite())
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");   // readers don't block the writer
}

{
    var catalog = app.Services.GetRequiredService<ModelCatalog>();
    if (useRouter)
    {
        app.Logger.LogInformation("Agent models: {Routing}", string.Join("; ", catalog.Routing.Select(r => $"{r.Key} → {string.Join(" > ", r.Value)}")));
        foreach (var note in catalog.Notes) app.Logger.LogWarning("Models: {Note}", note);
    }
    else app.Logger.LogInformation("Agent runner: {Runner}", useClaude ? $"Claude ({claudeOptions.Model})" : "scripted (configure Harness:Models or set ANTHROPIC_API_KEY for real agents)");
}

app.UseForwardedHeaders();
// The dashboard (wwwroot/index.html) is served at "/". It only talks to the /v1 API with the
// key the person signs in with, so it has no access the API doesn't already grant.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseExceptionHandler();

if (app.Environment.IsProduction() && string.IsNullOrEmpty(cfg["Harness:AdminKey"]))
    app.Logger.LogWarning("Harness:AdminKey is not set, so /admin/tenants is disabled. Set Harness__AdminKey to create tenants.");
app.MapGet("/api", () => Results.Ok(new { name = "Agency Harness", docs = "https://github.com/jaynivaan/system-design-101/tree/claude/system-design-agency-nuwwik/agency-harness", health = "/health/ready", mcp = "/mcp", api = "/v1" }));
app.UseCorrelationId();
app.UseTenantAuth();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.MapHarnessApi();
app.MapMcp("/mcp").RequireRateLimiting("per-tenant");
app.MapTwilioVoice();

app.Run();

public partial class Program;   // for WebApplicationFactory in tests
