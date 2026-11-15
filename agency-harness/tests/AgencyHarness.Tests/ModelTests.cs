using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgencyHarness.Agents;
using AgencyHarness.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgencyHarness.Tests;

/// <summary>Plays an OpenAI-compatible chat server: each request gets the next scripted reply for its model.</summary>
internal class FakeChatServer : HttpMessageHandler
{
    public readonly ConcurrentQueue<JsonObject> Requests = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Func<JsonObject, HttpResponseMessage>>> _script = new();

    public FakeChatServer On(string model, params Func<JsonObject, HttpResponseMessage>[] replies)
    {
        var q = _script.GetOrAdd(model, _ => new());
        foreach (var r in replies) q.Enqueue(r);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync(ct))!.AsObject();
        body["_url"] = req.RequestUri!.ToString();
        body["_auth"] = req.Headers.Authorization?.ToString();
        Requests.Enqueue(body);
        var model = body["model"]!.GetValue<string>();
        return _script.TryGetValue(model, out var q) && q.TryDequeue(out var reply)
            ? reply(body) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("no script") };
    }

    public static HttpResponseMessage ToolCalls(params (string Name, object Args)[] calls) => Ok(new
    {
        choices = new[] { new { message = new {
            role = "assistant", content = "",
            tool_calls = calls.Select((c, i) => new { id = $"call_{i}", type = "function", function = new { name = c.Name, arguments = JsonSerializer.Serialize(c.Args) } }).ToArray(),
        } } },
        usage = new { prompt_tokens = 100, completion_tokens = 20 },
    });

    public static HttpResponseMessage Text(string content) => Ok(new
    {
        choices = new[] { new { message = new { role = "assistant", content } } },
        usage = new { prompt_tokens = 50, completion_tokens = 30 },
    });

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("{\"error\":\"nope\"}") };

    private static HttpResponseMessage Ok(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
}

public class ModelTests
{
    private static HarnessFactory App(FakeChatServer server, Dictionary<string, string> settings)
    {
        var app = new HarnessFactory();
        app.Settings["Harness:ForceScriptedRunner"] = "false";
        foreach (var (k, v) in settings) app.Settings[k] = v;
        app.ConfigureServices = s => s.AddHttpClient("models").ConfigurePrimaryHttpMessageHandler(() => server);
        return app;
    }

