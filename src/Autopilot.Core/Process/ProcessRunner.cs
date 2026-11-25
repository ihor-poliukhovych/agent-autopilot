using System.Diagnostics;
using System.Text;

namespace Autopilot.Core.Process;

/// <summary>Runs an executable directly, with arguments passed one by one (no shell).</summary>
public static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// After a kill, how long to wait for the output readers. A grandchild that escaped the kill can
    /// hold the pipes open forever; after this the runner returns what it has.
    /// </summary>
    public static readonly TimeSpan ReaderGracePeriod = TimeSpan.FromSeconds(10);

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

    /// <summary>
    /// Runs a prepared start info. Output is redirected and read as UTF-8. With
    /// <paramref name="cleanEnvironment"/> the child starts from <see cref="ChildEnvironment"/>'s
    /// allow-list plus <paramref name="passEnvironment"/>; <paramref name="environment"/> is applied after that.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken ct = default,
        bool cleanEnvironment = false,
        IReadOnlyList<string>? passEnvironment = null)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;
        startInfo.CreateNoWindow = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        if (cleanEnvironment)
        {
            ChildEnvironment.Apply(startInfo.Environment, passEnvironment);
        }

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
            await WaitAtMostAsync(process.WaitForExitAsync(CancellationToken.None), ReaderGracePeriod).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        // WaitForExitAsync returns after the output streams reach end of file; after a kill that
        // wait is capped, so the text below may be cut.
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

    /// <summary>Waits for a task for at most <paramref name="limit"/>. True when it finished in time.</summary>
    public static async Task<bool> WaitAtMostAsync(Task task, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(task);
        var finished = await Task.WhenAny(task, Task.Delay(limit)).ConfigureAwait(false);
        if (finished != task)
        {
            // Observe a later failure so it is not reported as unobserved.
            _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return false;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The reader ended badly; what was captured is still returned.
        }

        return true;
    }

    /// <summary>Kills the process tree. Never throws: a failed kill leaves the capped waits to end the run.</summary>
    public static void Kill(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied or already gone.
        }
        catch (AggregateException)
        {
            // Some processes of the tree could not be killed.
        }
        catch (NotSupportedException)
        {
            // Remote process; not ours.
        }
    }
}
