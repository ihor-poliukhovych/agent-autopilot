using System.Text.Json;
using DevAutopilot.Core.Paths;

namespace DevAutopilot.Core.Policy;

public static class ToolPolicyEvaluator
{
    /// <summary>Tool input fields that name a file or folder.</summary>
    private static readonly string[] PathFields = ["file_path", "notebook_path", "path"];

    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal)
    {
        "Write", "Edit", "MultiEdit", "NotebookEdit",
    };

    /// <summary>Claude Code's built-in tool for returning a --json-schema answer.</summary>
    public const string StructuredOutputTool = "StructuredOutput";

    /// <summary>
    /// Tools the evaluator has rules for. Any other tool (PowerShell, Task, Agent, WebFetch, ...)
    /// is denied even when a role lists it, because nothing here checks what it does.
    /// MCP tools (<c>mcp__*</c>) are also known; they are checked like any other tool.
    /// </summary>
    public static IReadOnlyList<string> KnownTools { get; } =
        ["Read", "Grep", "Glob", "Edit", "Write", "MultiEdit", "NotebookEdit", "Bash", StructuredOutputTool];

    /// <summary>
    /// Shell text that could chain, redirect or substitute commands, expand variables or the home
    /// folder (<c>$</c>, <c>%</c>, <c>~</c>), or hide a token boundary inside quotes.
    /// </summary>
    private static readonly string[] ForbiddenShellText =
        [";", "&", "|", ">", "<", "`", "$", "%", "~", "\"", "'", "\n", "\r"];

    /// <summary>
    /// Argument prefixes denied after an allowed command prefix. They write output to a chosen place
    /// (<c>--output</c>, <c>-o</c>), compare or run files outside git's control (<c>--no-index</c>,
    /// <c>--ext-diff</c>, <c>--exec</c>), change git's or MSBuild's configuration or target folder
    /// (<c>--config</c>, <c>-c</c>, <c>--git-dir</c>, <c>--work-tree</c>, <c>-C</c>), or load code and
    /// properties from elsewhere (<c>--source</c>, <c>--property</c>, <c>-p:</c>, <c>/p:</c>).
    /// Matching ignores case, so <c>/P:</c> is covered by <c>/p:</c>.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenArgumentPrefixes { get; } =
    [
        "--output", "-o", "--no-index", "--ext-diff", "--exec", "--config", "-c",
        "--git-dir", "--work-tree", "-C", "--source", "--property", "-p:", "/p:",
    ];

    /// <summary>Decides one tool call. callNumber is 1 for the first call of the run.</summary>
    public static PolicyDecision Evaluate(ToolCall call, ToolPolicy policy, int callNumber)
    {
        try
        {
            return EvaluateCore(call, policy, callNumber);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fail closed: anything unexpected is a deny.
            return PolicyDecision.Deny($"policy check failed: {ex.Message}");
        }
    }

    private static PolicyDecision EvaluateCore(ToolCall call, ToolPolicy policy, int callNumber)
    {
        if (call is null || policy is null)
        {
            return PolicyDecision.Deny("no tool call or no policy");
        }

        // Claude Code returns --json-schema answers through this tool. It only carries the
        // final answer, touches no files, and must work even at the call limit so a run can end.
        if (string.Equals(call.ToolName, StructuredOutputTool, StringComparison.Ordinal))
        {
            return PolicyDecision.Allow("final structured answer");
        }

        var tool = call.ToolName ?? string.Empty;
        if (!IsKnownTool(tool))
        {
            return PolicyDecision.Deny($"no policy for tool {tool}");
        }

        if (callNumber > policy.MaxToolCalls)
        {
            return PolicyDecision.Deny($"tool call limit {policy.MaxToolCalls} reached");
        }

        if (!(policy.AllowedTools ?? []).Contains(tool, StringComparer.Ordinal))
        {
            return PolicyDecision.Deny($"tool '{tool}' is not allowed for the {policy.Role} role");
        }

        if (string.IsNullOrWhiteSpace(policy.WorktreeRoot) || !Path.IsPathFullyQualified(policy.WorktreeRoot))
        {
            return PolicyDecision.Deny("the policy has no absolute worktree root");
        }

        var input = call.ToolInput;
        var hasObject = input.ValueKind == JsonValueKind.Object;

        // Every path argument, whatever the tool, must stay inside the worktree.
        var relativePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (hasObject)
        {
            foreach (var field in PathFields)
            {
                if (!input.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (value.ValueKind != JsonValueKind.String)
                {
                    return PolicyDecision.Deny($"'{field}' must be a string");
                }

                var raw = value.GetString()!;
                var check = ResolveInsideWorktree(raw, policy.WorktreeRoot);
                if (check.Error is not null)
                {
                    return PolicyDecision.Deny($"{field} '{raw}': {check.Error}");
                }

                relativePaths[field] = check.Relative!;
            }
        }

        if (WriteTools.Contains(tool))
        {
            var field = tool == "NotebookEdit" ? "notebook_path" : "file_path";
            if (!relativePaths.TryGetValue(field, out var relative) || relative.Length == 0)
            {
                return PolicyDecision.Deny($"{tool} needs a '{field}' that names a file");
            }

            if (GlobMatcher.IsMatch(relative, policy.ProtectedPaths ?? []))
            {
                return PolicyDecision.Deny($"'{relative}' is protected and cannot be written");
            }

            if (!GlobMatcher.IsMatch(relative, policy.WritablePaths ?? []))
            {
                return PolicyDecision.Deny($"'{relative}' is outside the writable paths");
            }

            return PolicyDecision.Allow($"{tool} on '{relative}' is inside the writable paths");
        }

        if (tool == "Glob" && hasObject && input.TryGetProperty("pattern", out var pattern))
        {
            if (pattern.ValueKind != JsonValueKind.String)
            {
                return PolicyDecision.Deny("'pattern' must be a string");
            }

            var text = pattern.GetString()!.Replace('\\', '/');
            if (text.StartsWith('/') || text.Contains(':', StringComparison.Ordinal) || text.Split('/').Contains(".."))
            {
                return PolicyDecision.Deny($"glob pattern '{pattern.GetString()}' may reach outside the worktree");
            }
        }

        if (tool == "Bash")
        {
            return EvaluateBash(input, policy);
        }

        return relativePaths.Count > 0
            ? PolicyDecision.Allow($"{tool} inside the worktree")
            : PolicyDecision.Allow($"{tool} is allowed for the {policy.Role} role");
    }

    private static PolicyDecision EvaluateBash(JsonElement input, ToolPolicy policy)
    {
        var allowed = policy.AllowedCommands ?? [];
        var configured = (policy.ConfiguredCommands ?? [])
            .Select(c => c?.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .ToList();
        if (allowed.Count == 0 && configured.Count == 0)
        {
            return PolicyDecision.Deny($"no shell commands are allowed for the {policy.Role} role");
        }

        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("command", out var commandValue)
            || commandValue.ValueKind != JsonValueKind.String)
        {
            return PolicyDecision.Deny("Bash needs a 'command' string");
        }

        var command = commandValue.GetString()!.Trim();
        if (command.Length == 0)
        {
            return PolicyDecision.Deny("empty shell command");
        }

        // The project's own build and test command lines come from its reviewed config, so they are
        // allowed exactly as written. Extra trailing arguments must pass the normal rules.
        foreach (var line in configured)
        {
            if (command.Equals(line, StringComparison.Ordinal))
            {
                return PolicyDecision.Allow("shell command is the configured build or test command");
            }

            if (command.StartsWith(line + " ", StringComparison.Ordinal))
            {
                var extra = command[line.Length..];
                if (ForbiddenTextProblem(extra) is null && CheckArguments(extra) is null)
                {
                    return PolicyDecision.Allow("shell command is the configured build or test command with extra arguments");
                }
            }
        }

        var textProblem = ForbiddenTextProblem(command);
        if (textProblem is not null)
        {
            return PolicyDecision.Deny(textProblem);
        }

        foreach (var entry in allowed)
        {
            var prefix = entry?.Trim();
            if (string.IsNullOrEmpty(prefix))
            {
                continue;
            }

            if (command.Equals(prefix, StringComparison.Ordinal)
                || command.StartsWith(prefix + " ", StringComparison.Ordinal))
            {
                var argumentProblem = CheckArguments(command[prefix.Length..]);
                return argumentProblem is null
                    ? PolicyDecision.Allow($"shell command matches allowed '{prefix}'")
                    : PolicyDecision.Deny($"shell command '{command}': {argumentProblem}");
            }
        }

        return PolicyDecision.Deny($"shell command '{command}' is not on the allowed list");
    }

    /// <summary>Why the shell text is refused (chaining, redirection, variables, quotes, newlines), or null.</summary>
    private static string? ForbiddenTextProblem(string command)
    {
        foreach (var forbidden in ForbiddenShellText)
        {
            if (command.Contains(forbidden, StringComparison.Ordinal))
            {
                var shown = forbidden switch
                {
                    "\n" => "a newline",
                    "\r" => "a carriage return",
                    _ => $"'{forbidden}'",
                };
                return $"shell command contains {shown}; chaining, redirection, substitution, variables and quotes are not allowed";
            }
        }

        return null;
    }

    public static bool IsKnownTool(string tool) =>
        (tool.StartsWith("mcp__", StringComparison.Ordinal) && tool.Length > "mcp__".Length)
        || KnownTools.Contains(tool, StringComparer.Ordinal);

    /// <summary>
    /// Checks the arguments after the allowed prefix (the prefix itself comes from the project's own
    /// config). Tokens are split on whitespace; quotes are already denied, so a token cannot hide a space.
    /// Returns null when every token is fine.
    /// </summary>
    /// <summary>
    /// Long options ("--output") match the exact token or "--output=value", so "--configuration"
    /// is not mistaken for "--config". Short options ("-o") and options ending in ':' ("-p:")
    /// also match with an attached value ("-obin", "-p:X=1").
    /// </summary>
    private static bool IsForbiddenOption(string token, string option)
    {
        if (token.Equals(option, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var isLong = option.StartsWith("--", StringComparison.Ordinal);
        return isLong
            ? token.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase)
            : token.StartsWith(option, StringComparison.OrdinalIgnoreCase);
    }

    private static string? CheckArguments(string arguments)
    {
        foreach (var token in arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var prefix = ForbiddenArgumentPrefixes.FirstOrDefault(p => IsForbiddenOption(token, p));
            if (prefix is not null)
            {
                return $"argument '{token}' is not allowed ('{prefix}' options can write, run or read outside the worktree)";
            }

            if (token.Contains("..", StringComparison.Ordinal))
            {
                return $"argument '{token}' contains '..'";
            }

            if (token.Contains(':', StringComparison.Ordinal))
            {
                return $"argument '{token}' contains ':'";
            }

            // Covers "/x", "\x" and "\\server\share"; drive paths already failed on ':'.
            if (token.StartsWith('/') || token.StartsWith('\\') || Path.IsPathRooted(token))
            {
                return $"argument '{token}' is an absolute path";
            }
        }

        return null;
    }

    private readonly record struct PathCheck(string? Relative, string? Error);

    private static PathCheck ResolveInsideWorktree(string rawPath, string worktreeRoot)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return new(null, "empty path");
        }

        if (rawPath.Contains('\0'))
        {
            return new(null, "path contains a null character");
        }

        var path = rawPath.Replace('\\', '/');

        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            return new(null, "network and device paths are not allowed");
        }

        var hasDrive = path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
        if (hasDrive)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new(null, "drive paths are not allowed");
            }

            if (path.Length == 2 || path[2] != '/')
            {
                return new(null, "drive-relative paths are not allowed");
            }
        }

        // Any other colon (for example an NTFS alternate data stream) is refused.
        if (path.IndexOf(':', hasDrive ? 2 : 0) >= 0)
        {
            return new(null, "path contains ':'");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreeRoot));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, root));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var inside = full.Equals(root, comparison)
                     || full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        if (!inside)
        {
            return new(null, "outside the worktree");
        }

        var relative = GlobMatcher.Normalize(Path.GetRelativePath(root, full));
        if (relative == ".")
        {
            relative = string.Empty;
        }

        var first = relative.Split('/', 2)[0];
        if (first.Equals(".git", StringComparison.OrdinalIgnoreCase))
        {
            return new(null, "access to .git is not allowed");
        }

        var link = FindLink(root, relative);
        if (link is not null)
        {
            return new(null, $"'{link}' is a link (symlink, junction or other reparse point); links are not followed");
        }

        return new(relative, null);
    }

    /// <summary>
    /// Walks every existing segment from the worktree root down to the target and returns the first
    /// one that is a symlink, junction or other reparse point, or null. A link inside the worktree
    /// could point anywhere, so the path check above would not hold for it.
    /// </summary>
    private static string? FindLink(string root, string relative)
    {
        var current = root;
        var shown = string.Empty;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            shown = shown.Length == 0 ? segment : shown + "/" + segment;

            var info = new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                return shown;
            }

            if (!info.Exists && !Directory.Exists(current))
            {
                // Nothing exists from here down, so nothing further can be a link.
                return null;
            }

            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return shown;
            }
        }

        return null;
    }
}
