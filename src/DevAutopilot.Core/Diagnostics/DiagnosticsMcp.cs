using DevAutopilot.Core.Config;

namespace DevAutopilot.Core.Diagnostics;

/// <summary>
/// Names and rules shared by the diagnostics MCP server and the pipeline that wires it into agent runs.
/// The server is read-only; only the triage and implementer roles get its tools.
/// </summary>
public static class DiagnosticsMcp
{
    /// <summary>Server name in the run's mcp.json; Claude Code names its tools <c>mcp__diagnostics__*</c>.</summary>
    public const string ServerName = "diagnostics";

    /// <summary>Module type that reads a JSON Lines log file.</summary>
    public const string JsonlType = "jsonl";

    /// <summary>Module type that turns diagnostics off (the default).</summary>
    public const string NoneType = "none";

    /// <summary>Module setting with the log file path, relative to the project root.</summary>
    public const string PathSetting = "path";

    public const string ListRecentErrors = "list_recent_errors";
    public const string GetErrorDetails = "get_error_details";
    public const string SearchLogs = "search_logs";
    public const string GetEndpointStats = "get_endpoint_stats";

    /// <summary>The server's tool names, without the Claude Code prefix.</summary>
    public static IReadOnlyList<string> ToolNames { get; } = [ListRecentErrors, GetErrorDetails, SearchLogs, GetEndpointStats];

    /// <summary>The tool names as Claude Code sees them: <c>mcp__diagnostics__&lt;tool&gt;</c>.</summary>
    public static IReadOnlyList<string> ClaudeToolNames { get; } =
        ToolNames.Select(t => $"mcp__{ServerName}__{t}").ToList();

    /// <summary>True when the module config turns the diagnostics server on.</summary>
    public static bool IsEnabled(ModuleConfig? module) =>
        module is not null && string.Equals(module.Type, JsonlType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Triage and implementer read diagnostics; the reviewer sees only the work item and the diff.</summary>
    public static bool RoleUsesDiagnostics(AgentRole role) => role is AgentRole.Triage or AgentRole.Implementer;

    /// <summary>The role config with the four diagnostics tools added to its allowed tools (no duplicates).</summary>
    public static RoleConfig WithDiagnosticsTools(RoleConfig role)
    {
        ArgumentNullException.ThrowIfNull(role);
        var tools = role.AllowedTools.Concat(ClaudeToolNames).Distinct(StringComparer.Ordinal).ToList();
        return role with { AllowedTools = tools };
    }
}
