using System.Diagnostics;

namespace DevAutopilot.Core.Process;

/// <summary>
/// Runs a configured command line through the platform shell: <c>cmd /c</c> on Windows,
/// <c>/bin/sh -c</c> elsewhere. Used for the project's build and test commands, so the child always
/// starts from the <see cref="ChildEnvironment"/> allow-list plus <c>passEnvironment</c>.
/// </summary>
public static class ShellRunner
{
    public static Task<ProcessResult> RunAsync(
        string commandLine,
        string workingDirectory,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken ct = default,
        IReadOnlyList<string>? passEnvironment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        return ProcessRunner.RunAsync(
            CreateStartInfo(commandLine, workingDirectory), timeout, environment, ct, cleanEnvironment: true, passEnvironment);
    }

    public static ProcessStartInfo CreateStartInfo(string commandLine, string workingDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            // /s with outer quotes: cmd removes exactly the outer pair and runs the rest as typed.
            return new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /s /c \"{commandLine}\"",
                WorkingDirectory = workingDirectory,
            };
        }

        var startInfo = new ProcessStartInfo("/bin/sh") { WorkingDirectory = workingDirectory };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(commandLine);
        return startInfo;
    }
}
