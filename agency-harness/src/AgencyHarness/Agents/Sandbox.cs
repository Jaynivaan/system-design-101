using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgencyHarness.Infrastructure;

namespace AgencyHarness.Agents;

public class SandboxOptions
{
    /// <summary>"off", "container" (Docker/Podman engine API) or "remote" (any sandbox service with the simple HTTP contract).</summary>
    public string Mode { get; set; } = "off";
    /// <summary>Engine API endpoint: unix:///var/run/docker.sock, unix:///run/podman/podman.sock, or http://host:2375.</summary>
    public string Engine { get; set; } = "unix:///var/run/docker.sock";
    public Dictionary<string, string> Images { get; set; } = new() { ["python"] = "python:3.12-alpine", ["node"] = "node:22-alpine" };
    public int MemoryMb { get; set; } = 256;
    public double Cpus { get; set; } = 0.5;
    public int Pids { get; set; } = 64;
    public int TmpfsMb { get; set; } = 64;
    public int DefaultTimeoutSeconds { get; set; } = 20;
    public int MaxTimeoutSeconds { get; set; } = 60;
    public int MaxConcurrent { get; set; } = 2;
    public int DailyRunsPerTenant { get; set; } = 200;
    public int MaxOutputBytes { get; set; } = 32_000;
    public int MaxCodeBytes { get; set; } = 64_000;
    /// <summary>Roles allowed to run code.</summary>
    public string[] Roles { get; set; } = ["techseo", "analyst", "seo", "geo", "content", "strategist"];
    public string? RemoteUrl { get; set; }
    public string? RemoteApiKeyEnv { get; set; }
}

public record SandboxRun(string Language, string Code, int TimeoutSeconds);
public record SandboxResult(int ExitCode, string Stdout, string Stderr, double DurationMs, bool TimedOut, bool Truncated);

public interface ISandbox
{
    string Name { get; }
    Task<SandboxResult> RunAsync(SandboxRun run, CancellationToken ct);
}

public class SandboxUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One throwaway container per run, created through the Docker Engine API (Podman serves the same API),
/// so the harness needs no CLI. Locked down: no network, read-only root filesystem, a small tmpfs work
/// directory, nobody user, all capabilities dropped, no privilege escalation, and memory / CPU / process /
/// time limits. The container is always force-removed afterwards.
/// </summary>
public class ContainerSandbox(SandboxOptions o) : ISandbox
{
    public string Name => "container";
    private readonly HttpClient _http = CreateClient(o.Engine);

