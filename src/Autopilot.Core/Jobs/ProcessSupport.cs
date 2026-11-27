using System.Diagnostics;

namespace Autopilot.Core.Jobs;

/// <summary>Starts a program that keeps running after the caller exits, with no window and no shared console.</summary>
public sealed class DetachedProcessLauncher : IJobLauncher
{
    public int Launch(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (OperatingSystem.IsWindows())
        {
            // A shell-executed process is not a child of this console, so closing the terminal does not stop it.
            startInfo.UseShellExecute = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        using var process = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException($"{fileName} could not be started.");
        return process.Id;
    }
}

/// <summary>Kills one process and the processes it started.</summary>
public sealed class ProcessTreeKiller : IProcessKiller
{
    public void Kill(int pid)
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
    }
}
