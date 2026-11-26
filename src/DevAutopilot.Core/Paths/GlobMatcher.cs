using Microsoft.Extensions.FileSystemGlobbing;

namespace DevAutopilot.Core.Paths;

public static class GlobMatcher
{
    /// <summary>Turns a path into a project-relative form with forward slashes and no leading "./".</summary>
    public static string Normalize(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var path = relativePath.Replace('\\', '/');
        while (path.Contains("//", StringComparison.Ordinal))
        {
            path = path.Replace("//", "/", StringComparison.Ordinal);
        }

        while (true)
        {
            if (path.StartsWith("./", StringComparison.Ordinal))
            {
                path = path[2..];
            }
            else if (path.StartsWith('/'))
            {
                path = path[1..];
            }
            else
            {
                break;
            }
        }

        return path;
    }

    /// <summary>True when the project-relative path matches any of the glob patterns (case-insensitive).</summary>
    public static bool IsMatch(string relativePath, IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(patterns);

        var path = Normalize(relativePath);
        if (path.Length == 0)
        {
            return false;
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        var any = false;
        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var pattern = Normalize(raw.Trim());
            matcher.AddInclude(pattern);
            any = true;

            // "**/x" must also match "x" at the project root.
            while (pattern.StartsWith("**/", StringComparison.Ordinal))
            {
                pattern = pattern[3..];
                if (pattern.Length > 0)
                {
                    matcher.AddInclude(pattern);
                }
            }
        }

        return any && matcher.Match(path).HasMatches;
    }
}
