using System.Text.Json;
using Autopilot.Core.Config;

namespace Autopilot.Core.Policy;

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
    string CounterFile);

/// <summary>A tool call as Claude Code sends it to a PreToolUse hook.</summary>
public sealed record ToolCall(string ToolName, JsonElement ToolInput);

public sealed record PolicyDecision(bool Allowed, string Reason)
{
    public static PolicyDecision Allow(string reason = "allowed") => new(true, reason);
    public static PolicyDecision Deny(string reason) => new(false, reason);
}
