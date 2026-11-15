using System.Text.Json;
using System.Text.RegularExpressions;
using AgencyHarness.Domain;
using AgencyHarness.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AgencyHarness.Workers;

/// <summary>An issue the gate found, plus the general lesson that would have prevented it.</summary>
public record GateIssue(string Problem, string Lesson);

public record GateResult(bool Pass, int Score, IReadOnlyList<GateIssue> Issues, bool Placeholder = false)
{
    public string Notes => Issues.Count == 0 ? (Placeholder ? "Placeholder output (no model connected)." : "Passed automatic checks.")
        : string.Join(" • ", Issues.Select(i => i.Problem));
}

/// <summary>
/// Cheap, deterministic checks that run on every finished job before it counts as done
/// (the reviewer agent adds judgement on top for agenda tasks). Each failure names the problem
/// and a reusable lesson, so the same mistake is less likely next time.
/// </summary>
public partial class QualityGate
{
    public const int MinLength = 200;

    [GeneratedRegex(@"\[(?:business|client|company|name|insert|your|tbd|city|address|phone|link|url)[^\]\n]{0,40}\]", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"```json\s*(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex JsonBlock();

    public async Task<GateResult> CheckAsync(HarnessDbContext db, AgentJob job, CancellationToken ct)
    {
        var deliverable = await db.Deliverables.AsNoTracking().FirstOrDefaultAsync(d => d.JobId == job.Id, ct);
        if (deliverable is null)
            return Fail([new("No deliverable was saved.", "Always call save_deliverable with the finished work before replying.")]);
        var content = deliverable.Content;
        if (content.Contains("placeholder from the scripted runner", StringComparison.OrdinalIgnoreCase))
            return new GateResult(true, 0, [], Placeholder: true);

        var brief = await (from p in db.Projects.AsNoTracking()
                           join c in db.Clients.AsNoTracking() on p.ClientId equals c.Id
                           where p.Id == job.ProjectId
                           select c.Brief).FirstOrDefaultAsync(ct) ?? "";
        var issues = new List<GateIssue>();
        var role = job.AgentRole;
        var instructions = job.Instructions.ToLowerInvariant();

        if (content.Trim().Length < MinLength)
            issues.Add(new($"The deliverable is very short ({content.Trim().Length} characters).", "Deliver complete, usable work, not an outline or a one-liner."));

        foreach (var term in AvoidTerms(brief))
            if (Regex.IsMatch(content, $@"(?<![\p{{L}}]){Regex.Escape(term)}(?![\p{{L}}])", RegexOptions.IgnoreCase))
                issues.Add(new($"Used \"{term}\", which the brief says to avoid.", $"Never use \"{term}\" for this client; the brief lists it under Avoid."));

        if (!instructions.Contains("template") && PlaceholderPattern().Match(content) is { Success: true } ph)
            issues.Add(new($"Left a placeholder: {ph.Value}.", "Fill in real details from the brief instead of leaving [placeholders]; state assumptions if unsure."));

        bool isOutreach = role == "outreach" || Regex.IsMatch(instructions, @"\b(cold email|outreach email|email to|newsletter|email campaign)\b");
        if (isOutreach && !Regex.IsMatch(content, @"unsubscribe|opt[- ]out|se désabonner|désabonnement", RegexOptions.IgnoreCase))
            issues.Add(new("The outreach message has no unsubscribe/opt-out line (required by CASL).",
                "Every commercial message to Canadians needs sender identification and a working unsubscribe (CASL)."));

        foreach (Match m in JsonBlock().Matches(content))
        {
            try { using var _ = JsonDocument.Parse(m.Groups[1].Value); }
            catch (JsonException e)
            {
                issues.Add(new($"A JSON block isn't valid JSON ({e.Message.Split('.')[0]}).", "Structured data (JSON-LD) must be strictly valid JSON: no comments or trailing commas."));
                break;
            }
        }

        return issues.Count == 0 ? new GateResult(true, 10, []) : Fail(issues);
    }

    private static GateResult Fail(List<GateIssue> issues) => new(false, Math.Max(1, 10 - 3 * issues.Count), issues);

    /// <summary>Terms listed after "Avoid:" in the client brief (comma/semicolon separated; quotes removed).</summary>
    public static IEnumerable<string> AvoidTerms(string brief)
    {
        foreach (var line in brief.Split('\n'))
        {
            var idx = line.IndexOf("avoid", StringComparison.OrdinalIgnoreCase);
            if (idx < 0 || line.IndexOf(':', idx) is var colon && colon < 0) continue;
            foreach (var raw in line[(colon + 1)..].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = raw.Trim().Trim('.', ' ');
                var quoted = Regex.Match(t, "[\"“'‘]([^\"”'’]+)[\"”'’]");
                if (quoted.Success) t = quoted.Groups[1].Value.Trim();
                else if (t.Split(' ').Length > 3) continue;   // a description ("spicy-food clichés"), not a term to search for
                t = Regex.Replace(t, @"^(the word|the phrase|words like)\s+", "", RegexOptions.IgnoreCase).Trim();
                if (t.Length >= 3) yield return t;
            }
        }
    }
}
