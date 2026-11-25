using System.Diagnostics;
using System.Text;

namespace Autopilot.Core.Process;

/// <summary>Runs an executable directly, with arguments passed one by one (no shell).</summary>
public static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public static Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return RunAsync(startInfo, timeout, environment, ct);
    }

    /// <summary>Runs a prepared start info. Output is redirected and read as UTF-8.</summary>
    public static async Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken ct = default)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;
        startInfo.CreateNoWindow = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var combined = new StringBuilder();
        var gate = new object();
        var stopwatch = Stopwatch.StartNew();

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => Append(e.Data, stdout);
        process.ErrorDataReceived += (_, e) => Append(e.Data, stderr);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            var message = $"Could not start '{startInfo.FileName}': {ex.Message}";
            return new ProcessResult(-1, string.Empty, message, message, false, stopwatch.Elapsed);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        // WaitForExitAsync returns after the output streams reach end of file.
        lock (gate)
        {
            return new ProcessResult(
                timedOut ? -1 : process.ExitCode,
                stdout.ToString(),
                stderr.ToString(),
                combined.ToString(),
                timedOut,
                stopwatch.Elapsed);
        }

        void Append(string? line, StringBuilder target)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                target.AppendLine(line);
                combined.AppendLine(line);
            }
        }
    }

    private static void Kill(System.Diagnostics.Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
