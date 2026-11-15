using AgencyHarness.Agents;
using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgencyHarness.Api;

public record CreateTenantRequest(string Name, int? RateLimitPerMinute, decimal? MonthlyBudgetUsd, string? WebhookUrl);
public record CreateClientRequest(string Name, string? Brief);
public record UpdateBriefRequest(string Brief);
public record CreateProjectRequest(string Name, string? Goal);
public record CreateJobRequest(string Role, string Instructions, int? Priority);
public record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>
/// REST surface (SD101: "How do we design effective and safe APIs?"):
/// resource-oriented URLs, versioned under /v1, cursor pagination, 202 Accepted for
/// async work, Idempotency-Key on job creation, RFC 7807 problem responses.
/// </summary>
public static class Endpoints
{
    public static void MapHarnessApi(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/health/ready", async (HarnessDbContext db, CircuitBreaker breaker) =>
            await db.Database.CanConnectAsync()
                ? Results.Ok(new { status = "ready", modelCircuit = breaker.State.ToString() })
                : Results.Problem("Database unavailable", statusCode: 503));

        MapAdmin(app);

        var v1 = app.MapGroup("/v1").RequireRateLimiting("per-tenant");

        v1.MapGet("/me", async (HttpContext http, HarnessDbContext db, TimeProvider clock) =>
        {
            var t = http.Tenant();
            var spent = await MonthSpendAsync(db, t.Id, clock);
            return Results.Ok(new { t.Id, t.Name, t.RateLimitPerMinute, t.MonthlyBudgetUsd, monthSpendUsd = spent, t.WebhookUrl });
        });

        AgencyHarness.Voice.VoiceLabEndpoints.MapVoiceLab(v1);
        AgencyHarness.Voice.TwilioVoiceEndpoints.MapWebVoiceTicket(v1);
        v1.MapSwarm();

        v1.MapGet("/roles", () => Results.Ok(AgentRoster.Roles.Values.Select(r => new { r.Name, r.Effort, r.Tools })));

        // ---- Clients ----
        v1.MapPost("/clients", async (CreateClientRequest req, HttpContext http, HarnessDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Required."] });
            var c = new Client { TenantId = http.Tenant().Id, Name = req.Name.Trim(), Brief = req.Brief ?? "" };
            db.Clients.Add(c);
            await db.SaveChangesAsync();
            return Results.Created($"/v1/clients/{c.Id}", c);
        });

        v1.MapGet("/clients", async (HttpContext http, HarnessDbContext db, string? cursor, int? limit) =>
        {
            var tid = http.Tenant().Id;
            var take = Math.Clamp(limit ?? 20, 1, 100);
            var q = db.Clients.AsNoTracking().Where(c => c.TenantId == tid);
            if (DecodeCursor(cursor) is { } before) q = q.Where(c => c.CreatedAt < before);
            var items = await q.OrderByDescending(c => c.CreatedAt).Take(take + 1).ToListAsync();
            return Results.Ok(ToPage(items, take, c => c.CreatedAt));
        });

        // All projects with their client's name: what the dashboard's sidebar needs in one call.
        v1.MapGet("/projects", async (HttpContext http, HarnessDbContext db) =>
        {
            var tid = http.Tenant().Id;
            var rows = await (from p in db.Projects.AsNoTracking()
                              join c in db.Clients.AsNoTracking() on p.ClientId equals c.Id
                              where p.TenantId == tid
                              orderby p.CreatedAt
                              select new { p.Id, p.Name, p.Goal, p.ClientId, clientName = c.Name, clientBrief = c.Brief }).ToListAsync();
            return Results.Ok(rows);
        });