    private static async Task<JsonElement> RunJobAsync(HarnessFactory app, string role = "copywriter")
    {
        var (c, _) = await app.NewTenantAsync();
        var projectId = await HarnessFactory.CreateProjectAsync(c);
        var job = await (await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role, instructions = "Write 3 Onam taglines" })).Content.ReadFromJsonAsync<JsonElement>();
        await app.DrainAsync();
        var id = job.GetProperty("id").GetGuid();
        var state = await c.GetFromJsonAsync<JsonElement>($"/v1/jobs/{id}");
        var delivs = await c.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/deliverables");
        return JsonSerializer.SerializeToElement(new { state, delivs });
    }

    [Fact]
    public async Task Agent_runs_on_a_local_openai_compatible_model_with_native_tool_calls()
    {
        var server = new FakeChatServer().On("gemma3:4b",
            _ => FakeChatServer.ToolCalls(("get_client_brief", new { })),
            _ => FakeChatServer.ToolCalls(("save_deliverable", new { title = "Onam taglines", content = "1. ഓണം…\n2. Sadya time!\n3. Pookalam vibes" })),
            _ => FakeChatServer.Text("Saved three warm Onam taglines. Ready for review."));
        using var app = App(server, new()
        {
            ["Harness:Models:Profiles:local:BaseUrl"] = "http://127.0.0.1:11434/v1",
            ["Harness:Models:Profiles:local:Model"] = "gemma3:4b",
            ["Harness:Models:Profiles:local:ReasoningEffort"] = "low",
        });
        var r = await RunJobAsync(app);
        Assert.Equal("low", server.Requests.First()["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("Succeeded", r.GetProperty("state").GetProperty("status").GetString());
        Assert.Equal("local (gemma3:4b)", r.GetProperty("state").GetProperty("model").GetString());
        Assert.Equal("Onam taglines", r.GetProperty("delivs")[0].GetProperty("title").GetString());

        var first = server.Requests.First();
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", first["_url"]!.GetValue<string>());
        Assert.Contains("save_deliverable", first["tools"]!.ToJsonString());
        // The brief came back as readable text (not \u-escaped) in the tool result.
        var third = server.Requests.ElementAt(1)["messages"]!.ToJsonString();
        Assert.Contains("Bloom Coffee", third);
    }

    [Fact]
    public async Task Small_models_without_tool_calling_use_the_json_action_protocol()
    {
        var server = new FakeChatServer().On("tiny",
            _ => FakeChatServer.Text("Sure! ```json\n{\"tool\": \"get_client_brief\", \"input\": {}}\n```"),
            _ => FakeChatServer.Text("{\"tool\": \"save_deliverable\", \"input\": {\"title\": \"Taglines\", \"content\": \"Sadya season is here.\"}}"),
            _ => FakeChatServer.Text("{\"final\": \"Wrote the taglines.\"}"));
        using var app = App(server, new()
        {
            ["Harness:Models:Profiles:tiny:BaseUrl"] = "http://127.0.0.1:8080/v1",
            ["Harness:Models:Profiles:tiny:Model"] = "tiny",
            ["Harness:Models:Profiles:tiny:Tools"] = "false",
        });
        var r = await RunJobAsync(app);
        Assert.Equal("Succeeded", r.GetProperty("state").GetProperty("status").GetString());
        Assert.Equal("Wrote the taglines.", r.GetProperty("state").GetProperty("output").GetString());
        Assert.Equal("Sadya season is here.", r.GetProperty("delivs")[0].GetProperty("content").GetString());
        Assert.Null(server.Requests.First()["tools"]);
        Assert.Null(server.Requests.First()["reasoning_effort"]);   // not sent unless configured
        Assert.Contains("exactly ONE JSON object", server.Requests.First()["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Falls_back_down_the_chain_when_a_model_is_down_or_rejects_the_request()
    {
        var server = new FakeChatServer()
            .On("local-model", _ => FakeChatServer.Status(HttpStatusCode.ServiceUnavailable))
            .On("picky-model", _ => FakeChatServer.Status(HttpStatusCode.BadRequest))
            .On("cloud-model", _ => FakeChatServer.Text("Here are three taglines: Onam is home. Sadya is love. Pookalam is joy."));
        using var app = App(server, new()
        {
            ["Harness:Models:Profiles:local:BaseUrl"] = "http://127.0.0.1:11434/v1",
            ["Harness:Models:Profiles:local:Model"] = "local-model",
            ["Harness:Models:Profiles:picky:BaseUrl"] = "https://picky.example/v1",
            ["Harness:Models:Profiles:picky:Model"] = "picky-model",
            ["Harness:Models:Profiles:cloud:BaseUrl"] = "https://cloud.example/v1",
            ["Harness:Models:Profiles:cloud:Model"] = "cloud-model",
            ["Harness:Models:Routing:default:0"] = "local",
            ["Harness:Models:Routing:default:1"] = "picky",
            ["Harness:Models:Routing:default:2"] = "cloud",
        });
        var r = await RunJobAsync(app);
        Assert.Equal("Succeeded", r.GetProperty("state").GetProperty("status").GetString());
        Assert.Equal("cloud (cloud-model)", r.GetProperty("state").GetProperty("model").GetString());
        // The model answered without calling save_deliverable; its answer is kept as the deliverable.
        Assert.Contains("Onam is home", r.GetProperty("delivs")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task When_every_model_is_down_the_job_retries_instead_of_failing()
    {
        var server = new FakeChatServer();   // every request gets 503
        using var app = App(server, new()
        {
            ["Harness:Models:Profiles:local:BaseUrl"] = "http://127.0.0.1:11434/v1",
            ["Harness:Models:Profiles:local:Model"] = "x",
        });
        var (c, _) = await app.NewTenantAsync();
        var projectId = await HarnessFactory.CreateProjectAsync(c);
        var job = await (await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "seo", instructions = "Keywords" })).Content.ReadFromJsonAsync<JsonElement>();
        await app.Processor.ProcessNextAsync("w", default);
        var state = await c.GetFromJsonAsync<JsonElement>($"/v1/jobs/{job.GetProperty("id").GetGuid()}");
        Assert.Equal("Failed", state.GetProperty("status").GetString());   // "Failed" = waiting to retry
        Assert.Contains("Every model failed", state.GetProperty("lastError").GetString());
    }

    [Fact]
    public void Imports_models_from_an_opencode_config()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opencode-{Guid.NewGuid():n}.jsonc");
        File.WriteAllText(path, """
            // my OpenCode setup
            {
              "$schema": "https://opencode.ai/config.json",
              "model": "ollama/gemma3:4b",
              "providers": {
                "acme": {
                  "env": ["ACME_API_KEY"],
                  "package": "@opencode/ai/providers/openai-compatible",
                  "settings": { "baseURL": "https://llm.acme.example/v1" },
                  "models": { "qwen3-coder": { "name": "Qwen 3 Coder" } },
                },
                "openrouter": {
                  "settings": { "apiKey": "{env:MY_OPENROUTER_KEY}" },
                  "models": { "google/gemma-4-e4b-it": {} },
                },
                "anthropic": { "models": { "claude-sonnet-5-5": {} } },
              },
            }
            """);
        var r = OpenCodeImport.Load(path);
        Assert.Equal("opencode:ollama/gemma3:4b", r.DefaultModel);
        Assert.Equal("http://127.0.0.1:11434/v1", r.Profiles["opencode:ollama/gemma3:4b"].BaseUrl);
        Assert.Equal("gemma3:4b", r.Profiles["opencode:ollama/gemma3:4b"].Model);
        Assert.Equal("https://llm.acme.example/v1", r.Profiles["opencode:acme/qwen3-coder"].BaseUrl);
        Assert.Equal("ACME_API_KEY", r.Profiles["opencode:acme/qwen3-coder"].ApiKeyEnv);
        var or = r.Profiles["opencode:openrouter/google/gemma-4-e4b-it"];
        Assert.Equal("https://openrouter.ai/api/v1", or.BaseUrl);
        Assert.Equal("MY_OPENROUTER_KEY", or.ApiKeyEnv);   // the variable name, never the value
        Assert.Null(or.ApiKey);
        Assert.False(r.Profiles.ContainsKey("opencode:anthropic/claude-sonnet-5-5"));
        Assert.Contains(r.Notes, n => n.Contains("claude"));
    }

    [Fact]
    public async Task OpenCode_default_model_runs_the_agents_and_models_endpoint_reports_the_chain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opencode-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, """{ "model": "ollama/qwen3:8b" }""");
        var server = new FakeChatServer().On("qwen3:8b", _ => FakeChatServer.Text("Done: three taglines about sadya."));
        using var app = App(server, new() { ["Harness:Models:OpenCodeConfig"] = path });
        var r = await RunJobAsync(app);
        Assert.Equal("opencode:ollama/qwen3:8b (qwen3:8b)", r.GetProperty("state").GetProperty("model").GetString());

        var (c, _) = await app.NewTenantAsync("Second");
        var models = await c.GetFromJsonAsync<JsonElement>("/v1/models");
        Assert.Equal("models", models.GetProperty("runner").GetString());
        Assert.Equal("opencode", models.GetProperty("routing").GetProperty("*")[0].GetString());
        var m = models.GetProperty("models").EnumerateArray().First(x => x.GetProperty("name").GetString() == "opencode");
        Assert.True(m.GetProperty("local").GetBoolean());
        Assert.Equal("http://127.0.0.1:11434", m.GetProperty("endpoint").GetString());
    }

    [Fact]
    public void Json_action_parser_handles_fences_chatter_and_nested_braces()
    {
        var a = OpenAiChatAgentRunner.ParseAction("ok here: {\"tool\":\"save_deliverable\",\"input\":{\"title\":\"x {y}\",\"content\":\"}\"}} thanks");
        Assert.Equal("save_deliverable", a!.Tool);
        Assert.Contains("x {y}", a.Input);
        Assert.Equal("All done", OpenAiChatAgentRunner.ParseAction("```json\n{\"final\": \"All done\"}\n```")!.Final);
        Assert.Null(OpenAiChatAgentRunner.ParseAction("no json here"));
    }
}
