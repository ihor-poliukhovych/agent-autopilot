// TEMPORARY: signatures agreed between parallel work streams. The owner moves each type
// into its own file, implements it, and deletes it from here.
using Autopilot.Core;

namespace Autopilot.ClaudeCode
{
    public sealed record ClaudeCodeRunnerOptions
    {
        /// <summary>Claude Code executable; resolved from PATH when not absolute.</summary>
        public string ClaudeExecutable { get; init; } = "claude";

        /// <summary>
        /// Command line that starts the PreToolUse hook, without the policy argument. The runner
        /// appends <c>--policy "&lt;file&gt;"</c>. Example: <c>"C:\tools\autopilot.exe" hook pre-tool-use</c>.
        /// </summary>
        public required string HookCommand { get; init; }

        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    }

    public sealed class ClaudeCodeRunner(ClaudeCodeRunnerOptions options) : IAgentRunner
    {
        public ClaudeCodeRunnerOptions Options { get; } = options;

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct) => throw new NotImplementedException();
    }
}

namespace Autopilot.ClaudeCode.Hooks
{
    public static class PreToolUseHook
    {
        /// <summary>
        /// Entry point of <c>autopilot hook pre-tool-use --policy &lt;file&gt;</c>. Reads Claude Code's hook
        /// JSON from stdin, writes the decision, returns the process exit code. Fails closed.
        /// </summary>
        public static Task<int> RunAsync(string policyFile, TextReader stdin, TextWriter stdout, TextWriter stderr) => throw new NotImplementedException();
    }
}
