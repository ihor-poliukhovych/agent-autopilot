namespace DevAutopilot.ClaudeCode;

public sealed record ClaudeCodeRunnerOptions
{
    /// <summary>
    /// Claude Code executable: an absolute path, or a name looked up on PATH. On Windows an npm
    /// shim (<c>claude.cmd</c>, <c>claude.ps1</c>) is replaced by the <c>claude.exe</c> it starts,
    /// because arguments passed through <c>cmd.exe</c> cannot be quoted safely.
    /// </summary>
    public string ClaudeExecutable { get; init; } = "claude";

    /// <summary>
    /// Command line that starts the PreToolUse hook, without the policy argument. The runner
    /// appends <c>--policy "&lt;file&gt;"</c>. Example: <c>"C:\tools\autopilot.exe" hook pre-tool-use</c>.
    /// </summary>
    public required string HookCommand { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Seconds Claude Code waits for one hook call. Claude Code lets a call go ahead when its hook times
    /// out, so this is generous: a cold start of the hook must never run into it.
    /// </summary>
    public int HookTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Extra environment variable names passed to Claude Code (from the config's <c>passEnvironment</c>).
    /// ANTHROPIC_* and AUTOPILOT_* are never passed.
    /// </summary>
    public IReadOnlyList<string> PassEnvironment { get; init; } = [];

    /// <summary>
    /// Extra MCP config files passed to <c>--mcp-config</c> next to the run's own empty config.
    /// When empty and the request names no MCP servers, every MCP tool is also denied with
    /// <c>--disallowedTools "mcp__*"</c>.
    /// </summary>
    public IReadOnlyList<string> McpConfigFiles { get; init; } = [];

    /// <summary>Optional <c>--max-turns</c> limit. Null means no limit beyond the hook's tool call limit.</summary>
    public int? MaxTurns { get; init; }
}
