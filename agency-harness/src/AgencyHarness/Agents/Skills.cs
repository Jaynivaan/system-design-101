using System.Text;
using System.Text.RegularExpressions;
using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgencyHarness.Agents;

/// <summary>The playbooks that ship with the harness. Seeded on startup; a newer Version replaces the stored one.</summary>
public static class BuiltinSkills
{
    public static readonly IReadOnlyList<Skill> All =
    [
        new()
        {
            Name = "deliverable-format", Title = "Deliverable format", Roles = "*", Always = true, Version = 1,
            Body = """
                - Start with a 2–3 sentence summary of what you produced and why.
                - Then the deliverable itself, ready to use as-is (no placeholders like [Business Name] unless the task asks for a template).
                - End with "Assumptions" (anything you had to guess) and "Next steps" (max 3 bullets).
                - Use Markdown headings and tables where they help; keep it scannable.
                """,
        },
        new()
        {
            Name = "brand-voice-guard", Title = "Follow the client brief exactly", Roles = "copywriter,content,outreach,seo,geo,strategist", Always = true, Version = 1,
            Body = """
                - Call get_client_brief first and follow the voice, audience and goals it gives.
                - Never use anything listed under "Avoid" in the brief — not even in headings or hashtags.
                - Prefer specifics (places, products, numbers from the brief) over generic marketing claims.
                - Don't invent facts about the client (awards, prices, reviews, partnerships). Mark anything uncertain as an assumption.
                """,
        },
        new()
        {
            Name = "canada-local-seo", Title = "Canadian local SEO", Roles = "seo,geo,techseo,content,strategist,planner",
            Triggers = "seo,local,directory,listing,city,province,canada,canadian,ontario,quebec,toronto,vancouver,montreal,landing,search", Version = 1,
            Body = """
                - Keep NAP (name, address, phone) identical everywhere; use Canadian address format and postal codes (A1A 1A1).
                - Build city/province/category hubs with unique, useful content per page (avoid thin, near-duplicate pages).
                - Plan bilingual EN/FR where the market needs it (Quebec, Ottawa, New Brunswick): hreflang en-CA / fr-CA, real French copy, not machine-literal.
                - Use LocalBusiness (or a more specific subtype) JSON-LD with address, geo, openingHours, sameAs; validate it parses.
                - Canonical URLs, XML sitemaps per section, and fast server-rendered HTML for anything that must rank.
                """,
        },
        new()
        {
            Name = "geo-ai-citations", Title = "Get cited by AI search (GEO)", Roles = "geo,seo,content,strategist",
            Triggers = "geo,ai search,chatgpt,perplexity,claude,ai overview,citation,llm,generative", Version = 1,
            Body = """
                - Lead each page with a direct, quotable 1–2 sentence answer, then supporting detail.
                - Add concise FAQs written the way people ask (who/what/where/how much/is it open).
                - Include concrete, verifiable facts (hours, neighbourhood, distances, counts) and keep them fresh.
                - Use clear entity names and consistent structured data so AI systems can attribute facts to the right business.
                - Avoid keyword stuffing; write for a person deciding where to go.
                """,
        },
        new()
        {
            Name = "casl-outreach", Title = "CASL-compliant outreach (Canada anti-spam law)", Roles = "outreach,copywriter,content",
            Triggers = "email,outreach,cold,newsletter,message,campaign,dm,sms,whatsapp", Version = 1,
            Body = """
                - Commercial electronic messages to Canadians need consent (express, or implied e.g. a publicly listed business address relevant to the message).
                - Every message must identify the sender (business name + mailing address + a contact method).
                - Every message must include a working unsubscribe/opt-out that's honoured within 10 business days.
                - No misleading subject lines or sender names. Keep a record of how consent was obtained.
                - Write drafts for a human to approve and send; don't claim a message was sent.
                """,
        },
        new()
        {
            Name = "structured-data-valid", Title = "Valid structured data", Roles = "techseo,seo,geo",
            Triggers = "json-ld,schema,structured data,markup,sitemap,canonical", Version = 1,
            Body = """
                - Put JSON-LD in a ```json code block and make sure it is valid JSON (no comments, no trailing commas).
                - Use schema.org types and properties that exist; include @context and @type.
                - Don't mark up content that isn't visible on the page.
                """,
        },
        new()
        {
            Name = "planner-dag", Title = "Turning a plan into a task graph", Roles = "planner", Always = true, Version = 1,
            Body = """
                - Create 6–15 tasks; each is one agent's job finishable in a single sitting with a concrete deliverable.
                - Give each task a short key (kebab-case), a role from: strategist, copywriter, seo, geo, techseo, content, outreach, analyst.
                - Use depends_on only for real data dependencies; let independent work run in parallel.
                - Group tasks into the plan's phases (e.g. "Days 1–7: Technical & indexing").
                - Instructions must be self-contained: say what to produce, for which pages/audience, and the format.
                - Don't create tasks that need systems the agents can't access (analytics, databases, sending email); instead create a task that produces the spec, script or draft a human will run.
                """,
        },
        new()
        {
            Name = "reviewer-rubric", Title = "Reviewing work", Roles = "reviewer", Always = true, Version = 1,
            Body = """
                - Check the work against the task instructions, the client brief (voice, Avoid list), and the skills listed.
                - Score 1–10. Pass at 7+. Be specific: quote the problem and say how to fix it.
                - Record 0–3 short, reusable lessons that would prevent the same mistake next time (not task-specific trivia).
                """,
        },
    ];