    private static HttpClient CreateClient(string engine)
    {
        if (engine.StartsWith("unix://", StringComparison.Ordinal))
        {
            var path = engine["unix://".Length..];
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                },
            };
            return new HttpClient(handler) { BaseAddress = new Uri("http://engine/v1.41/"), Timeout = Timeout.InfiniteTimeSpan };
        }
        return new HttpClient { BaseAddress = new Uri(engine.TrimEnd('/') + "/v1.41/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<SandboxResult> RunAsync(SandboxRun run, CancellationToken ct)
    {
        if (!o.Images.TryGetValue(run.Language, out var image)) throw new SandboxUnavailableException($"No image configured for {run.Language}.");
        string[] cmd = run.Language switch
        {
            "python" => ["python", "-I", "-c", run.Code],
            "node" => ["node", "-e", run.Code],
            _ => throw new SandboxUnavailableException($"Unsupported language: {run.Language}"),
        };
        var spec = new JsonObject
        {
            ["Image"] = image,
            ["Cmd"] = new JsonArray(cmd.Select(c => (JsonNode)c).ToArray()),
            ["User"] = "65534:65534",
            ["WorkingDir"] = "/work",
            ["NetworkDisabled"] = true,
            ["Env"] = new JsonArray("HOME=/work", "PYTHONDONTWRITEBYTECODE=1", "PYTHONUNBUFFERED=1"),
            ["HostConfig"] = new JsonObject
            {
                ["NetworkMode"] = "none",
                ["ReadonlyRootfs"] = true,
                ["Tmpfs"] = new JsonObject { ["/work"] = $"rw,size={o.TmpfsMb}m,mode=1777", ["/tmp"] = $"rw,size={o.TmpfsMb}m,mode=1777" },
                ["Memory"] = (long)o.MemoryMb * 1024 * 1024,
                ["MemorySwap"] = (long)o.MemoryMb * 1024 * 1024,
                ["NanoCpus"] = (long)(o.Cpus * 1_000_000_000),
                ["PidsLimit"] = o.Pids,
                ["CapDrop"] = new JsonArray("ALL"),
                ["SecurityOpt"] = new JsonArray("no-new-privileges"),
            },
        };

        var sw = Stopwatch.StartNew();
        string? id = null;
        try
        {
            id = await CreateAsync(spec, image, ct);
            (await _http.PostAsync($"containers/{id}/start", null, ct)).EnsureSuccessStatusCode();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(run.TimeoutSeconds));
            int exit; bool timedOut = false;
            try
            {
                var waited = await _http.PostAsync($"containers/{id}/wait", null, timeout.Token);
                var body = JsonNode.Parse(await waited.Content.ReadAsStringAsync(timeout.Token));
                exit = body?["StatusCode"]?.GetValue<int>() ?? -1;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true; exit = 124;
                try { await _http.PostAsync($"containers/{id}/kill", null, CancellationToken.None); } catch { }
            }
            var (stdout, stderr, truncated) = await LogsAsync(id, o.MaxOutputBytes, CancellationToken.None);
            if (exit == 137 && !timedOut) stderr += "\n[sandbox] Killed: the program used more than the memory limit.";
            if (timedOut) stderr += $"\n[sandbox] Stopped after {run.TimeoutSeconds}s.";
            return new SandboxResult(exit, stdout, stderr.Trim(), sw.Elapsed.TotalMilliseconds, timedOut, truncated);
        }
        catch (HttpRequestException e) { throw new SandboxUnavailableException("Container engine unreachable: " + e.Message, e); }
        catch (SocketException e) { throw new SandboxUnavailableException("Container engine unreachable: " + e.Message, e); }
        finally
        {
            if (id is not null)
                try { await _http.DeleteAsync($"containers/{id}?force=true&v=true", CancellationToken.None); } catch { }
        }
    }

    private async Task<string> CreateAsync(JsonObject spec, string image, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var resp = await _http.PostAsync($"containers/create?name=sbx-{Guid.NewGuid():N}",
                new StringContent(spec.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode) return JsonNode.Parse(text)!["Id"]!.GetValue<string>();
            if ((int)resp.StatusCode == 404 && attempt == 0)
            {
                // Image not present yet: pull it once (the only time the engine itself touches the network).
                var (name, tag) = image.LastIndexOf(':') is var i and > 0 && !image[i..].Contains('/') ? (image[..i], image[(i + 1)..]) : (image, "latest");
                using var pull = await _http.PostAsync($"images/create?fromImage={Uri.EscapeDataString(name)}&tag={Uri.EscapeDataString(tag)}", null, ct);
                await pull.Content.ReadAsStringAsync(ct);   // the stream ends when the pull finishes
                continue;
            }
            throw new SandboxUnavailableException($"Couldn't create the sandbox ({(int)resp.StatusCode}): {text}");
        }
        throw new SandboxUnavailableException("Couldn't create the sandbox.");
    }

    /// <summary>Reads the container's output. Without a TTY the engine multiplexes it: 8-byte frame headers, stream 1 = stdout, 2 = stderr.</summary>
    private async Task<(string Stdout, string Stderr, bool Truncated)> LogsAsync(string id, int max, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"containers/{id}/logs?stdout=true&stderr=true", ct);
        var data = await resp.Content.ReadAsByteArrayAsync(ct);
        var outB = new MemoryStream(); var errB = new MemoryStream();
        bool truncated = false;
        int pos = 0;
        while (pos + 8 <= data.Length)
        {
            int stream = data[pos];
            int len = (data[pos + 4] << 24) | (data[pos + 5] << 16) | (data[pos + 6] << 8) | data[pos + 7];
            pos += 8;
            if (len < 0 || pos + len > data.Length) break;
            var target = stream == 2 ? errB : outB;
            int room = Math.Max(0, max - (int)target.Length);
            if (len > room) truncated = true;
            target.Write(data, pos, Math.Min(len, room));
            pos += len;
        }
        if (pos == 0 && data.Length > 0) outB.Write(data, 0, Math.Min(max, data.Length));   // engines that don't multiplex
        return (Encoding.UTF8.GetString(outB.ToArray()), Encoding.UTF8.GetString(errB.ToArray()), truncated);
    }
}

