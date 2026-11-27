namespace Autopilot.ClaudeCode;

/// <summary>
/// Finds the Claude Code program to start. Pure logic over a PATH value and a file-exists check,
/// so it can be tested for both Windows and Unix on any machine.
/// </summary>
public static class ClaudeExecutableResolver
{
    /// <summary>Where the npm package keeps the native binary, relative to the folder of the npm shim.</summary>
    public static readonly string[] NpmBinaryRelativePath = ["node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"];

    private static readonly string[] WindowsShimExtensions = [".cmd", ".bat", ".ps1"];

    public static string? Resolve(string configured) => Resolve(
        configured,
        Environment.GetEnvironmentVariable("PATH"),
        OperatingSystem.IsWindows(),
        File.Exists);

    /// <returns>The full path of a directly startable executable, or null when none was found.</returns>
    public static string? Resolve(string configured, string? pathVariable, bool isWindows, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        char separator = isWindows ? ';' : ':';

        if (IsPath(configured, isWindows))
        {
            return ResolveFile(configured, isWindows, fileExists);
        }

        string[] directories = (pathVariable ?? string.Empty)
            .Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('"'))
            .ToArray();

        if (!isWindows)
        {
            return directories.Select(d => Join(d, configured, isWindows)).FirstOrDefault(fileExists);
        }

        string extension = GetExtension(configured);
        IEnumerable<string> names = extension.Length > 0
            ? [configured]
            : [configured + ".exe", configured + ".cmd", configured + ".bat", configured + ".ps1"];

        // Prefer a real .exe anywhere on PATH over a shim that comes earlier.
        foreach (string name in names.OrderBy(n => GetExtension(n).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            foreach (string directory in directories)
            {
                string? found = ResolveFile(Join(directory, name, isWindows), isWindows, fileExists);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string? ResolveFile(string path, bool isWindows, Func<string, bool> fileExists)
    {
        if (!isWindows)
        {
            return fileExists(path) ? path : null;
        }

        string extension = GetExtension(path);
        if (WindowsShimExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            if (!fileExists(path))
            {
                return null;
            }

            string directory = GetDirectory(path, isWindows);
            string binary = NpmBinaryRelativePath.Aggregate(directory, (current, part) => Join(current, part, isWindows));
            return fileExists(binary) ? binary : null;
        }

        if (extension.Length == 0 && !fileExists(path))
        {
            string withExe = path + ".exe";
            return fileExists(withExe) ? withExe : null;
        }

        return fileExists(path) ? path : null;
    }

    private static bool IsPath(string value, bool isWindows) =>
        value.Contains('/') || (isWindows && (value.Contains('\\') || (value.Length > 1 && value[1] == ':')));

    private static string Join(string directory, string name, bool isWindows)
    {
        char separator = isWindows ? '\\' : '/';
        return directory.EndsWith('/') || directory.EndsWith('\\') ? directory + name : directory + separator + name;
    }

    private static string GetDirectory(string path, bool isWindows)
    {
        int index = isWindows ? path.LastIndexOfAny(['\\', '/']) : path.LastIndexOf('/');
        return index < 0 ? "." : path[..index];
    }

    private static string GetExtension(string path)
    {
        int slash = path.LastIndexOfAny(['\\', '/']);
        int dot = path.LastIndexOf('.');
        return dot > slash && dot >= 0 ? path[dot..] : string.Empty;
    }
}
