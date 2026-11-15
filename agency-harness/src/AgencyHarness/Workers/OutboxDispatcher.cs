using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgencyHarness.Workers;

/// <summary>
/// Delivers outbox events as signed webhooks (SD101: "What is a webhook?").
/// At-least-once: receivers dedupe on X-Harness-Event-Id. Failed deliveries back off
/// exponentially and give up after MaxAttempts, keeping the row for inspection/replay.
/// </summary>
public class OutboxDispatcher(IServiceScopeFactory scopes, IHttpClientFactory http, TimeProvider clock,
    ILogger<OutboxDispatcher> log) : BackgroundService
{
    public const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int sent;
            try { sent = await DispatchBatchAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogError(e, "Outbox dispatch error"); sent = 0; }
            if (sent == 0)
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarnessDbContext>();
        var now = clock.GetUtcNow();

        var batch = await db.Outbox
            .Where(o => o.DeliveredAt == null && o.NextAttemptAt <= now && o.Attempts < MaxAttempts)
            .OrderBy(o => o.Id).Take(50).ToListAsync(ct);
        if (batch.Count == 0) return 0;

        var tenantIds = batch.Select(b => b.TenantId).Distinct().ToList();
        var tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var client = http.CreateClient("webhooks");

        foreach (var msg in batch)
        {
            var tenant = tenants[msg.TenantId];
            if (string.IsNullOrEmpty(tenant.WebhookUrl))
            {
                msg.DeliveredAt = now;   // nobody subscribed; events stay queryable via the API
                continue;
            }
            msg.Attempts++;
            try
            {
                var ts = clock.GetUtcNow().ToUnixTimeSeconds();
                using var req = new HttpRequestMessage(HttpMethod.Post, tenant.WebhookUrl)
                {
                    Content = new StringContent(msg.Payload, System.Text.Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("X-Harness-Event-Id", msg.Id.ToString());
                req.Headers.Add("X-Harness-Event-Type", msg.EventType);
                req.Headers.Add("X-Harness-Timestamp", ts.ToString());
                req.Headers.Add("X-Harness-Signature", Security.SignWebhook(tenant.WebhookSecret, ts, msg.Payload));

                using var resp = await client.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                msg.DeliveredAt = clock.GetUtcNow();
                Telemetry.WebhooksDelivered.Add(1);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                msg.LastError = e.Message;
                msg.NextAttemptAt = clock.GetUtcNow() + Backoff.Compute(msg.Attempts, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(30));
                log.LogWarning("Webhook {Id} to tenant {Tenant} failed (attempt {N}): {Err}", msg.Id, msg.TenantId, msg.Attempts, e.Message);
            }
        }
        await db.SaveChangesAsync(ct);
        return batch.Count;
    }
}
