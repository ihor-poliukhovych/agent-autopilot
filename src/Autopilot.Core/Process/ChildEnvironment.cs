namespace Autopilot.Core.Process;

/// <summary>
/// The environment allow-list for child processes that run agent work: Claude Code itself and the
/// project's build and test commands. A child starts from an empty environment and gets only the
/// variables a toolchain needs, so tokens and secrets in the user's environment do not reach code the
/// agent wrote. Git calls made by the harness itself keep the full environment.
/// </summary>
public static class ChildEnvironment
{
    /// <summary>Variables passed by name (any case).</summary>
    public static IReadOnlyList<string> AllowedNames { get; } =
    [
        "PATH", "PATHEXT", "SystemRoot", "windir", "ComSpec", "SystemDrive", "OS", "TEMP", "TMP",
        "USERPROFILE", "HOME", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles",
        "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "USERNAME", "LANG", "LC_ALL", "TERM",
    ];

    /// <summary>Variables passed by prefix (any case): .NET, NuGet and MSBuild settings.</summary>
    public static IReadOnlyList<string> AllowedPrefixes { get; } = ["DOTNET_", "NUGET_", "MSBUILD"];

    /// <summary>Prefixes never passed, even when listed in <c>passEnvironment</c>.</summary>
    public static IReadOnlyList<string> NeverPassedPrefixes { get; } = ["ANTHROPIC_", "AUTOPILOT_"];

    public static bool IsNeverPassed(string name) =>
        NeverPassedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static bool IsAllowed(string name, IEnumerable<string>? passEnvironment = null)
    {
        if (string.IsNullOrEmpty(name) || IsNeverPassed(name))
        {
            return false;
        }

        return AllowedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || AllowedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || (passEnvironment ?? []).Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Removes, in place, every variable that is not on the allow-list.</summary>
    public static void Apply(IDictionary<string, string?> environment, IEnumerable<string>? passEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var extra = passEnvironment?.ToList() ?? [];
        foreach (var key in environment.Keys.Where(k => !IsAllowed(k, extra)).ToList())
        {
            environment.Remove(key);
        }
    }
}
