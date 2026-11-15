using System.Threading.RateLimiting;
using AgencyHarness.Agents;
using AgencyHarness.Api;
using AgencyHarness.Infrastructure;
using AgencyHarness.Workers;
using Anthropic;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---------- Persistence ----------
builder.Services.AddDbContext<HarnessDbContext>(o =>
    o.UseSqlite(cfg.GetConnectionString("Harness") ?? "Data Source=harness.db"));

// ---------- Core services ----------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ClientBriefCache>();
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
builder.Services.AddSingleton<ToolRegistry>();

var claudeOptions = cfg.GetSection("Harness:Claude").Get<ClaudeOptions>() ?? new ClaudeOptions();
builder.Services.AddSingleton(claudeOptions);
var useClaude = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
                && !cfg.GetValue("Harness:ForceScriptedRunner", false);
if (useClaude)
{
    builder.Services.AddSingleton(_ => new AnthropicClient());   // reads ANTHROPIC_API_KEY
    builder.Services.AddSingleton<IAgentRunner, ClaudeAgentRunner>();
}
else
{
    builder.Services.AddSingleton<IAgentRunner, ScriptedAgentRunner>();
}

// ---------- Workers ----------
builder.Services.AddSingleton(cfg.GetSection("Harness:Worker").Get<WorkerOptions>() ?? new WorkerOptions());
builder.Services.AddSingleton<JobProcessor>();
builder.Services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(10));
if (cfg.GetValue("Harness:RunWorkers", true))
{
    builder.Services.AddHostedService<AgentWorker>();
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

builder.Services.AddProblemDetails();
builder.Logging.AddSimpleConsole(o => { o.IncludeScopes = true; o.SingleLine = true; });

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HarnessDbContext>();
    db.Database.EnsureCreated();   // swap for EF migrations before production
    if (db.Database.IsSqlite())
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");   // readers don't block the writer
}

app.Logger.LogInformation("Agent runner: {Runner}", useClaude ? $"Claude ({claudeOptions.Model})" : "scripted (set ANTHROPIC_API_KEY for Claude)");

app.UseExceptionHandler();
app.UseCorrelationId();
app.UseTenantAuth();
app.UseRateLimiter();
app.MapHarnessApi();

app.Run();

public partial class Program;   // for WebApplicationFactory in tests
