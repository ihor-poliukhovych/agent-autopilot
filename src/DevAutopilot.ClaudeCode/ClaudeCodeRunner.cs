using System.Diagnostics;
using System.Text;
using DevAutopilot.ClaudeCode.Hooks;
using DevAutopilot.Core;
using DevAutopilot.Core.Process;

namespace DevAutopilot.ClaudeCode;

/// <summary>Runs one agent role as a headless <c>claude -p</c> process behind the PreToolUse hook.</summary>
public sealed class ClaudeCodeRunner(ClaudeCodeRunnerOptions options) : IAgentRunner
{
    public ClaudeCodeRunnerOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var clock = Stopwatch.StartNew();

        string? problem = Validate(request);
        if (problem is not null)
        {
            return ClaudeResultParser.Failure(problem, clock.Elapsed);
        }

        string? executable = ClaudeExecutableResolver.Resolve(Options.ClaudeExecutable);
        if (executable is null)
        {
            return ClaudeResultParser.Failure(
                $"Claude Code was not found (\"{Options.ClaudeExecutable}\"). Install it or set the full path of claude (claude.exe on Windows).",
                clock.Elapsed);
        }

        ClaudeRunFiles files = ClaudeRunFileWriter.Write(request, Options);
        IReadOnlyList<string> arguments = ClaudeArgumentBuilder.Build(request, files, Options);

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetFullPath(request.WorkingDirectory),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ClaudeEnvironment.Apply(startInfo.Environment, Options.PassEnvironment);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ClaudeResultParser.Failure($"Could not start Claude Code at \"{executable}\": {ex.Message}", clock.Elapsed);
        }

        TextCapture stdoutCapture = TextCapture.Start(process.StandardOutput);
        TextCapture stderrCapture = TextCapture.Start(process.StandardError);

        using var timeout = new CancellationTokenSource(Options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        bool killed = false;
        try
        {
            try
            {
                await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), linked.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The process exited before reading its input; its output says why.
            }
            finally
            {
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            killed = true;
        }

        // A grandchild can keep the pipes open after Claude Code is gone; wait for the readers for a
        // bounded time only, then go on with what was captured.
        string stdout = await stdoutCapture.TextAfterAsync(ProcessRunner.ReaderGracePeriod).ConfigureAwait(false);
        string stderr = await stderrCapture.TextAfterAsync(ProcessRunner.ReaderGracePeriod).ConfigureAwait(false);
        await File.WriteAllTextAsync(files.StdoutFile, stdout, CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(files.StderrFile, stderr, CancellationToken.None).ConfigureAwait(false);

        if (killed)
        {
            ct.ThrowIfCancellationRequested();
            return ClaudeResultParser.Failure(
                $"Claude Code did not finish within {Options.Timeout} and was stopped.",
                clock.Elapsed);
        }

        AgentRunResult result = ClaudeResultParser.Parse(
            stdout,
            stderr,
            process.HasExited ? process.ExitCode : -1,
            clock.Elapsed,
            expectStructuredOutput: !string.IsNullOrWhiteSpace(request.OutputJsonSchema));
        return CheckHookRan(result, files);
    }

    /// <summary>
    /// More than one turn means the agent called at least one tool, so the hook must have logged a
    /// decision. When it did not, the hook was not in force (for example Claude Code did not load the
    /// settings), and the run cannot be trusted.
    /// </summary>
    public static AgentRunResult CheckHookRan(AgentRunResult result, ClaudeRunFiles files)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(files);
        if (result.Turns <= 1)
        {
            return result;
        }

        string log = Path.Combine(files.RunDirectory, PreToolUseHook.LogFileName);
        bool hasEntries = File.Exists(log) && new FileInfo(log).Length > 0;
        if (hasEntries)
        {
            return result;
        }

        return result with
        {
            Succeeded = false,
            Error = $"the policy hook did not run: Claude Code reported {result.Turns} turns but {PreToolUseHook.LogFileName} is missing or empty, so the run is not trusted.",
        };
    }

    private static string? Validate(AgentRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
        {
            return $"The working directory \"{request.WorkingDirectory}\" does not exist.";
        }

        if (string.IsNullOrWhiteSpace(request.RunDirectory))
        {
            return "The run directory is not set.";
        }

        string work = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.WorkingDirectory)) + Path.DirectorySeparatorChar;
        string run = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.RunDirectory)) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (run.StartsWith(work, comparison))
        {
            return $"The run directory \"{request.RunDirectory}\" must be outside the working directory, so the agent cannot change its own policy.";
        }

        return null;
    }

    /// <summary>Kills the process tree and waits at most 10 s for it to exit. Never throws.</summary>
    private static void Kill(Process process)
    {
        ProcessRunner.Kill(process);
        try
        {
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            // Already gone or not ours to wait for; the capped reader waits end the run.
        }
    }
}
