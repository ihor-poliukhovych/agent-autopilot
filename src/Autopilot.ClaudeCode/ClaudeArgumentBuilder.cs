using Autopilot.Core;

namespace Autopilot.ClaudeCode;

/// <summary>
/// Builds the argument list for one headless Claude Code run. Each item is one argv entry;
/// nothing is joined into a command string. The prompt is not here: it goes through stdin.
/// </summary>
public static class ClaudeArgumentBuilder
{
    /// <summary>Built-in tools that reach outside the worktree or start other agents.</summary>
    public static IReadOnlyList<string> RiskyTools { get; } =
        ["WebFetch", "WebSearch", "Task", "Agent", "NotebookEdit", "PowerShell"];

    /// <summary>
    /// Under dontAsk, tools listed in --allowedTools run without a prompt and everything that
    /// would prompt is denied, so a headless run never waits for a person.
    /// </summary>
    public const string PermissionMode = "dontAsk";

    public static IReadOnlyList<string> Build(
        AgentRunRequest request,
        ClaudeRunFiles files,
        ClaudeCodeRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);

        IReadOnlyList<string> allowed = request.RoleConfig.AllowedTools;
        bool hasMcp = options.McpConfigFiles.Count > 0;

        var args = new List<string> { "-p", "--output-format", "json" };

        if (!string.IsNullOrWhiteSpace(request.RoleConfig.Model))
        {
            args.Add("--model");
            args.Add(request.RoleConfig.Model);
        }

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            // The file form keeps long or special-character text off the command line.
            args.Add("--append-system-prompt-file");
            args.Add(files.SystemPromptFile);
        }

        // Only these built-in tools exist in the session; MCP tools are not built-ins.
        args.Add("--tools");
        args.Add(string.Join(',', allowed.Where(t => !IsMcpTool(t))));

        if (allowed.Count > 0)
        {
            args.Add("--allowedTools");
            args.Add(string.Join(',', allowed));
        }

        var disallowed = RiskyTools
            .Where(t => !allowed.Contains(t, StringComparer.Ordinal))
            .ToList();
        if (!hasMcp)
        {
            disallowed.Add("mcp__*");
        }

        args.Add("--disallowedTools");
        args.Add(string.Join(',', disallowed));

        args.Add("--permission-mode");
        args.Add(PermissionMode);

        args.Add("--settings");
        args.Add(files.SettingsFile);

        // Empty list: no user, project or local settings (and none of their hooks); --settings still applies.
        // The CLI reference (https://code.claude.com/docs/en/cli-reference) lists the values user, project
        // and local and does not say what an empty list does, so it was checked on Claude Code 2.1.332
        // (2026-11-25): a folder with .claude/settings.json and .claude/settings.local.json, each with a
        // SessionStart and a UserPromptSubmit hook that writes a marker file, run with
        // `claude -p --setting-sources "" --debug-file ...`. No marker file was written, so the empty value
        // is not read as "not given" (the default loads all sources and would have run those hooks).
        // The user source could not be tested without changing the user's own settings; its debug lines
        // were the same as with `--setting-sources local`. There is no documented value that names
        // "none", so the empty value stays.
        args.Add("--setting-sources");
        args.Add(string.Empty);

        args.Add("--strict-mcp-config");
        args.Add("--mcp-config");
        args.Add(files.McpConfigFile);
        args.AddRange(options.McpConfigFiles);

        args.Add("--no-session-persistence");

        if (options.MaxTurns is int maxTurns)
        {
            args.Add("--max-turns");
            args.Add(maxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(request.OutputJsonSchema))
        {
            args.Add("--json-schema");
            args.Add(request.OutputJsonSchema);
        }

        return args;
    }

    private static bool IsMcpTool(string tool) => tool.StartsWith("mcp__", StringComparison.Ordinal);
}
