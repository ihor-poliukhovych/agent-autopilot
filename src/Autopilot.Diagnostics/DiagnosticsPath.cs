namespace Autopilot.Diagnostics;

/// <summary>A diagnostics setting that cannot be used; the message says what to change.</summary>
public sealed class DiagnosticsConfigException(string message) : Exception(message);

/// <summary>Resolves the configured log file path and keeps it inside the project root.</summary>
public static class DiagnosticsPath
{
    /// <summary>
    /// Returns the full path of <paramref name="configuredPath"/>. A relative path is resolved against the
    /// project root; an absolute path is accepted only when it is inside the project root. Paths that
    /// leave the root (<c>..</c>), network paths and NTFS stream names are rejected.
    /// </summary>
    public static string Resolve(string projectRoot, string? configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new DiagnosticsConfigException(
                "modules.diagnostics.settings.path is not set. Set it to the JSON Lines log file, relative to the project root (for example \"telemetry/app.jsonl\").");
        }

        var raw = configuredPath.Trim();
        if (raw.Contains('\0'))
        {
            throw new DiagnosticsConfigException("modules.diagnostics.settings.path contains a null character.");
        }

        var normalized = raw.Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            throw new DiagnosticsConfigException($"The diagnostics path \"{raw}\" is a network or device path. Use a file inside the project.");
        }

        var hasDrive = normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':';
        if (normalized.IndexOf(':', hasDrive ? 2 : 0) >= 0)
        {
            throw new DiagnosticsConfigException($"The diagnostics path \"{raw}\" contains ':'. Use a plain file path inside the project.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var full = Path.GetFullPath(normalized, root);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new DiagnosticsConfigException(
                $"The diagnostics path \"{raw}\" is outside the project root {root}. Put the log file inside the project and use a relative path.");
        }

        return full;
    }
}
