using System.Net.Http.Json;
using System.Text.Json;
using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using AgencyHarness.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgencyHarness.Tests;

public class SwarmTests
{
    private static AgendaScheduler Scheduler(HarnessFactory app) => app.Services.GetRequiredService<AgendaScheduler>();

    /// <summary>Alternate scheduler ticks and queue drains until the agenda settles (or a cap is hit).</summary>
    private static async Task<List<string>> RunAgendaAsync(HarnessFactory app, HttpClient c, Guid agendaId, Action<JsonElement>? onTick = null)
    {
        var log = new List<string>();
        for (int i = 0; i < 30; i++)
        {
            await Scheduler(app).TickAsync(default);
            var a = await c.GetFromJsonAsync<JsonElement>($"/v1/agendas/{agendaId}");
            onTick?.Invoke(a);
            var status = a.GetProperty("agenda").GetProperty("status").GetString()!;
            log.Add(status + ":" + string.Join(",", a.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("key").GetString() + "=" + t.GetProperty("status").GetString())));
            if (status is "Done" or "Failed") break;
            await app.DrainAsync();
        }
        return log;
    }

    private static async Task<(HttpClient C, Guid ProjectId)> SetupAsync(HarnessFactory app, string brief = "Warm, witty voice.")
    {
        var (c, _) = await app.NewTenantAsync();
        var client = await (await c.PostAsJsonAsync("/v1/clients", new { name = "ZUNVI", brief })).Content.ReadFromJsonAsync<JsonElement>();
        var project = await (await c.PostAsJsonAsync($"/v1/clients/{client.GetProperty("id").GetGuid()}/projects", new { name = "30-day growth sprint", goal = "Traffic" })).Content.ReadFromJsonAsync<JsonElement>();
        return (c, project.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Agenda_is_planned_then_run_as_a_dependency_graph_by_a_swarm()
    {
        using var app = new HarnessFactory();
        var (c, projectId) = await SetupAsync(app);
        var created = await c.PostAsJsonAsync($"/v1/projects/{projectId}/agendas", new { title = "ZUNVI 30-day sprint", plan = "Days 1-7 technical, then content, then outreach", swarmSize = 3 });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, created.StatusCode);
        var agendaId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        int maxActive = 0;
        var log = await RunAgendaAsync(app, c, agendaId, a => maxActive = Math.Max(maxActive, a.GetProperty("agenda").GetProperty("counts").GetProperty("active").GetInt32()));

        var final = await c.GetFromJsonAsync<JsonElement>($"/v1/agendas/{agendaId}");
        Assert.Equal("Done", final.GetProperty("agenda").GetProperty("status").GetString());
        Assert.Equal(4, final.GetProperty("agenda").GetProperty("counts").GetProperty("done").GetInt32());
        // Dependencies were respected: outreach ran only after content and geo-faqs, which ran in parallel after audit.
        var outreachStart = log.FindIndex(l => l.Contains("outreach=Running"));
        Assert.True(outreachStart > log.FindIndex(l => l.Contains("content=Done") && l.Contains("geo-faqs=Done")) - 1);
        Assert.Contains(log, l => l.Contains("content=Running") && l.Contains("geo-faqs=Running"));
        Assert.True(maxActive is >= 2 and <= 3);

        var events = await c.GetFromJsonAsync<JsonElement>("/v1/events?limit=200");
        var types = events.EnumerateArray().Select(e => e.GetProperty("eventType").GetString()).ToList();
        Assert.Contains("agenda.running", types);
        Assert.Equal(4, types.Count(t => t == "agenda.task.done"));
        Assert.Contains("agenda.done", types);
    }

    [Fact]
    public async Task Swarm_size_caps_how_many_tasks_run_at_once()
    {
        using var app = new HarnessFactory();
        var (c, projectId) = await SetupAsync(app);
        var agendaId = (await (await c.PostAsJsonAsync($"/v1/projects/{projectId}/agendas", new { title = "One at a time", plan = "x", swarmSize = 1 }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        int maxActive = 0;
        await RunAgendaAsync(app, c, agendaId, a => maxActive = Math.Max(maxActive, a.GetProperty("agenda").GetProperty("counts").GetProperty("active").GetInt32()));
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task Gate_failure_triggers_a_revision_and_the_lesson_reaches_the_next_attempt()
    {
        var server = new FakeChatServer().On("m",
            _ => FakeChatServer.Text("Discover the most authentic Toronto storefronts on ZUNVI. " + new string('x', 260)),
            req => FakeChatServer.Text("Discover Toronto's most-visited virtual storefronts on ZUNVI this week. " + new string('y', 260)));
        using var app = new HarnessFactory();
        app.Settings["Harness:ForceScriptedRunner"] = "false";
        app.Settings["Harness:Models:Profiles:m:BaseUrl"] = "http://127.0.0.1:11434/v1";
        app.Settings["Harness:Models:Profiles:m:Model"] = "m";
        app.ConfigureServices = s => s.AddHttpClient("models").ConfigurePrimaryHttpMessageHandler(() => server);
        var (c, projectId) = await SetupAsync(app, "Canadian business directory.\nAvoid: the word \"authentic\", hype.");

        // Insert a ready-made one-task agenda (no planner) with review off, to isolate the gate → revision loop.
        Guid agendaId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarnessDbContext>();
            var tenantId = await db.Projects.Where(p => p.Id == projectId).Select(p => p.TenantId).FirstAsync();
            var agenda = new Agenda { TenantId = tenantId, ProjectId = projectId, Title = "Hub copy", Plan = "x", Status = AgendaStatus.Running, ReviewGate = false };
            db.Agendas.Add(agenda);
            db.AgendaTasks.Add(new AgendaTask { TenantId = tenantId, AgendaId = agenda.Id, Key = "toronto-hub", Title = "Toronto hub intro", Role = "copywriter", Instructions = "Write the Toronto hub intro." });
            await db.SaveChangesAsync();
            agendaId = agenda.Id;
        }

        await RunAgendaAsync(app, c, agendaId);
        var a = await c.GetFromJsonAsync<JsonElement>($"/v1/agendas/{agendaId}");
        var task = a.GetProperty("tasks")[0];
        Assert.Equal("Done", task.GetProperty("status").GetString());
        Assert.Equal(1, task.GetProperty("revisions").GetInt32());

        // The second attempt was told what went wrong, and the learned lesson was in its instructions.
        var second = server.Requests.Last();
        var system = second["messages"]![0]!["content"]!.GetValue<string>();
        var user = second["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("Lessons from earlier work", system);
        Assert.Contains("authentic", system);
        Assert.Contains("revision 1", user);
        Assert.Contains("Brand", system.Replace("brief exactly", "Brand"));   // the brand-voice skill was applied

        var lessons = await c.GetFromJsonAsync<JsonElement>("/v1/lessons");
        Assert.Contains(lessons.EnumerateArray(), l => l.GetProperty("text").GetString()!.Contains("authentic") && l.GetProperty("source").GetString() == "gate");
    }

    [Fact]
    public async Task Feedback_becomes_a_lesson_and_a_lesson_can_become_a_skill()
    {
        using var app = new HarnessFactory();
        var (c, projectId) = await SetupAsync(app);
        await c.PostAsJsonAsync($"/v1/projects/{projectId}/jobs", new { role = "copywriter", instructions = "Taglines" });
        await app.DrainAsync();
        var d = (await c.GetFromJsonAsync<JsonElement>($"/v1/projects/{projectId}/deliverables"))[0];

        var fb = await c.PostAsJsonAsync($"/v1/deliverables/{d.GetProperty("id").GetGuid()}/feedback", new { rating = -1, note = "Mention the city name in the first sentence." });
        Assert.True((await fb.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("learned").GetBoolean());
        var lesson = (await c.GetFromJsonAsync<JsonElement>("/v1/lessons")).EnumerateArray().Single(l => l.GetProperty("source").GetString() == "feedback");
        Assert.Equal("copywriter", lesson.GetProperty("role").GetString());
        Assert.Equal("ZUNVI", lesson.GetProperty("client").GetString());

        // Same feedback again reinforces instead of duplicating.
        await c.PostAsJsonAsync($"/v1/deliverables/{d.GetProperty("id").GetGuid()}/feedback", new { rating = -1, note = "Mention the city name in the first sentence." });
        var again = (await c.GetFromJsonAsync<JsonElement>("/v1/lessons")).EnumerateArray().Where(l => l.GetProperty("source").GetString() == "feedback").ToList();
        Assert.Single(again);
        Assert.Equal(2, again[0].GetProperty("seen").GetInt32());

        var promoted = await c.PostAsync($"/v1/lessons/{lesson.GetProperty("id").GetGuid()}/promote", null);
        Assert.Equal(System.Net.HttpStatusCode.Created, promoted.StatusCode);
        var skills = await c.GetFromJsonAsync<JsonElement>("/v1/skills");
        Assert.Contains(skills.EnumerateArray(), s => s.GetProperty("source").GetString() == "learned" && s.GetProperty("editable").GetBoolean());
        Assert.Contains(skills.EnumerateArray(), s => s.GetProperty("name").GetString() == "casl-outreach" && !s.GetProperty("editable").GetBoolean());
        Assert.Contains(skills.EnumerateArray(), s => s.GetProperty("name").GetString() == "deliverable-format" && s.GetProperty("uses").GetInt32() > 0);
    }

    [Fact]
    public async Task Schema_sync_adds_missing_tables_to_an_existing_database_and_is_idempotent()
    {
        using var app = new HarnessFactory();
        await app.NewTenantAsync();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarnessDbContext>();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Lessons\"");     // simulate a database from an older version
        SchemaSync.Apply(db);
        SchemaSync.Apply(db);                                               // running twice is harmless
        Assert.Equal(0, await db.Lessons.CountAsync());
        Assert.True(await db.Tenants.AnyAsync());                           // existing data untouched
    }
}

public class QualityGateTests
{
    [Fact]
    public void Avoid_terms_are_read_from_the_brief()
    {
        var terms = QualityGate.AvoidTerms("Family-run restaurant.\nAvoid: spicy-food clichés, the word \"authentic\".\nAudience: families").ToList();
        Assert.Contains("authentic", terms);
        Assert.Contains("spicy-food clichés", terms);
        Assert.DoesNotContain(terms, t => t.Contains("Audience"));
    }
}
