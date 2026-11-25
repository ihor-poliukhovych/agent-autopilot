namespace Autopilot.ClaudeCode;

/// <summary>Prepares the child process environment so the run uses the subscription login.</summary>
public static class ClaudeEnvironment
{
    /// <summary>Variables that would switch Claude Code from the subscription login to API billing.</summary>
    public static IReadOnlyList<string> RemovedVariables { get; } = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN"];

    /// <summary>Removes <see cref="RemovedVariables"/> in place, ignoring case.</summary>
    public static void Scrub(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        string[] keys = environment.Keys
            .Where(k => RemovedVariables.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        foreach (string key in keys)
        {
            environment.Remove(key);
        }
    }
}
