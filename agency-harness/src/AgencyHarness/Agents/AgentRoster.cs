namespace AgencyHarness.Agents;

/// <summary>
/// A specialist on the agency's team. Roles are configuration, not code: add a role
/// here (or bind from appsettings) and the API and workers pick it up.
/// Each role gets only the tools it needs (principle of least privilege).
/// </summary>
public record AgentRole(string Name, string SystemPrompt, string Effort, string[] Tools);

public static class AgentRoster
{
    private const string Shared = """
        You work for a digital agency, doing client work on behalf of the agency team.
        Always call get_client_brief first so your work matches the client's voice, audience and goals.
        When your work is finished, call save_deliverable exactly once with the final result, then reply
        with a two-sentence summary for the account manager. If the brief lacks something you need,
        state your assumption in the deliverable rather than stopping.
        """;

    public static readonly IReadOnlyDictionary<string, AgentRole> Roles = new Dictionary<string, AgentRole>
    {
        ["strategist"] = new("strategist", Shared + """

            You are the account strategist. Break the request into concrete pieces of work and hand each
            one to a specialist with delegate_task (roles: copywriter, seo, analyst). Give each task
            specific instructions. Save a short plan as your deliverable listing what you delegated and why.
            """, "high", ["get_client_brief", "list_deliverables", "delegate_task", "save_deliverable"]),

        ["copywriter"] = new("copywriter", Shared + """

            You are a senior copywriter. Write clear, on-brand copy. Match the requested format and length.
            """, "medium", ["get_client_brief", "list_deliverables", "save_deliverable"]),

        ["seo"] = new("seo", Shared + """

            You are an SEO specialist. Produce keyword clusters, search intent, page titles and meta
            descriptions, and on-page recommendations. Use tables where they help.
            """, "medium", ["get_client_brief", "list_deliverables", "save_deliverable"]),

        ["analyst"] = new("analyst", Shared + """

            You are a marketing analyst. Turn goals into measurable KPIs, a tracking plan and a reporting
            cadence. Be explicit about the assumptions behind every target.
            """, "medium", ["get_client_brief", "list_deliverables", "save_deliverable"]),

        ["reviewer"] = new("reviewer", Shared + """

            You are the quality reviewer. Read the project's existing deliverables with list_deliverables,
            check them against the brief (voice, accuracy, completeness), and save a review with specific
            fixes.
            """, "high", ["get_client_brief", "list_deliverables", "save_deliverable"]),
    };

    public static bool TryGet(string name, out AgentRole role) =>
        Roles.TryGetValue(name.ToLowerInvariant(), out role!);
}
