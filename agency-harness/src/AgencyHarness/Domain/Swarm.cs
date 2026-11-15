namespace AgencyHarness.Domain;

public enum AgendaStatus { Planning, Running, Paused, Done, Failed }

/// <summary>A goal-sized piece of work (e.g. "ZUNVI 30-day growth sprint") that a swarm of agents executes as a task graph.</summary>
public class Agenda
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ProjectId { get; set; }
    public required string Title { get; set; }
    /// <summary>The plan in your own words; the planner agent turns it into tasks.</summary>
    public required string Plan { get; set; }
    public AgendaStatus Status { get; set; } = AgendaStatus.Planning;
    /// <summary>Most tasks running at once (bulkhead: one agenda can't starve the others or the model quota).</summary>
    public int SwarmSize { get; set; } = 3;
    /// <summary>Send each finished task through the reviewer agent before it counts as done.</summary>
    public bool ReviewGate { get; set; } = true;
    public Guid? PlannerJobId { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public enum AgendaTaskStatus { Pending, Running, Reviewing, Done, Failed, Blocked }

/// <summary>One node of an agenda's task graph.</summary>
public class AgendaTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AgendaId { get; set; }
    /// <summary>Short id the planner uses for dependencies, e.g. "sitemap-audit".</summary>
    public required string Key { get; set; }
    public required string Title { get; set; }
    public required string Role { get; set; }
    public required string Instructions { get; set; }
    /// <summary>Keys of tasks that must be done first, comma-separated.</summary>
    public string DependsOn { get; set; } = "";
    /// <summary>Grouping for the board, e.g. "Days 1–7: Technical & indexing".</summary>
    public string Phase { get; set; } = "Tasks";
    public int Order { get; set; }
    public AgendaTaskStatus Status { get; set; } = AgendaTaskStatus.Pending;
    public Guid? JobId { get; set; }
    public Guid? ReviewJobId { get; set; }
    /// <summary>How many times the work has been (re)done after failing a quality check.</summary>
    public int Revisions { get; set; }
    public int? QualityScore { get; set; }
    /// <summary>Latest quality-gate and review findings shown on the board.</summary>
    public string? QualityNotes { get; set; }
    public string? Summary { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public IEnumerable<string> Dependencies => DependsOn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// A playbook an agent follows: checklists and rules that prevent known mistakes.
/// Built-in skills ship with the harness (TenantId null); agencies add their own or promote lessons into skills.
/// </summary>
public class Skill
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    /// <summary>Stable slug, e.g. "casl-outreach".</summary>
    public required string Name { get; set; }
    public required string Title { get; set; }
    /// <summary>Roles it applies to, comma-separated; "*" for all.</summary>
    public string Roles { get; set; } = "*";
    /// <summary>Words in a task that make this skill relevant, comma-separated. Empty + Always = always used.</summary>
    public string Triggers { get; set; } = "";
    public bool Always { get; set; }
    public required string Body { get; set; }
    public int Version { get; set; } = 1;
    public string Source { get; set; } = "builtin";   // builtin | agency | learned
    public bool Active { get; set; } = true;
    public int Uses { get; set; }
    /// <summary>Uses whose work then passed the quality gate.</summary>
    public int Passes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Something learned while working: from a failed check, a review, or your feedback. Fed into future work.</summary>
public class Lesson
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ClientId { get; set; }
    /// <summary>Role it applies to, or "*".</summary>
    public string Role { get; set; } = "*";
    public required string Text { get; set; }
    /// <summary>Normalised text, so the same lesson is reinforced rather than duplicated.</summary>
    public required string Fingerprint { get; set; }
    public string Source { get; set; } = "gate";   // gate | review | feedback | manual
    public Guid? EvidenceJobId { get; set; }
    /// <summary>0–1. Grows each time the lesson is seen again, decays when unused.</summary>
    public double Confidence { get; set; } = 0.5;
    public int Seen { get; set; } = 1;
    public int Uses { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Your rating of a deliverable. Becomes a lesson for the client and role.</summary>
public class Feedback
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid DeliverableId { get; set; }
    public int Rating { get; set; }   // +1 or -1
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
