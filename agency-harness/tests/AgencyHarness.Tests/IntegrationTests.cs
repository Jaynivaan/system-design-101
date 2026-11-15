using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgencyHarness.Agents;
using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgencyHarness.Tests;

public class IntegrationTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Strategist_fans_out_to_specialists_and_everything_is_metered_and_evented()
    {
        using var app = new HarnessFactory();
        var (c, _) = await app.NewTenantAsync();
        var projectId = await HarnessFactory.CreateProjectAsync(c);

        var created = await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "strategist", instructions = "Plan the spring launch campaign" });
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var jobId = (await Json(created)).GetProperty("id").GetGuid();

        Assert.Equal(3, await app.DrainAsync());   // strategist + copywriter + seo

        var job = await Json(await c.GetAsync($"/v1/jobs/{jobId}"));
        Assert.Equal("Succeeded", job.GetProperty("status").GetString());

        var children = await Json(await c.GetAsync($"/v1/projects/{projectId}/jobs?status=Succeeded"));
        Assert.Equal(3, children.GetProperty("items").GetArrayLength());

        var deliverables = await Json(await c.GetAsync($"/v1/projects/{projectId}/deliverables"));
        Assert.Equal(3, deliverables.GetArrayLength());
        Assert.Contains("Bloom Coffee", deliverables[0].GetProperty("content").GetString());

        var events = await Json(await c.GetAsync("/v1/events"));
        var types = events.EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).ToList();
        Assert.Equal(3, types.Count(t => t == "job.queued"));
        Assert.Equal(3, types.Count(t => t == "job.succeeded"));

        var usage = await Json(await c.GetAsync("/v1/usage"));
        Assert.Equal(3, usage[0].GetProperty("jobs").GetInt32());
    }

    [Fact]
    public async Task Idempotency_key_replays_same_job_and_rejects_different_body()
    {
        using var app = new HarnessFactory();
        var (c, _) = await app.NewTenantAsync();
        var projectId = await HarnessFactory.CreateProjectAsync(c);

        HttpRequestMessage Req(string instructions) => new(HttpMethod.Post, $"/v1/projects/{projectId}/jobs")
        {
            Content = JsonContent.Create(new { role = "copywriter", instructions }),
            Headers = { { "Idempotency-Key", "abc-123" } },
        };

        var first = await Json(await c.SendAsync(Req("Write 3 taglines")));
        var second = await Json(await c.SendAsync(Req("Write 3 taglines")));
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.True(second.GetProperty("replayed").GetBoolean());

        var conflict = await c.SendAsync(Req("Something else"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);

        var list = await Json(await c.GetAsync($"/v1/projects/{projectId}/jobs"));
        Assert.Equal(1, list.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Tenants_cannot_see_each_others_data()
    {
        using var app = new HarnessFactory();
        var (a, _) = await app.NewTenantAsync("Agency A");
        var (b, _) = await app.NewTenantAsync("Agency B");
        var projectId = await HarnessFactory.CreateProjectAsync(a);
        var job = await Json(await a.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "seo", instructions = "Keyword research" }));

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/v1/jobs/{job.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/v1/projects/{projectId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "seo", instructions = "x" })).StatusCode);
    }

    [Fact]
    public async Task Requests_need_a_valid_api_key()
    {
        using var app = new HarnessFactory();
        var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/v1/me")).StatusCode);
        anon.DefaultRequestHeaders.Add("X-Api-Key", "ak_wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Per_tenant_rate_limit_returns_429_with_retry_after()
    {
        using var app = new HarnessFactory();
        var (slow, _) = await app.NewTenantAsync("Small", rpm: 3);
        var (fast, _) = await app.NewTenantAsync("Big", rpm: 100);

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await slow.GetAsync("/v1/me")).StatusCode);
        Assert.Equal(3, codes.Count(s => s == HttpStatusCode.OK));
        var limited = await slow.GetAsync("/v1/me");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));

        // A noisy tenant doesn't affect anyone else.
        Assert.Equal(HttpStatusCode.OK, (await fast.GetAsync("/v1/me")).StatusCode);
    }

    private class FlakyRunner(int failuresBeforeSuccess, bool permanent = false) : IAgentRunner
    {
        public int Calls;
        public Task<AgentRunResult> RunAsync(AgentRole role, ToolContext ctx, IReadOnlyList<IAgentTool> tools,
            Func<CancellationToken, Task> heartbeat, CancellationToken ct)
        {
            Calls++;
            if (permanent) throw new PermanentAgentException("bad request");
            if (Calls <= failuresBeforeSuccess) throw new RetryableAgentException("503 overloaded");
            return Task.FromResult(new AgentRunResult("done", "claude-opus-5-5", 1000, 500, Pricing.Cost("claude-opus-5-5", 1000, 500)));
        }
    }

    [Fact]
    public async Task Transient_failures_retry_then_succeed()
    {
        var runner = new FlakyRunner(failuresBeforeSuccess: 2);
        using var app = new HarnessFactory { RunnerOverride = runner };
        var (c, _) = await app.NewTenantAsync();
        var projectId = await HarnessFactory.CreateProjectAsync(c);
        var job = await Json(await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "analyst", instructions = "KPIs" }));

        await app.DrainAsync();
        var state = await Json(await c.GetAsync($"/v1/jobs/{job.GetProperty("id").GetGuid()}"));
        Assert.Equal("Succeeded", state.GetProperty("status").GetString());
        Assert.Equal(3, state.GetProperty("attempts").GetInt32());
        Assert.Equal(0.014m, state.GetProperty("costUsd").GetDecimal());   // 1000*$4/M + 500*$20/M
    }

    [Fact]
    public async Task Exhausted_retries_dead_letter_and_can_be_redriven()
    {
        var runner = new FlakyRunner(failuresBeforeSuccess: 100);
        using var app = new HarnessFactory { RunnerOverride = runner };
        var (c, _) = await app.NewTenantAsync();
        var projectId = await HarnessFactory.CreateProjectAsync(c);
        var id = (await Json(await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "analyst", instructions = "KPIs" }))).GetProperty("id").GetGuid();

        await app.DrainAsync();
        var state = await Json(await c.GetAsync($"/v1/jobs/{id}"));
        Assert.Equal("DeadLettered", state.GetProperty("status").GetString());
        Assert.Equal(4, runner.Calls);

        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync($"/v1/jobs/{id}/retry", null)).StatusCode);
        Assert.Equal("Queued", (await Json(await c.GetAsync($"/v1/jobs/{id}"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Permanent_failures_dead_letter_immediately()
    {
        var runner = new FlakyRunner(0, permanent: true);
        using var app = new HarnessFactory { RunnerOverride = runner };
        var (c, _) = await app.NewTenantAsync();
        var projectId = await HarnessFactory.CreateProjectAsync(c);
        await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "analyst", instructions = "KPIs" });
        await app.DrainAsync();
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Budget_guardrail_stops_work_when_monthly_budget_is_spent()
    {
        var runner = new FlakyRunner(0);
        using var app = new HarnessFactory { RunnerOverride = runner };
        var (c, _) = await app.NewTenantAsync(budget: 0.01m);
        var projectId = await HarnessFactory.CreateProjectAsync(c);
        var a = (await Json(await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "analyst", instructions = "one" }))).GetProperty("id").GetGuid();
        await app.DrainAsync();
        var b = (await Json(await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "analyst", instructions = "two" }))).GetProperty("id").GetGuid();
        await app.DrainAsync();

        Assert.Equal("Succeeded", (await Json(await c.GetAsync($"/v1/jobs/{a}"))).GetProperty("status").GetString());
        var second = await Json(await c.GetAsync($"/v1/jobs/{b}"));
        Assert.Equal("DeadLettered", second.GetProperty("status").GetString());
        Assert.Contains("budget", second.GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task Competing_workers_never_claim_the_same_job_and_expired_leases_are_reclaimed()
    {
        using var app = new HarnessFactory();
        var (c, tenantId) = await app.NewTenantAsync();
        var projectId = await HarnessFactory.CreateProjectAsync(c);
        for (var i = 0; i < 20; i++)
            await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "copywriter", instructions = $"task {i}" });

        // 8 workers race; each claim is a separate DbContext like separate processes.
        var claimed = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async w =>
        {
            while (true)
            {
                using var scope = app.Services.CreateScope();
                var q = new JobQueue(scope.ServiceProvider.GetRequiredService<HarnessDbContext>(), TimeProvider.System);
                var job = await q.TryClaimAsync($"w{w}", TimeSpan.FromMinutes(5), default);
                if (job is null) break;
                claimed.Add(job.Id);
            }
        }));
        Assert.Equal(20, claimed.Count);
        Assert.Equal(20, claimed.Distinct().Count());

        // Simulate a crashed worker: move time past the lease; the job is claimable again.
        using var s2 = app.Services.CreateScope();
        var later = new ManualClock(DateTimeOffset.UtcNow.AddMinutes(6));
        var reclaimed = await new JobQueue(s2.ServiceProvider.GetRequiredService<HarnessDbContext>(), later)
            .TryClaimAsync("rescuer", TimeSpan.FromMinutes(5), default);
        Assert.NotNull(reclaimed);
        Assert.Equal(2, reclaimed!.Attempts);
    }

    [Fact]
    public async Task Webhooks_are_signed_and_delivered_from_the_outbox()
    {
        using var app = new HarnessFactory();
        var (c, tenantId) = await app.NewTenantAsync();
        var projectId = await HarnessFactory.CreateProjectAsync(c);
        await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "copywriter", instructions = "taglines" });

        string secret;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarnessDbContext>();
            var t = await db.Tenants.FirstAsync(x => x.Id == tenantId);
            t.WebhookUrl = "https://hooks.example.test/harness";
            secret = t.WebhookSecret;
            await db.SaveChangesAsync();
        }

        var captured = new List<HttpRequestMessage>();
        var bodies = new List<string>();
        var handler = new CaptureHandler(async r => { captured.Add(r); bodies.Add(await r.Content!.ReadAsStringAsync()); });
        var dispatcher = new AgencyHarness.Workers.OutboxDispatcher(
            app.Services.GetRequiredService<IServiceScopeFactory>(), new StubHttpFactory(handler), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgencyHarness.Workers.OutboxDispatcher>.Instance);

        Assert.Equal(1, await dispatcher.DispatchBatchAsync(default));
        var req = captured.Single();
        Assert.Equal("job.queued", req.Headers.GetValues("X-Harness-Event-Type").Single());
        var ts = long.Parse(req.Headers.GetValues("X-Harness-Timestamp").Single());
        Assert.True(Security.VerifyWebhook(secret, ts, bodies[0], req.Headers.GetValues("X-Harness-Signature").Single()));
        Assert.Equal(0, await dispatcher.DispatchBatchAsync(default));   // delivered exactly once from our side
    }

    private class CaptureHandler(Func<HttpRequestMessage, Task> onSend) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            await onSend(r);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private class StubHttpFactory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }
}
