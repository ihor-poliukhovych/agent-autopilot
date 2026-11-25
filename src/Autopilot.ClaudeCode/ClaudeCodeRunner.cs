using System.Diagnostics;
using System.Text;
using Autopilot.Core;

namespace Autopilot.ClaudeCode;

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

        ClaudeEnvironment.Scrub(startInfo.Environment);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ClaudeResultParser.Failure($"Could not start Claude Code at \"{executable}\": {ex.Message}", clock.Elapsed);
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

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

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        await File.WriteAllTextAsync(files.StdoutFile, stdout, CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(files.StderrFile, stderr, CancellationToken.None).ConfigureAwait(false);

        if (killed)
        {
            ct.ThrowIfCancellationRequested();
            return ClaudeResultParser.Failure(
                $"Claude Code did not finish within {Options.Timeout} and was stopped.",
                clock.Elapsed);
        }

        return ClaudeResultParser.Parse(
            stdout,
            stderr,
            process.ExitCode,
            clock.Elapsed,
            expectStructuredOutput: !string.IsNullOrWhiteSpace(request.OutputJsonSchema));
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

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Could not kill (already gone or access denied); nothing more to do.
        }
    }
}
