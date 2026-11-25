using System.Text;
using Autopilot.Core.Config;
using Autopilot.Core.Gate;
using Autopilot.Core.Git;
using Autopilot.Core.Process;

namespace Autopilot.Core.Facts;

/// <summary>Collects the facts the merge gate decides on.</summary>
public interface IGateFactsCollector
{
    Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct);
}

/// <summary>
/// Builds and tests the base commit and the branch commit in fresh worktrees of their own (never the
/// agent's worktree, which may hold files the agent did not commit) and reads the diff from git.
/// </summary>
public sealed class GateFactsCollector(GitClient git, AutopilotConfig config, string worktreeRoot) : IGateFactsCollector
{
    private const int MaxLogCharsPerStep = 20_000;

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Set on top of the clean <see cref="ChildEnvironment"/> for build and test runs: English output,
    /// no build servers that hold files open.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> CommandEnvironment { get; } = new Dictionary<string, string?>
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
        ["UseSharedCompilation"] = "false",
    };

    public async Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct)
    {
        var changes = await git.GetChangesAsync(baseCommit, headCommit, ct).ConfigureAwait(false);
        var log = new StringBuilder();

        var before = await BuildAndTestAsync(workItemId, "gate-base", baseCommit, log, ct).ConfigureAwait(false);
        var after = await BuildAndTestAsync(workItemId, "gate-head", headCommit, log, ct).ConfigureAwait(false);

        var tests = new TestFacts(
            BuildSucceeded: after.BuildSucceeded,
            TestsSucceeded: after.BuildSucceeded && after.TestsSucceeded,
            TestCountBefore: before.Counts.Total,
            TestCountAfter: after.Counts.Total,
            SkipsAdded: Math.Max(0, after.Counts.Skipped - before.Counts.Skipped),
            Log: log.ToString(),
            TestCountsKnown: before.Counts.Found && after.Counts.Found);
        return new GateInput(changes, tests, evidence);
    }

    /// <summary>
    /// Internal folder names start with '@', which work item ids may not contain, so they can never
    /// collide with an agent worktree named after an id.
    /// </summary>
    public static string InternalName(string workItemId, string label) => $"@{label}-{workItemId}";

    private async Task<StepResult> BuildAndTestAsync(string workItemId, string label, string commit, StringBuilder log, CancellationToken ct)
    {
        var path = Path.Combine(worktreeRoot, InternalName(workItemId, label));
        if (Directory.Exists(path))
        {
            await git.RemoveWorktreeAsync(path, ct).ConfigureAwait(false);
        }

        await git.AddDetachedWorktreeAsync(path, commit, ct).ConfigureAwait(false);
        try
        {
            log.AppendLine($"== {label} ({commit[..Math.Min(12, commit.Length)]}): {config.Commands.Build}");
            var build = await ShellRunner.RunAsync(config.Commands.Build, path, CommandTimeout, CommandEnvironment, ct, config.PassEnvironment).ConfigureAwait(false);
            AppendLog(log, build);
            if (!build.Succeeded)
            {
                return new StepResult(false, false, TrxCounts.None);
            }

            // A fresh results folder that only the harness knows, outside every worktree: only TRX files
            // this run wrote are counted. Console text is never used for counting.
            var results = Path.Combine(worktreeRoot, InternalName(workItemId, label + "-trx-" + Guid.NewGuid().ToString("N")[..8]));
            DeleteFolder(results);
            Directory.CreateDirectory(results);
            try
            {
                var testCommand = TrxTestCounts.WithTrxLogger(config.Commands.Test, results) ?? config.Commands.Test;
                log.AppendLine($"== {label}: {testCommand}");
                var started = DateTime.UtcNow.AddSeconds(-2);
                var test = await ShellRunner.RunAsync(testCommand, path, CommandTimeout, CommandEnvironment, ct, config.PassEnvironment).ConfigureAwait(false);
                AppendLog(log, test);
                var counts = ReadCounts(results, started, log);
                return new StepResult(true, test.Succeeded, counts);
            }
            finally
            {
                DeleteFolder(results);
            }
        }
        finally
        {
            await git.RemoveWorktreeAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static TrxCounts ReadCounts(string results, DateTime started, StringBuilder log)
    {
        try
        {
            var counts = TrxTestCounts.Read(results, started);
            log.AppendLine(counts.Found
                ? $"-> TRX: {counts.Files} file(s), {counts.Total} tests, {counts.Skipped} skipped, {counts.Failed} failed"
                : "-> TRX: no result files, so the tests could not be counted");
            return counts;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            log.AppendLine($"-> TRX could not be read, so the tests could not be counted: {ex.Message}");
            return TrxCounts.None;
        }
    }

    private static void DeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover results folder is harmless: the next run uses a new name.
        }
    }

    private static void AppendLog(StringBuilder log, ProcessResult result)
    {
        var output = result.Output;
        if (output.Length > MaxLogCharsPerStep)
        {
            output = "(earlier output cut)\n" + output[^MaxLogCharsPerStep..];
        }

        log.AppendLine(output);
        log.AppendLine(result.TimedOut ? "-> timed out" : $"-> exit code {result.ExitCode}");
    }

    private sealed record StepResult(bool BuildSucceeded, bool TestsSucceeded, TrxCounts Counts);
}