        v1.MapPut("/clients/{id:guid}/brief", async (Guid id, UpdateBriefRequest req, HttpContext http, HarnessDbContext db, ClientBriefCache cache) =>
        {
            var tid = http.Tenant().Id;
            var c = await db.Clients.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid);
            if (c is null) return Results.NotFound();
            c.Brief = req.Brief;
            await db.SaveChangesAsync();
            await cache.InvalidateClientAsync(db, tid, id, http.RequestAborted);   // write-then-invalidate
            return Results.Ok(c);
        });

        // ---- Projects ----
        v1.MapPost("/clients/{clientId:guid}/projects", async (Guid clientId, CreateProjectRequest req, HttpContext http, HarnessDbContext db) =>
        {
            var tid = http.Tenant().Id;
            if (!await db.Clients.AnyAsync(c => c.Id == clientId && c.TenantId == tid)) return Results.NotFound();
            var p = new Project { TenantId = tid, ClientId = clientId, Name = req.Name, Goal = req.Goal ?? "" };
            db.Projects.Add(p);
            await db.SaveChangesAsync();
            return Results.Created($"/v1/projects/{p.Id}", p);
        });

        v1.MapGet("/projects/{id:guid}", async (Guid id, HttpContext http, HarnessDbContext db) =>
        {
            var tid = http.Tenant().Id;
            var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid);
            if (p is null) return Results.NotFound();
            var counts = await db.Jobs.AsNoTracking().Where(j => j.ProjectId == id && j.TenantId == tid)
                .GroupBy(j => j.Status).Select(g => new { status = g.Key.ToString(), count = g.Count() }).ToListAsync();
            return Results.Ok(new { project = p, jobs = counts });
        });

        v1.MapGet("/projects/{id:guid}/deliverables", async (Guid id, HttpContext http, HarnessDbContext db) =>
        {
            var tid = http.Tenant().Id;
            var items = await db.Deliverables.AsNoTracking().Where(d => d.ProjectId == id && d.TenantId == tid)
                .OrderByDescending(d => d.CreatedAt).ToListAsync();
            return Results.Ok(items);
        });

        // ---- Jobs ----
        v1.MapPost("/projects/{projectId:guid}/jobs", CreateJobAsync);

        v1.MapGet("/projects/{projectId:guid}/jobs", async (Guid projectId, HttpContext http, HarnessDbContext db, string? status, string? cursor, int? limit) =>
        {
            var tid = http.Tenant().Id;
            var take = Math.Clamp(limit ?? 20, 1, 100);
            var q = db.Jobs.AsNoTracking().Where(j => j.TenantId == tid && j.ProjectId == projectId);
            if (Enum.TryParse<JobStatus>(status, true, out var s)) q = q.Where(j => j.Status == s);
            if (DecodeCursor(cursor) is { } before) q = q.Where(j => j.CreatedAt < before);
            var items = await q.OrderByDescending(j => j.CreatedAt).Take(take + 1).ToListAsync();
            var page = ToPage(items, take, j => j.CreatedAt);
            var models = await ModelsForAsync(db, tid, page.Items.Select(j => j.Id).ToList());
            return Results.Ok(new Page<object>(page.Items.Select(j => JobView(j, models.GetValueOrDefault(j.Id))).ToList(), page.NextCursor));
        });

        v1.MapGet("/jobs/{id:guid}", async (Guid id, HttpContext http, HarnessDbContext db) =>
        {
            var tid = http.Tenant().Id;
            var j = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid);
            if (j is null) return Results.NotFound();
            var models = await ModelsForAsync(db, tid, [j.Id]);
            return Results.Ok(JobView(j, models.GetValueOrDefault(j.Id)));
        });

        // Which models the agents use, in what order per role, and whether each is healthy right now.
        v1.MapGet("/models", (ModelCatalog catalog, IAgentRunner runner) => Results.Ok(new
        {
            runner = runner switch { RoutedAgentRunner => "models", ScriptedAgentRunner => "scripted", _ => "claude" },
            routing = catalog.Routing,
            models = catalog.Entries.Select(e => new
            {
                e.Name,
                model = e.Profile?.Model ?? "claude",
                endpoint = e.Profile is { } p && Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}" : "anthropic",
                local = e.Profile is { } lp && IsLocal(lp.BaseUrl),
                tools = e.Profile?.Tools ?? true,
                source = e.Profile?.Source ?? "claude",
                health = e.Breaker.State.ToString(),
            }),
            notes = catalog.Notes,
        }));

        v1.MapPost("/jobs/{id:guid}/cancel", async (Guid id, HttpContext http, HarnessDbContext db, TimeProvider clock) =>
        {
            var tid = http.Tenant().Id;
            // Conditional update: only jobs nobody is working on can be cancelled.
            var newVersion = Guid.NewGuid();
            var n = await db.Jobs.Where(j => j.Id == id && j.TenantId == tid && (j.Status == JobStatus.Queued || j.Status == JobStatus.Failed))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Cancelled)
                                          .SetProperty(j => j.UpdatedAt, clock.GetUtcNow())
                                          .SetProperty(j => j.Version, newVersion));
            return n == 1 ? Results.NoContent() : Results.Problem("Job not found or not cancellable.", statusCode: 409);
        });

        v1.MapPost("/jobs/{id:guid}/retry", async (Guid id, HttpContext http, HarnessDbContext db, TimeProvider clock) =>
        {
            var tid = http.Tenant().Id;
            // Redrive from the dead-letter queue after a human has looked at it.
            var newVersion = Guid.NewGuid();
            var n = await db.Jobs.Where(j => j.Id == id && j.TenantId == tid && j.Status == JobStatus.DeadLettered)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Queued)
                                          .SetProperty(j => j.Attempts, 0)
                                          .SetProperty(j => j.NextAttemptAt, clock.GetUtcNow())
                                          .SetProperty(j => j.CompletedAt, (DateTimeOffset?)null)
                                          .SetProperty(j => j.Version, newVersion));
            return n == 1 ? Results.Accepted($"/v1/jobs/{id}") : Results.Problem("Only dead-lettered jobs can be retried.", statusCode: 409);
        });

        v1.MapGet("/usage", async (HttpContext http, HarnessDbContext db, TimeProvider clock) =>
        {
            var tid = http.Tenant().Id;
            var rows = await db.Usage.AsNoTracking().Where(u => u.TenantId == tid).ToListAsync();
            var byMonth = rows.GroupBy(u => $"{u.CreatedAt:yyyy-MM}")
                .Select(g => new { month = g.Key, jobs = g.Count(), inputTokens = g.Sum(x => x.InputTokens), outputTokens = g.Sum(x => x.OutputTokens), costUsd = g.Sum(x => x.CostUsd) })
                .OrderByDescending(x => x.month);
            return Results.Ok(byMonth);
        });

        v1.MapGet("/events", async (HttpContext http, HarnessDbContext db, long? after, int? limit) =>
        {
            var tid = http.Tenant().Id;
            var from = after ?? 0;
            var items = await db.Outbox.AsNoTracking().Where(o => o.TenantId == tid && o.Id > from)
                .OrderBy(o => o.Id).Take(Math.Clamp(limit ?? 50, 1, 200))
                .ToListAsync();
            return Results.Ok(items.Select(o => new
            {
                o.Id, o.EventType, delivered = o.DeliveredAt != null,
                payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(o.Payload),
            }));
        });
    }

    private static async Task<IResult> CreateJobAsync(Guid projectId, CreateJobRequest req, HttpContext http, HarnessService svc)
    {
        var idemKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        var r = await svc.QueueJobAsync(http.Tenant(), projectId, req.Role, req.Instructions, req.Priority, idemKey, http.RequestAborted);
        return r.Outcome switch
        {
            QueueOutcome.Accepted => Results.Accepted($"/v1/jobs/{r.Job!.Id}", JobView(r.Job)),
            QueueOutcome.Replayed => Results.Accepted($"/v1/jobs/{r.ExistingJobId}", new { id = r.ExistingJobId, replayed = true }),
            QueueOutcome.Invalid => Results.ValidationProblem(r.Errors!),
            QueueOutcome.NotFound => Results.NotFound(),
            QueueOutcome.KeyConflict => Results.Problem(r.Message, statusCode: 422),
            _ => Results.Problem(r.Message, statusCode: 409),
        };
    }

    private static void MapAdmin(WebApplication app)
    {
        // Tenant provisioning, guarded by a separate operator key.
        app.MapPost("/admin/tenants", async (CreateTenantRequest req, HttpContext http, HarnessDbContext db, IConfiguration cfg) =>
        {
            var adminKey = cfg["Harness:AdminKey"];
            var given = http.Request.Headers["X-Admin-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(adminKey) || given is null ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(adminKey), System.Text.Encoding.UTF8.GetBytes(given)))
                return Results.Problem("Forbidden.", statusCode: 403);

            if (req.WebhookUrl is { } url && !IsAllowedWebhook(url, app.Environment.IsDevelopment()))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["webhookUrl"] = ["Must be an absolute https URL."] });

            var apiKey = Security.NewApiKey();
            var t = new Tenant
            {
                Name = req.Name, ApiKeyHash = Security.HashApiKey(apiKey), WebhookUrl = req.WebhookUrl,
                WebhookSecret = Security.NewSecret(),
                RateLimitPerMinute = req.RateLimitPerMinute ?? 120, MonthlyBudgetUsd = req.MonthlyBudgetUsd ?? 100m,
            };
            db.Tenants.Add(t);
            await db.SaveChangesAsync();
            // The raw key and webhook secret are returned exactly once.
            return Results.Created($"/v1/me", new { t.Id, t.Name, apiKey, webhookSecret = t.WebhookSecret });
        });
    }

    private static bool IsAllowedWebhook(string url, bool dev) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || (dev && u.Scheme == Uri.UriSchemeHttp));

    private static async Task<Dictionary<Guid, string>> ModelsForAsync(HarnessDbContext db, Guid tenantId, List<Guid> jobIds) =>
        (await db.Usage.AsNoTracking().Where(u => u.TenantId == tenantId && jobIds.Contains(u.JobId))
            .Select(u => new { u.JobId, u.Model, u.Id }).ToListAsync())
        .GroupBy(u => u.JobId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Id).First().Model);

    private static bool IsLocal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.IsLoopback || u.Host.EndsWith(".local") || u.Host is "host.containers.internal" or "host.docker.internal") return true;
        if (!System.Net.IPAddress.TryParse(u.Host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();   // private ranges: 10/8, 172.16/12, 192.168/16
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    internal static object JobView(AgentJob j) => JobView(j, null);

    internal static object JobView(AgentJob j, string? model) => new
    {
        model,
        j.Id, j.ProjectId, j.ParentJobId, role = j.AgentRole, j.Instructions, j.Priority,
        status = j.Status.ToString(), j.Attempts, j.MaxAttempts, j.NextAttemptAt,
        j.Output, j.LastError, j.InputTokens, j.OutputTokens, j.CostUsd, j.CreatedAt, j.CompletedAt,
    };

    private static async Task<decimal> MonthSpendAsync(HarnessDbContext db, Guid tenantId, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return (await db.Usage.AsNoTracking().Where(u => u.TenantId == tenantId && u.CreatedAt >= start)
            .Select(u => u.CostUsd).ToListAsync()).Sum();
    }

    // Cursor = opaque base64 of the last item's CreatedAt ticks (keyset pagination: O(1) per page,
    // stable under inserts, unlike OFFSET).
    private static DateTimeOffset? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        try { return new DateTimeOffset(BitConverter.ToInt64(Convert.FromBase64String(cursor)), TimeSpan.Zero); }
        catch { return null; }
    }

    private static Page<T> ToPage<T>(List<T> items, int take, Func<T, DateTimeOffset> createdAt)
    {
        string? next = null;
        if (items.Count > take)
        {
            items.RemoveAt(take);
            next = Convert.ToBase64String(BitConverter.GetBytes(createdAt(items[^1]).UtcTicks));
        }
        return new Page<T>(items, next);
    }
}
