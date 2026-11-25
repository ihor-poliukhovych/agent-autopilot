using System.Text.Json;
using Autopilot.Core.Paths;

namespace Autopilot.Core.Policy;

public static class ToolPolicyEvaluator
{
    /// <summary>Tool input fields that name a file or folder.</summary>
    private static readonly string[] PathFields = ["file_path", "notebook_path", "path"];

    private static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal)
    {
        "Write", "Edit", "MultiEdit", "NotebookEdit",
    };

    /// <summary>Shell text that could chain, redirect or substitute commands.</summary>
    private static readonly string[] ForbiddenShellText = [";", "&", "|", ">", "<", "`", "$(", "\n", "\r"];

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

        if (callNumber > policy.MaxToolCalls)
        {
            return PolicyDecision.Deny($"tool call limit {policy.MaxToolCalls} reached");
        }

        var tool = call.ToolName ?? string.Empty;
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
        if (allowed.Count == 0)
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
                return PolicyDecision.Deny(
                    $"shell command contains {shown}; chaining, redirection and substitution are not allowed");
            }
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
                return PolicyDecision.Allow($"shell command matches allowed '{prefix}'");
            }
        }

        return PolicyDecision.Deny($"shell command '{command}' is not on the allowed list");
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

        return new(relative, null);
    }
}
