using DevAutopilot.Core.Process;

namespace DevAutopilot.ClaudeCode;

/// <summary>Prepares the child process environment so the run uses the subscription login and sees no secrets.</summary>
public static class ClaudeEnvironment
{
    /// <summary>Variables that would switch Claude Code from the subscription login to API billing.</summary>
    public static IReadOnlyList<string> RemovedVariables { get; } = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"];

    /// <summary>
    /// Keeps only the <see cref="ChildEnvironment"/> allow-list plus <paramref name="passEnvironment"/>,
    /// in place. ANTHROPIC_* and AUTOPILOT_* are always removed. The hook and every shell command the
    /// agent starts inherit this environment from Claude Code.
    /// </summary>
    public static void Apply(IDictionary<string, string?> environment, IEnumerable<string>? passEnvironment = null) =>
        ChildEnvironment.Apply(environment, passEnvironment);
}
