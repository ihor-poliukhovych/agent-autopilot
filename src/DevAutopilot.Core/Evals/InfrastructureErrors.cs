namespace DevAutopilot.Core.Evals;

/// <summary>
/// Recognises errors that come from the machine rather than from the agent's work: Claude Code is not
/// signed in, the subscription's usage limit is reached, a program could not start, the policy hook did
/// not run. Every later run would fail the same way, so the eval stops instead of using up the runs.
/// </summary>
public static class InfrastructureErrors
{
    private static readonly (string Needle, string Reason)[] Patterns =
    [
        ("not logged in", "Claude Code is not signed in. Run 'claude' once and sign in, then try again."),
        ("/login", "Claude Code is not signed in. Run 'claude' once and sign in, then try again."),
        ("invalid api key", "Claude Code is not signed in. Run 'claude' once and sign in, then try again."),
        ("authentication_error", "Claude Code could not authenticate. Run 'claude' once and sign in, then try again."),
        ("oauth token", "Claude Code could not authenticate. Run 'claude' once and sign in, then try again."),
        ("usage limit", "The Claude subscription's usage limit is reached. Wait until it resets, then try again."),
        ("hit your limit", "The Claude subscription's usage limit is reached. Wait until it resets, then try again."),
        ("limit will reset", "The Claude subscription's usage limit is reached. Wait until it resets, then try again."),
        ("rate limit", "Claude Code was rate limited. Wait a while, then try again."),
        ("rate_limit", "Claude Code was rate limited. Wait a while, then try again."),
        ("overloaded", "The Claude service is overloaded. Try again later."),
        ("could not start", "A program could not be started (is Claude Code or git installed and on PATH?)."),
        ("the policy hook did not run", "The policy hook did not run, so agent runs cannot be trusted. Run 'autopilot doctor'."),
    ];

    /// <summary>A plain-English reason when <paramref name="message"/> is an infrastructure error, otherwise null.</summary>
    public static string? Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        foreach (var (needle, reason) in Patterns)
        {
            if (message.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return reason;
            }
        }

        return null;
    }
}