/// <summary>
/// Any hosted or self-hosted sandbox service: POST {language, code, timeout_seconds} → {exit_code, stdout, stderr}.
/// Put a thin adapter in front of a provider whose API differs.
/// </summary>
public class RemoteSandbox(HttpClient http, SandboxOptions o) : ISandbox
{
    public string Name => "remote";

    public async Task<SandboxResult> RunAsync(SandboxRun run, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(o.RemoteUrl)) throw new SandboxUnavailableException("Sandbox RemoteUrl isn't set.");
        using var req = new HttpRequestMessage(HttpMethod.Post, o.RemoteUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { language = run.Language, code = run.Code, timeout_seconds = run.TimeoutSeconds }), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(o.RemoteApiKeyEnv) && Environment.GetEnvironmentVariable(o.RemoteApiKeyEnv) is { Length: > 0 } key)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(run.TimeoutSeconds + 15));
        try
        {
            using var resp = await http.SendAsync(req, timeout.Token);
            var text = await resp.Content.ReadAsStringAsync(timeout.Token);
            if (!resp.IsSuccessStatusCode) throw new SandboxUnavailableException($"Sandbox service returned {(int)resp.StatusCode}.");
            var j = JsonNode.Parse(text)!;
            string Cap(string? s) => s is null ? "" : s.Length > o.MaxOutputBytes ? s[..o.MaxOutputBytes] : s;
            var stdout = j["stdout"]?.GetValue<string>(); var stderr = j["stderr"]?.GetValue<string>();
            return new SandboxResult(j["exit_code"]?.GetValue<int>() ?? -1, Cap(stdout), Cap(stderr), sw.Elapsed.TotalMilliseconds,
                j["timed_out"]?.GetValue<bool>() ?? false, (stdout?.Length ?? 0) > o.MaxOutputBytes || (stderr?.Length ?? 0) > o.MaxOutputBytes);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new SandboxUnavailableException("Sandbox service unreachable: " + e.Message, e);
        }
    }
}

/// <summary>
/// The guardrails that let agents run code without anyone watching: a concurrency cap (bulkhead),
/// a per-agency daily quota, size and time limits, metrics, and an audit event for every run.
/// </summary>
public class SandboxGuard(ISandbox inner, SandboxOptions o, IServiceScopeFactory scopes, Outbox outbox, TimeProvider clock)
{
    private static readonly Meter Meter = new(Telemetry.MeterName);
    private static readonly Counter<long> Runs = Meter.CreateCounter<long>("sandbox.runs");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("sandbox.duration", "ms");

    private readonly SemaphoreSlim _slots = new(o.MaxConcurrent, o.MaxConcurrent);
    private readonly ConcurrentDictionary<(Guid, DateOnly), int> _today = new();

