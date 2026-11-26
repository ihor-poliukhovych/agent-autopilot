using System.Text.Json;
using DevAutopilot.Core.Config;

namespace DevAutopilot.Core.Policy;

/// <summary>
/// Everything the PreToolUse hook needs to decide on one tool call. Written to a JSON file
/// in the run directory before the agent starts; the hook reads it on every call.
/// </summary>
public sealed record ToolPolicy(
    AgentRole Role,
    /// <summary>Absolute path of the worktree; no file access outside it.</summary>
    string WorktreeRoot,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string> WritablePaths,
    /// <summary>Always denied for writing, even when inside WritablePaths (for example ".autopilot/**").</summary>
    IReadOnlyList<string> ProtectedPaths,
    IReadOnlyList<string> AllowedCommands,
    int MaxToolCalls,
    /// <summary>File where the hook keeps its call counter.</summary>
    string CounterFile,
    /// <summary>
    /// The project's configured build and test command lines. Each is allowed exactly as written (after
    /// trimming), even with quotes or options such as <c>-p:Name=Value</c> that the normal argument rules
    /// deny, and with extra trailing arguments that pass the normal rules. Null or empty: none.
    /// </summary>
    IReadOnlyList<string>? ConfiguredCommands = null);

/// <summary>A tool call as Claude Code sends it to a PreToolUse hook.</summary>
public sealed record ToolCall(string ToolName, JsonElement ToolInput);

public sealed record PolicyDecision(bool Allowed, string Reason)
{
    public static PolicyDecision Allow(string reason = "allowed") => new(true, reason);
    public static PolicyDecision Deny(string reason) => new(false, reason);
}