    public static async Task SeedAsync(HarnessDbContext db, CancellationToken ct = default)
    {
        foreach (var s in All)
        {
            var existing = await db.Skills.FirstOrDefaultAsync(x => x.TenantId == null && x.Name == s.Name, ct);
            if (existing is null)
                db.Skills.Add(new Skill { Name = s.Name, Title = s.Title, Roles = s.Roles, Triggers = s.Triggers, Always = s.Always, Body = s.Body, Version = s.Version, Source = "builtin" });
            else if (existing.Version < s.Version)
            {
                existing.Title = s.Title; existing.Roles = s.Roles; existing.Triggers = s.Triggers; existing.Always = s.Always;
                existing.Body = s.Body; existing.Version = s.Version; existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Gives each job the skills and lessons that apply to it. Skills are chosen by role and by keywords in the
/// task; lessons are this agency's (and this client's) learned rules, strongest and most recent first.
/// </summary>
public class SkillsAndLessons(TimeProvider clock)
{
    public const int MaxSkills = 4, MaxLessons = 6;

    public record Context(string Prompt, List<Guid> SkillIds, List<Guid> LessonIds);

    public async Task<Context> BuildAsync(HarnessDbContext db, AgentJob job, string role, CancellationToken ct)
    {
        var clientId = await db.Projects.AsNoTracking().Where(p => p.Id == job.ProjectId).Select(p => (Guid?)p.ClientId).FirstOrDefaultAsync(ct);
        var text = job.Instructions.ToLowerInvariant();

        var skills = (await db.Skills.AsNoTracking()
                .Where(s => s.Active && (s.TenantId == null || s.TenantId == job.TenantId)).ToListAsync(ct))
            .Where(s => AppliesTo(s.Roles, role))
            .Select(s => (Skill: s, Score: (s.Always ? 100 : 0) + s.Triggers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Count(t => text.Contains(t.ToLowerInvariant()))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Skill.TenantId != null)   // agency skills win ties
            .Take(MaxSkills).Select(x => x.Skill).ToList();

        var lessons = (await db.Lessons.AsNoTracking()
                .Where(l => l.TenantId == job.TenantId && l.Active && (l.ClientId == null || l.ClientId == clientId) && (l.Role == "*" || l.Role == role))
                .ToListAsync(ct))
            .OrderByDescending(l => Strength(l, clock.GetUtcNow()))
            .Take(MaxLessons).ToList();

        var sb = new StringBuilder();
        if (skills.Count > 0)
        {
            sb.Append("\n\n## Skills to apply\n");
            foreach (var s in skills) sb.Append($"\n### {s.Title}\n{s.Body.Trim()}\n");
        }
        if (lessons.Count > 0)
        {
            sb.Append("\n\n## Lessons from earlier work (don't repeat these mistakes)\n");
            foreach (var l in lessons) sb.Append($"- {l.Text}\n");
        }

        if (skills.Count > 0)
        {
            var ids = skills.Select(s => s.Id).ToList();
            await db.Skills.Where(s => ids.Contains(s.Id)).ExecuteUpdateAsync(u => u.SetProperty(s => s.Uses, s => s.Uses + 1), ct);
        }
        if (lessons.Count > 0)
        {
            var ids = lessons.Select(l => l.Id).ToList();
            await db.Lessons.Where(l => ids.Contains(l.Id)).ExecuteUpdateAsync(u => u.SetProperty(l => l.Uses, l => l.Uses + 1), ct);
        }
        return new Context(sb.ToString(), skills.Select(s => s.Id).ToList(), lessons.Select(l => l.Id).ToList());
    }

    /// <summary>Confidence, decayed by how long since the lesson was last seen (half-life ~60 days).</summary>
    public static double Strength(Lesson l, DateTimeOffset now) =>
        l.Confidence * Math.Pow(0.5, Math.Max(0, (now - l.LastSeenAt).TotalDays) / 60.0);

    private static bool AppliesTo(string roles, string role) =>
        roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(r => r == "*" || r == role);

    /// <summary>Record a lesson; if the same lesson already exists, reinforce it instead of duplicating.</summary>
    public static async Task<Lesson> LearnAsync(HarnessDbContext db, Outbox outbox, Guid tenantId, Guid? clientId, string role,
        string text, string source, Guid? jobId, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length > 400) text = text[..400];
        var fp = Fingerprint(text);
        var existing = await db.Lessons.FirstOrDefaultAsync(l => l.TenantId == tenantId && l.Fingerprint == fp && l.ClientId == clientId && l.Role == role, ct);
        if (existing is not null)
        {
            existing.Seen++;
            existing.Confidence = Math.Min(1.0, existing.Confidence + (1 - existing.Confidence) * 0.3);
            existing.LastSeenAt = DateTimeOffset.UtcNow;
            existing.Active = true;
            await db.SaveChangesAsync(ct);
            return existing;
        }
        var lesson = new Lesson { TenantId = tenantId, ClientId = clientId, Role = role, Text = text, Fingerprint = fp, Source = source, EvidenceJobId = jobId,
            Confidence = source == "feedback" ? 0.8 : 0.5 };
        db.Lessons.Add(lesson);
        outbox.Add(db, tenantId, "lesson.learned", new { lessonId = lesson.Id, role, source, text });
        await db.SaveChangesAsync(ct);
        return lesson;
    }

    public static string Fingerprint(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
}
