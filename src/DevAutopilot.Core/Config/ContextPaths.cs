namespace DevAutopilot.Core.Config;

/// <summary>
/// Rules for the <c>context</c> setting: project-relative paths of files or folders whose text the
/// agents get as project rules. They are plain paths, not glob patterns.
/// </summary>
public static class ContextPaths
{
    private static readonly char[] Wildcards = ['*', '?', '[', ']', '{', '}'];

    /// <summary>Forward slashes, no leading "./" and no trailing "/". "docs/decisions/" becomes "docs/decisions".</summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimEnd('/');
    }

    /// <summary>What is wrong with one entry, in plain English, or null when it is fine.</summary>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "context has an empty entry. Remove it.";
        }

        var raw = path.Trim().Replace('\\', '/');
        var normalized = Normalize(path);
        if (raw.StartsWith('/') || raw.Contains(':', StringComparison.Ordinal) || Path.IsPathRooted(path.Trim()))
        {
            return $"context entry \"{path}\" is an absolute path. Use a path relative to the project root, for example \"docs/agent-rules.md\".";
        }

        if (normalized.Length == 0 || normalized == ".")
        {
            return $"context entry \"{path}\" names the whole project. List the rule files or folders, for example \"docs/agent-rules.md\".";
        }

        if (normalized.Split('/').Any(s => s == ".."))
        {
            return $"context entry \"{path}\" contains '..'. Context files must be inside the project.";
        }

        if (normalized.IndexOfAny(Wildcards) >= 0)
        {
            return $"context entry \"{path}\" has a wildcard. List a file or a folder instead; a folder includes every file in it.";
        }

        return null;
    }

    /// <summary>
    /// The glob patterns that protect the context paths from writes: the path itself and, for a folder,
    /// everything under it. Entries with a <see cref="Problem"/> are left out.
    /// </summary>
    public static IReadOnlyList<string> ProtectedPatterns(IEnumerable<string>? paths) =>
        (paths ?? [])
            .Where(p => Problem(p) is null)
            .Select(Normalize)
            .SelectMany(p => new[] { p, p + "/**" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
