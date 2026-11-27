using Autopilot.Core.Process;

namespace Autopilot.AzureDevOps;

/// <summary>What to start for the Azure CLI: the full path of the launcher and the environment changes for the child.</summary>
public sealed record AzureCliLaunch(string FileName, IReadOnlyDictionary<string, string?> Environment);

/// <summary>
/// Finds the Azure CLI launcher and builds how to start it.
/// </summary>
/// <remarks>
/// On Windows the launcher is <c>az.cmd</c>. Started by its bare name from another process, it cannot find its own
/// folder (the batch file's <c>%~dp0</c> is wrong), so it prints "Failed to load python executable." and exits 1,
/// even though the same command works in a shell. Starting it by full path fixes that, so the path is always
/// resolved from PATH first.
/// </remarks>
public static class AzureCliLauncher
{
    /// <summary>Variables that can point the CLI's Python at the wrong installation. They are removed for the child.</summary>
    public static readonly IReadOnlyList<string> PythonVariables = ["PYTHONHOME", "PYTHONPATH"];

    /// <param name="windows">True to look for az.cmd / az.exe; false for a plain az.</param>
    /// <param name="path">The PATH value.</param>
    /// <param name="exists">Whether a file exists (for tests).</param>
    /// <returns>The full path, or null when az is not on PATH.</returns>
    public static string? Resolve(bool windows, string? path, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var names = windows ? new[] { "az.cmd", "az.exe" } : ["az"];
        var separator = windows ? ';' : ':';
        foreach (var folder in (path ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(folder.Trim('"'), name);
                if (exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static AzureCliLaunch Build(string fullPath)
    {
        var environment = new Dictionary<string, string?> { ["AZURE_CORE_ONLY_SHOW_ERRORS"] = "true" };
        foreach (var variable in PythonVariables)
        {
            environment[variable] = null;
        }

        return new AzureCliLaunch(fullPath, environment);
    }

    /// <summary>The first non-empty line of the CLI's error text, cut to a readable length. Never contains a token: az prints none on stderr.</summary>
    public static string FirstLine(string? text)
    {
        var line = (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return line.Length > 200 ? line[..200] + "..." : line;
    }

    /// <summary>Starts az with the full path resolved from the real PATH.</summary>
    public static Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var path = Resolve(OperatingSystem.IsWindows(), System.Environment.GetEnvironmentVariable("PATH"), File.Exists);
        if (path is null)
        {
            var message = "The Azure CLI (az) was not found on PATH.";
            return Task.FromResult(new ProcessResult(-1, string.Empty, message, message, false, TimeSpan.Zero));
        }

        var launch = Build(path);
        return ProcessRunner.RunAsync(launch.FileName, arguments, System.Environment.CurrentDirectory, timeout, launch.Environment, ct);
    }
}
