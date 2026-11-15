using System.Net.Http.Json;
using System.Text.Json;
using AgencyHarness.Agents;
using AgencyHarness.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgencyHarness.Tests;

/// <summary>
/// These run real containers through the Docker Engine API when a local engine is available
/// (they pass trivially elsewhere, so CI without Docker still goes green).
/// </summary>
public class ContainerSandboxTests
{
    private const string Socket = "/var/run/docker.sock";
    private static bool EngineAvailable => File.Exists(Socket);
    private static ContainerSandbox Box() => new(new SandboxOptions { Engine = "unix://" + Socket });

    [Fact]
    public async Task Runs_python_and_node_and_returns_output_and_exit_codes()
    {
        if (!EngineAvailable) return;
        var py = await Box().RunAsync(new SandboxRun("python", "import json; print(json.dumps({'ok': 2 + 2}))", 30), default);
        Assert.Equal(0, py.ExitCode);
        Assert.Equal("{\"ok\": 4}", py.Stdout.Trim());

        var fail = await Box().RunAsync(new SandboxRun("python", "import sys; print('bad', file=sys.stderr); sys.exit(3)", 30), default);
        Assert.Equal(3, fail.ExitCode);
        Assert.Contains("bad", fail.Stderr);

        var node = await Box().RunAsync(new SandboxRun("node", "console.log(JSON.stringify({ hreflang: ['en-CA','fr-CA'] }))", 30), default);
        Assert.Equal(0, node.ExitCode);
        Assert.Contains("fr-CA", node.Stdout);
    }

    [Fact]
    public async Task Has_no_network_and_a_read_only_system_with_a_writable_work_folder()
    {
        if (!EngineAvailable) return;
        var r = await Box().RunAsync(new SandboxRun("python", """
            import socket, os
            try:
                socket.create_connection(("1.1.1.1", 80), timeout=2); print("NET:open")
            except OSError: print("NET:blocked")
            try:
                open("/etc/hacked", "w"); print("ROOT:writable")
            except OSError: print("ROOT:read-only")
            open("/work/out.txt", "w").write("ok"); print("WORK:" + open("/work/out.txt").read())
            print("UID:" + str(os.getuid()))
            """, 30), default);
        Assert.Contains("NET:blocked", r.Stdout);
        Assert.Contains("ROOT:read-only", r.Stdout);
        Assert.Contains("WORK:ok", r.Stdout);
        Assert.Contains("UID:65534", r.Stdout);
    }

    [Fact]
    public async Task Runaway_programs_are_stopped_at_the_time_limit()
    {
        if (!EngineAvailable) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await Box().RunAsync(new SandboxRun("python", "while True: pass", 2), default);
        Assert.True(r.TimedOut);
        Assert.Contains("Stopped after 2s", r.Stderr);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
    }
}

public class SandboxGuardTests
{
    private class EchoSandbox : ISandbox
    {
        public string Name => "echo";
        public int Calls;
        public Task<SandboxResult> RunAsync(SandboxRun run, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new SandboxResult(0, "ran " + run.Language, "", 5, false, false)); }
    }

    [Fact]
    public async Task Daily_quota_and_code_size_are_enforced_and_every_run_is_audited()
    {
        var echo = new EchoSandbox();
        using var app = new HarnessFactory();
        app.Settings["Harness:Sandbox:Mode"] = "container";
        app.Settings["Harness:Sandbox:DailyRunsPerTenant"] = "2";
        app.ConfigureServices = s => { s.RemoveAll<ISandbox>(); s.AddSingleton<ISandbox>(echo); };
        var (c, tenantId) = await app.NewTenantAsync();
        var guard = app.Services.GetRequiredService<SandboxGuard>();

        await guard.RunAsync(tenantId, null, "python", "print(1)", null, default);
        await guard.RunAsync(tenantId, null, "python", "print(2)", null, default);
        var over = await Assert.ThrowsAsync<SandboxUnavailableException>(() => guard.RunAsync(tenantId, null, "python", "print(3)", null, default));
        Assert.Contains("limit", over.Message);
        await Assert.ThrowsAsync<SandboxUnavailableException>(() => guard.RunAsync(Guid.NewGuid(), null, "python", new string('x', 70_000), null, default));
        Assert.Equal(2, echo.Calls);

        var status = await c.GetFromJsonAsync<JsonElement>("/v1/sandbox");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(2, status.GetProperty("runsToday").GetInt32());
        Assert.Equal("none", status.GetProperty("limits").GetProperty("network").GetString());
        var events = await c.GetFromJsonAsync<JsonElement>("/v1/events?limit=100");
        Assert.Equal(2, events.EnumerateArray().Count(e => e.GetProperty("eventType").GetString() == "sandbox.run"));
    }

    [Fact]
    public async Task An_agent_checks_its_json_ld_in_the_sandbox_before_saving()
    {
        var echo = new EchoSandbox();
        var server = new FakeChatServer().On("m",
            _ => FakeChatServer.ToolCalls(("run_code", new { language = "python", code = "import json; json.loads('{\"@type\":\"LocalBusiness\"}'); print('valid')" })),
            req =>
            {
                Assert.Contains("ran python", req["messages"]!.ToJsonString());   // the model saw the sandbox result
                return FakeChatServer.ToolCalls(("save_deliverable", new { title = "LocalBusiness JSON-LD", content = "Validated JSON-LD template. " + new string('x', 250) }));
            },
            _ => FakeChatServer.Text("Saved a validated JSON-LD template."));
        using var app = new HarnessFactory();
        app.Settings["Harness:ForceScriptedRunner"] = "false";
        app.Settings["Harness:Models:Profiles:m:BaseUrl"] = "http://127.0.0.1:11434/v1";
        app.Settings["Harness:Models:Profiles:m:Model"] = "m";
        app.Settings["Harness:Sandbox:Mode"] = "container";
        app.ConfigureServices = s =>
        {
            s.AddHttpClient("models").ConfigurePrimaryHttpMessageHandler(() => server);
            s.RemoveAll<ISandbox>(); s.AddSingleton<ISandbox>(echo);
        };
        var (c, _) = await app.NewTenantAsync();
        var projectId = await HarnessFactory.CreateProjectAsync(c);
        await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "techseo", instructions = "Create LocalBusiness JSON-LD for the Toronto hub and validate it." });
        await app.DrainAsync();

        Assert.Equal(1, echo.Calls);
        Assert.Contains("run_code", server.Requests.First()["tools"]!.ToJsonString());
        Assert.Contains("Check your work in the sandbox", server.Requests.First()["messages"]![0]!["content"]!.GetValue<string>());
        var jobs = await c.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/jobs");
        Assert.Equal("Succeeded", jobs.GetProperty("items")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Copywriters_dont_get_the_sandbox_and_it_is_off_by_default()
    {
        using var app = new HarnessFactory();
        var (c, _) = await app.NewTenantAsync();
        var status = await c.GetFromJsonAsync<JsonElement>("/v1/sandbox");
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain("run_code", AgentRoster.Roles["copywriter"].Tools);
    }
}
