using AgencyHarness.Agents;
using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgencyHarness.Api;

public record CreateAgendaRequest(string Title, string Plan, int? SwarmSize, bool? ReviewGate);
public record SwarmSizeRequest(int Size);
public record SkillRequest(string Title, string Body, string? Roles, string? Triggers, bool? Always, bool? Active);
public record FeedbackRequest(int Rating, string? Note);

public static class SwarmEndpoints
{
    public static void MapSwarm(this RouteGroupBuilder v1)
    {
        // ───── Agendas ─────
        v1.MapPost("/projects/{projectId:guid}/agendas", async (Guid projectId, CreateAgendaRequest req, HttpContext http, HarnessDbContext db, Outbox outbox, AgendaOptionsView opts) =>
        {
            var t = http.Tenant();
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Length > 160) errors["title"] = ["Required, up to 160 characters."];
            if (string.IsNullOrWhiteSpace(req.Plan) || req.Plan.Length > 30_000) errors["plan"] = ["Required, up to 30,000 characters."];
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.TenantId == t.Id)) return Results.NotFound();
            var agenda = new Agenda
            {
                TenantId = t.Id, ProjectId = projectId, Title = req.Title.Trim(), Plan = req.Plan.Trim(),
                SwarmSize = Math.Clamp(req.SwarmSize ?? 3, 1, opts.MaxSwarmSize), ReviewGate = req.ReviewGate ?? true,
            };
            db.Agendas.Add(agenda);
            outbox.Add(db, t.Id, "agenda.created", new { agendaId = agenda.Id, agenda.Title, projectId, agenda.SwarmSize });
            await db.SaveChangesAsync();
            return Results.Accepted($"/v1/agendas/{agenda.Id}", AgendaView(agenda, []));
        });

        v1.MapGet("/agendas", async (HttpContext http, HarnessDbContext db, Guid? projectId) =>
        {
            var tid = http.Tenant().Id;
            var q = db.Agendas.AsNoTracking().Where(a => a.TenantId == tid);
            if (projectId is { } pid) q = q.Where(a => a.ProjectId == pid);
            var agendas = await q.OrderByDescending(a => a.CreatedAt).Take(50).ToListAsync();
            var ids = agendas.Select(a => a.Id).ToList();
            var tasks = await db.AgendaTasks.AsNoTracking().Where(x => ids.Contains(x.AgendaId)).ToListAsync();
            return Results.Ok(agendas.Select(a => AgendaView(a, tasks.Where(x => x.AgendaId == a.Id).ToList())));
        });

        v1.MapGet("/agendas/{id:guid}", async (Guid id, HttpContext http, HarnessDbContext db) =>
        {
            var tid = http.Tenant().Id;
            var a = await db.Agendas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid);
            if (a is null) return Results.NotFound();
            var tasks = await db.AgendaTasks.AsNoTracking().Where(x => x.AgendaId == id).OrderBy(x => x.Order).ToListAsync();
            var jobIds = tasks.SelectMany(x => new[] { x.JobId, x.ReviewJobId }).OfType<Guid>().Append(a.PlannerJobId ?? Guid.Empty).ToList();
            var jobs = await db.Jobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id);
            var models = (await db.Usage.AsNoTracking().Where(u => jobIds.Contains(u.JobId)).Select(u => new { u.JobId, u.Model }).ToListAsync())
                .GroupBy(u => u.JobId).ToDictionary(g => g.Key, g => g.Last().Model);
            object? JobState(Guid? jid) => jid is { } j && jobs.TryGetValue(j, out var job)
                ? new { id = job.Id, status = job.Status.ToString(), job.Attempts, job.LastError, model = models.GetValueOrDefault(job.Id) } : null;
            return Results.Ok(new
            {
                agenda = AgendaView(a, tasks),
                planner = JobState(a.PlannerJobId),
                tasks = tasks.Select(x => new
                {
                    x.Id, x.Key, x.Title, x.Role, x.Phase, x.Order, dependsOn = x.Dependencies, status = x.Status.ToString(),
                    x.Revisions, x.QualityScore, x.QualityNotes, x.Summary, x.Instructions, x.UpdatedAt, x.CompletedAt,
                    job = JobState(x.JobId), review = JobState(x.ReviewJobId),
                }),
            });
        });

        v1.MapPost("/agendas/{id:guid}/pause", (Guid id, HttpContext http, HarnessDbContext db) => SetStatus(id, http, db, AgendaStatus.Running, AgendaStatus.Paused));
        v1.MapPost("/agendas/{id:guid}/resume", (Guid id, HttpContext http, HarnessDbContext db) => SetStatus(id, http, db, AgendaStatus.Paused, AgendaStatus.Running));

        v1.MapPost("/agendas/{id:guid}/swarm", async (Guid id, SwarmSizeRequest req, HttpContext http, HarnessDbContext db, AgendaOptionsView opts) =>
        {
            var tid = http.Tenant().Id;
            var size = Math.Clamp(req.Size, 1, opts.MaxSwarmSize);
            var n = await db.Agendas.Where(a => a.Id == id && a.TenantId == tid).ExecuteUpdateAsync(s => s.SetProperty(a => a.SwarmSize, size));
            return n == 1 ? Results.Ok(new { swarmSize = size }) : Results.NotFound();
        });

        // Put a failed or blocked task (and everything it blocked) back in line.
        v1.MapPost("/agendas/{id:guid}/tasks/{taskId:guid}/retry", async (Guid id, Guid taskId, HttpContext http, HarnessDbContext db) =>
        {
            var tid = http.Tenant().Id;
            var agenda = await db.Agendas.FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tid);
            if (agenda is null) return Results.NotFound();
            var tasks = await db.AgendaTasks.Where(t => t.AgendaId == id).ToListAsync();
            var task = tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null || task.Status is not (AgendaTaskStatus.Failed or AgendaTaskStatus.Blocked))
                return Results.Problem("Only failed or blocked tasks can be retried.", statusCode: 409);
            task.Status = AgendaTaskStatus.Pending; task.Revisions = 0; task.JobId = null; task.ReviewJobId = null; task.QualityNotes = "Retried.";
            foreach (var b in tasks.Where(t => t.Status == AgendaTaskStatus.Blocked)) b.Status = AgendaTaskStatus.Pending;   // re-evaluated next tick
            if (agenda.Status is AgendaStatus.Done or AgendaStatus.Failed && tasks.Count > 0) { agenda.Status = AgendaStatus.Running; agenda.CompletedAt = null; }
            await db.SaveChangesAsync();
            return Results.Accepted($"/v1/agendas/{id}");
        });

        // ───── Skills ─────
        v1.MapGet("/skills", async (HttpContext http, HarnessDbContext db) =>
        {
            var tid = http.Tenant().Id;
            var skills = await db.Skills.AsNoTracking().Where(s => s.TenantId == null || s.TenantId == tid).OrderBy(s => s.TenantId == null).ThenBy(s => s.Title).ToListAsync();
            return Results.Ok(skills.Select(s => new
            {
                s.Id, s.Name, s.Title, s.Roles, s.Triggers, s.Always, s.Body, s.Version, s.Source, s.Active, s.Uses, s.Passes,
                editable = s.TenantId != null,
                passRate = s.Uses == 0 ? (double?)null : Math.Round((double)s.Passes / s.Uses, 2),
            }));
        });

        v1.MapPost("/skills", async (SkillRequest req, HttpContext http, HarnessDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Body) || req.Body.Length > 4000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["skill"] = ["Title and body are required (body up to 4000 characters)."] });
            var s = new Skill
            {
                TenantId = http.Tenant().Id, Name = Slug(req.Title), Title = req.Title.Trim(), Body = req.Body.Trim(),
                Roles = string.IsNullOrWhiteSpace(req.Roles) ? "*" : req.Roles.Trim(), Triggers = req.Triggers?.Trim() ?? "",
                Always = req.Always ?? string.IsNullOrWhiteSpace(req.Triggers), Source = "agency",
            };
            db.Skills.Add(s);
            await db.SaveChangesAsync();
            return Results.Created($"/v1/skills/{s.Id}", new { s.Id, s.Name, s.Title });
        });

        v1.MapPut("/skills/{id:guid}", async (Guid id, SkillRequest req, HttpContext http, HarnessDbContext db) =>
        {
            var tid = http.Tenant().Id;
            var s = await db.Skills.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid);
            if (s is null) return Results.NotFound();   // built-in skills are read-only; add an agency skill to override
            s.Title = req.Title?.Trim() is { Length: > 0 } t ? t : s.Title;
            s.Body = req.Body?.Trim() is { Length: > 0 } b ? b : s.Body;
            if (req.Roles is not null) s.Roles = req.Roles.Trim();
            if (req.Triggers is not null) s.Triggers = req.Triggers.Trim();
            if (req.Always is { } a) s.Always = a;
            if (req.Active is { } act) s.Active = act;
            s.Version++; s.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { s.Id, s.Version });
        });

        // ───── Learning ─────
        v1.MapGet("/lessons", async (HttpContext http, HarnessDbContext db, TimeProvider clock, bool? all) =>
        {
            var tid = http.Tenant().Id;
            var q = db.Lessons.AsNoTracking().Where(l => l.TenantId == tid);
            if (all != true) q = q.Where(l => l.Active);
            var lessons = await q.ToListAsync();
            var clients = await db.Clients.AsNoTracking().Where(c => c.TenantId == tid).ToDictionaryAsync(c => c.Id, c => c.Name);
            var now = clock.GetUtcNow();
            return Results.Ok(lessons.OrderByDescending(l => SkillsAndLessons.Strength(l, now)).Take(200).Select(l => new
            {
                l.Id, l.Text, l.Role, client = l.ClientId is { } c ? clients.GetValueOrDefault(c) : null, l.Source, l.Seen, l.Uses, l.Active,
                strength = Math.Round(SkillsAndLessons.Strength(l, now), 2), l.CreatedAt, l.LastSeenAt, l.EvidenceJobId,
            }));
        });

        v1.MapDelete("/lessons/{id:guid}", async (Guid id, HttpContext http, HarnessDbContext db) =>
        {
            var tid = http.Tenant().Id;
            var n = await db.Lessons.Where(l => l.Id == id && l.TenantId == tid).ExecuteUpdateAsync(s => s.SetProperty(l => l.Active, false));
            return n == 1 ? Results.NoContent() : Results.NotFound();
        });

        // A lesson that keeps proving useful becomes a skill the agency maintains.
        v1.MapPost("/lessons/{id:guid}/promote", async (Guid id, HttpContext http, HarnessDbContext db) =>
        {
            var tid = http.Tenant().Id;
            var l = await db.Lessons.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid);
            if (l is null) return Results.NotFound();
            var title = l.Text.Length > 60 ? l.Text[..60].TrimEnd() + "…" : l.Text;
            var skill = new Skill { TenantId = tid, Name = Slug(title), Title = title, Body = "- " + l.Text, Roles = l.Role, Always = true, Source = "learned" };
            db.Skills.Add(skill);
            l.Active = false;
            await db.SaveChangesAsync();
            return Results.Created($"/v1/skills/{skill.Id}", new { skill.Id, skill.Title });
        });

        // 👍 / 👎 on a deliverable becomes a lesson for that client and role.
        v1.MapPost("/deliverables/{id:guid}/feedback", async (Guid id, FeedbackRequest req, HttpContext http, HarnessDbContext db, Outbox outbox) =>
        {
            var tid = http.Tenant().Id;
            var d = await db.Deliverables.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tid);
            if (d is null) return Results.NotFound();
            var rating = req.Rating >= 0 ? 1 : -1;
            var note = req.Note?.Trim();
            db.Feedback.Add(new Feedback { TenantId = tid, DeliverableId = id, Rating = rating, Note = note });
            await db.SaveChangesAsync();
            var role = await db.Jobs.AsNoTracking().Where(j => j.Id == d.JobId).Select(j => j.AgentRole).FirstOrDefaultAsync() ?? "*";
            var clientId = await db.Projects.AsNoTracking().Where(p => p.Id == d.ProjectId).Select(p => (Guid?)p.ClientId).FirstOrDefaultAsync();
            if (!string.IsNullOrEmpty(note))
                await SkillsAndLessons.LearnAsync(db, outbox, tid, clientId, role, rating > 0 ? $"Keep doing: {note}" : note, "feedback", d.JobId, http.RequestAborted);
            return Results.Ok(new { recorded = true, learned = !string.IsNullOrEmpty(note) });
        });
    }

    private static async Task<IResult> SetStatus(Guid id, HttpContext http, HarnessDbContext db, AgendaStatus from, AgendaStatus to)
    {
        var tid = http.Tenant().Id;
        var n = await db.Agendas.Where(a => a.Id == id && a.TenantId == tid && a.Status == from)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, to).SetProperty(a => a.UpdatedAt, DateTimeOffset.UtcNow));
        return n == 1 ? Results.Ok(new { status = to.ToString() }) : Results.Problem($"Only {from.ToString().ToLowerInvariant()} agendas can do that.", statusCode: 409);
    }

    internal static object AgendaView(Agenda a, List<AgendaTask> tasks) => new
    {
        a.Id, a.ProjectId, a.Title, a.Plan, status = a.Status.ToString(), a.SwarmSize, a.ReviewGate, a.LastError, a.CreatedAt, a.CompletedAt,
        counts = new
        {
            total = tasks.Count,
            done = tasks.Count(t => t.Status == AgendaTaskStatus.Done),
            active = tasks.Count(t => t.Status is AgendaTaskStatus.Running or AgendaTaskStatus.Reviewing),
            waiting = tasks.Count(t => t.Status == AgendaTaskStatus.Pending),
            failed = tasks.Count(t => t.Status is AgendaTaskStatus.Failed or AgendaTaskStatus.Blocked),
            revisions = tasks.Sum(t => t.Revisions),
        },
    };

    private static string Slug(string s)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 60 ? slug[..60] : slug.Length == 0 ? "skill" : slug;
    }
}

/// <summary>The bits of agenda options the API needs (kept separate so the API doesn't depend on the worker).</summary>
public record AgendaOptionsView(int MaxSwarmSize);
