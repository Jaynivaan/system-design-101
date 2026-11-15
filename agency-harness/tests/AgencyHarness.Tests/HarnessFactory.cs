using System.Net.Http.Json;
using System.Text.Json;
using AgencyHarness.Agents;
using AgencyHarness.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgencyHarness.Tests;

/// <summary>Spins up the real app on a throwaway SQLite file with background workers off,
/// so tests drive the JobProcessor step by step.</summary>
public class HarnessFactory : WebApplicationFactory<Program>
{
    public const string AdminKey = "test-admin-key";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"harness-test-{Guid.NewGuid():n}.db");
    public IAgentRunner? RunnerOverride { get; set; }
    public Dictionary<string, string> Settings { get; } = new();
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Harness", $"Data Source={_dbPath}");
        builder.UseSetting("Harness:RunWorkers", "false");
        builder.UseSetting("Harness:AdminKey", AdminKey);
        builder.UseSetting("Harness:ForceScriptedRunner", "true");
        foreach (var (k, v) in Settings) builder.UseSetting(k, v);
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton(new WorkerOptions { RetryBase = TimeSpan.Zero, RetryMax = TimeSpan.Zero });
            ConfigureServices?.Invoke(s);
            if (RunnerOverride is not null)
            {
                s.RemoveAll<IAgentRunner>();
                s.AddSingleton(RunnerOverride);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    public JobProcessor Processor => Services.GetRequiredService<JobProcessor>();

    /// <summary>Run the queue until it's empty (or a safety cap is hit).</summary>
    public async Task<int> DrainAsync(int max = 50)
    {
        var n = 0;
        while (n < max && await Processor.ProcessNextAsync("test-worker", CancellationToken.None)) n++;
        return n;
    }

    public async Task<(HttpClient Client, Guid TenantId)> NewTenantAsync(string name = "Acme Agency", int rpm = 1000, decimal budget = 100m)
    {
        var admin = CreateClient();
        admin.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        var resp = await admin.PostAsJsonAsync("/admin/tenants", new { name, rateLimitPerMinute = rpm, monthlyBudgetUsd = budget });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", body.GetProperty("apiKey").GetString());
        return (client, body.GetProperty("id").GetGuid());
    }

    public static async Task<Guid> CreateProjectAsync(HttpClient c)
    {
        var client = await (await c.PostAsJsonAsync("/v1/clients", new { name = "Bloom Coffee", brief = "Warm, witty voice. Audience: remote workers 25-40." }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var project = await (await c.PostAsJsonAsync($"/v1/clients/{client.GetProperty("id").GetGuid()}/projects", new { name = "Spring launch", goal = "Grow subscriptions 20%" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        return project.GetProperty("id").GetGuid();
    }
}