    public string Mode => o.Mode;
    public SandboxOptions Options => o;
    public int RunsToday(Guid tenantId) => _today.GetValueOrDefault((tenantId, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)));

    public async Task<SandboxResult> RunAsync(Guid tenantId, Guid? jobId, string language, string code, int? timeoutSeconds, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(code) > o.MaxCodeBytes) throw new SandboxUnavailableException($"Code is larger than {o.MaxCodeBytes / 1000} KB.");
        var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var used = _today.AddOrUpdate((tenantId, day), 1, (_, n) => n + 1);
        if (used > o.DailyRunsPerTenant)
        {
            _today.AddOrUpdate((tenantId, day), 0, (_, n) => n - 1);
            throw new SandboxUnavailableException($"Today's sandbox limit ({o.DailyRunsPerTenant} runs) is used up.");
        }
        var timeout = Math.Clamp(timeoutSeconds ?? o.DefaultTimeoutSeconds, 1, o.MaxTimeoutSeconds);

        if (!await _slots.WaitAsync(TimeSpan.FromSeconds(60), ct)) throw new SandboxUnavailableException("All sandboxes are busy; try again shortly.");
        SandboxResult result;
        try { result = await inner.RunAsync(new SandboxRun(language, code, timeout), ct); }
        finally { _slots.Release(); }

        Runs.Add(1, new KeyValuePair<string, object?>("language", language));
        Duration.Record(result.DurationMs);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarnessDbContext>();
        outbox.Add(db, tenantId, "sandbox.run", new
        {
            jobId, language, exitCode = result.ExitCode, durationMs = (int)result.DurationMs, result.TimedOut,
            codeSha256 = Security.Sha256(code)[..16], codeBytes = code.Length,
        });
        await db.SaveChangesAsync(CancellationToken.None);
        return result;
    }
}

/// <summary>Agent tool: run a short Python or Node.js program in a locked-down sandbox and see its output.</summary>
public class RunCodeTool(SandboxGuard guard) : IAgentTool
{
    public string Name => "run_code";
    public string Description =>
        "Runs a short Python 3.12 or Node.js 22 program in an isolated sandbox with NO internet, a temporary /work folder, and " +
        $"limits ({guard.Options.MemoryMb} MB memory, {guard.Options.DefaultTimeoutSeconds}s default). Use it to check your work: validate JSON-LD/XML, " +
        "test a script or regex, compute numbers, transform data you were given. Returns exit_code, stdout and stderr.";
    public IReadOnlyDictionary<string, object> Properties => new Dictionary<string, object>
    {
        ["language"] = new { type = "string", @enum = new[] { "python", "node" } },
        ["code"] = new { type = "string", description = "The full program. Print what you need to see." },
        ["timeout_seconds"] = new { type = "integer", minimum = 1, maximum = guard.Options.MaxTimeoutSeconds },
    };
    public string[] Required => ["language", "code"];

    public async Task<string> ExecuteAsync(ToolContext ctx, JsonElement input, CancellationToken ct)
    {
        if (!guard.Options.Roles.Contains(ctx.Job.AgentRole)) throw new ToolException($"The {ctx.Job.AgentRole} role isn't allowed to run code.");
        var language = Input.Str(input, "language", 10).Trim().ToLowerInvariant();
        var code = Input.Str(input, "code", guard.Options.MaxCodeBytes);
        int? timeout = input.TryGetProperty("timeout_seconds", out var t) && t.TryGetInt32(out var v) ? v : null;
        try
        {
            var r = await guard.RunAsync(ctx.Job.TenantId, ctx.Job.Id, language, code, timeout, ct);
            return JsonSerializer.Serialize(new
            {
                exit_code = r.ExitCode, stdout = r.Stdout, stderr = r.Stderr, duration_ms = (int)r.DurationMs, timed_out = r.TimedOut,
                truncated = r.Truncated,
            }, OpenAiChatAgentRunner.Json);
        }
        catch (SandboxUnavailableException e) { throw new ToolException(e.Message); }
    }
}
