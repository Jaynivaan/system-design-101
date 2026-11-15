using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgencyHarness.Agents;
using AgencyHarness.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgencyHarness.Tests;

public class OpenModelTests
{
    /// <summary>Plays back canned chat-completions responses and records each request body.</summary>
    private class ScriptedServer(params string[] responses) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        private int _next;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[_next++], Encoding.UTF8, "application/json"),
            };
        }
    }

    private const string ToolCall = """
        {"model":"gemma4:e4b","choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":"",
          "tool_calls":[{"id":"call_1","type":"function","function":{"name":"save_deliverable",
            "arguments":"{\"title\":\"KPIs\",\"content\":\"Track bookings per week.\",\"kind\":\"other\"}"}}]}}],
         "usage":{"prompt_tokens":120,"completion_tokens":40}}
        """;

    private const string Done = """
        {"model":"gemma4:e4b","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"Saved the KPI list."}}],
         "usage":{"prompt_tokens":200,"completion_tokens":10}}
        """;

    [Fact]
    public async Task Open_model_runs_tools_and_finishes_at_zero_cost()
    {
        var server = new ScriptedServer(ToolCall, Done);
        var runner = new OpenModelAgentRunner(new HttpClient(server),
            new OpenModelOptions { BaseUrl = "http://model.test/v1" },
            new CircuitBreaker(5, TimeSpan.FromSeconds(30), TimeProvider.System),
            NullLogger<OpenModelAgentRunner>.Instance);
        using var app = new HarnessFactory { RunnerOverride = runner };
        var (c, _) = await app.NewTenantAsync();
        var projectId = await HarnessFactory.CreateProjectAsync(c);
        var created = await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "analyst", instructions = "KPIs" });
        var jobId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await app.DrainAsync();

        var job = await c.GetFromJsonAsync<JsonElement>($"/v1/jobs/{jobId}");
        Assert.Equal("Succeeded", job.GetProperty("status").GetString());
        Assert.Equal(0m, job.GetProperty("costUsd").GetDecimal());

        var deliverables = await c.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/deliverables");
        Assert.Equal("Track bookings per week.", deliverables[0].GetProperty("content").GetString());

        // The second request carries the tool call and its result back to the model.
        var second = server.Requests[1].GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("tool", second[^1].GetProperty("role").GetString());
        Assert.Equal("call_1", second[^1].GetProperty("tool_call_id").GetString());
    }
}
