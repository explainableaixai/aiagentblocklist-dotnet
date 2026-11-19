using System.Text.Json.Serialization;

namespace AiAgentBlocklist
{
    /// <summary>
    /// One row of the AI Agent Blocklist feed (one domain or one device identifier).
    /// The property names map to the snake_case columns of <c>aab_full.csv</c> and <c>aab_full.json</c>.
    /// </summary>
    public sealed record Entry
    {
        /// <summary>Name of the AI tool the row belongs to.</summary>
        [JsonPropertyName("tool_name")] public string ToolName { get; init; } = "";

        /// <summary>Main web domain of the tool.</summary>
        [JsonPropertyName("tool_domain")] public string ToolDomain { get; init; } = "";

        /// <summary>Category of the tool.</summary>
        [JsonPropertyName("category")] public string Category { get; init; } = "";

        /// <summary>Subcategory (may hold several values separated by "; ").</summary>
        [JsonPropertyName("subcategory")] public string Subcategory { get; init; } = "";

        /// <summary>Agent type, for example "AI browser", "Coding agent" or "MCP server".</summary>
        [JsonPropertyName("agent_type")] public string AgentType { get; init; } = "";

        /// <summary>Kind of identifier, for example "domain", "chrome_ext", "process_win" or "pypi".</summary>
        [JsonPropertyName("kind")] public string Kind { get; init; } = "";

        /// <summary>The identifier itself: a domain, extension ID, bundle ID, process or package name.</summary>
        [JsonPropertyName("ident")] public string Ident { get; init; } = "";

        /// <summary>Display name of the extension, app, process or package.</summary>
        [JsonPropertyName("name")] public string Name { get; init; } = "";

        /// <summary>Platform, for example "Network", "Chrome", "Windows" or "npm".</summary>
        [JsonPropertyName("platform")] public string Platform { get; init; } = "";

        /// <summary>Risk grade: Low, Moderate, Elevated, High, Severe or "Not graded".</summary>
        [JsonPropertyName("risk_grade")] public string RiskGrade { get; init; } = "";
    }
}
