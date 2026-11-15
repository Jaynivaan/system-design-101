using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AgencyHarness.Tests;

/// <summary>Drives the harness the way OpenCode or Claude Code would: over MCP Streamable HTTP.</summary>
public class McpTests
{
    private static async Task<McpClient> ConnectAsync(HarnessFactory app, HttpClient authed)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(authed.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, authed, ownsHttpClient: false);
        return await McpClient.CreateAsync(transport);
    }

    private static JsonElement Json(CallToolResult r)
    {
        var text = r.Content.OfType<TextContentBlock>().First().Text;
        Assert.False(r.IsError == true, text);
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task Mcp_client_can_list_tools_create_a_client_queue_work_and_read_deliverables()
    {
        using var app = new HarnessFactory();
        var (http, _) = await app.NewTenantAsync();
        await using var mcp = await ConnectAsync(app, http);

        var tools = (await mcp.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("queue_job", tools);
        Assert.Contains("get_deliverables", tools);
        Assert.Contains("list_clients", tools);

        var created = Json(await mcp.CallToolAsync("create_client", new Dictionary<string, object?>
        {
            ["name"] = "Northwind Bikes", ["brief"] = "Plain-spoken. Commuters 25-45.", ["projectName"] = "Summer campaign", ["projectGoal"] = "+30% rentals",
        }));
        var projectId = created.GetProperty("projectId").GetString();

        var args = new Dictionary<string, object?> { ["projectId"] = projectId, ["role"] = "copywriter", ["instructions"] = "Three taglines", ["idempotencyKey"] = "mcp-1" };
        var queued = Json(await mcp.CallToolAsync("queue_job", args));
        Assert.Equal("queued", queued.GetProperty("status").GetString());
        var again = Json(await mcp.CallToolAsync("queue_job", args));
        Assert.Equal("already_queued", again.GetProperty("status").GetString());
        Assert.Equal(queued.GetProperty("jobId").GetString(), again.GetProperty("jobId").GetString());

        await app.DrainAsync();

        var job = Json(await mcp.CallToolAsync("get_job", new Dictionary<string, object?> { ["jobId"] = queued.GetProperty("jobId").GetString() }));
        Assert.Equal("Succeeded", job.GetProperty("status").GetString());
        var delivs = Json(await mcp.CallToolAsync("get_deliverables", new Dictionary<string, object?> { ["projectId"] = projectId }));
        Assert.Equal(1, delivs.GetArrayLength());
        Assert.Contains("Northwind Bikes", delivs[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Mcp_tool_errors_are_reported_to_the_model_not_thrown()
    {
        using var app = new HarnessFactory();
        var (http, _) = await app.NewTenantAsync();
        await using var mcp = await ConnectAsync(app, http);
        var r = await mcp.CallToolAsync("get_job", new Dictionary<string, object?> { ["jobId"] = Guid.NewGuid().ToString() });
        Assert.True(r.IsError);
        Assert.Contains("not found", r.Content.OfType<TextContentBlock>().First().Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mcp_requires_an_api_key_and_accepts_bearer_tokens()
    {
        using var app = new HarnessFactory();
        var anon = app.CreateClient();
        var resp = await anon.PostAsync("/mcp", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        var (keyed, _) = await app.NewTenantAsync();
        var key = keyed.DefaultRequestHeaders.GetValues("X-Api-Key").Single();
        var bearer = app.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        await using var mcp = await ConnectAsync(app, bearer);
        Assert.NotEmpty(await mcp.ListToolsAsync());
    }
}
