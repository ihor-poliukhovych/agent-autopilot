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

    /// <summary>Environment for build and test runs: English output, no build servers that hold files open.</summary>
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
            TestCountBefore: before.Summary.Total,
            TestCountAfter: after.Summary.Total,
            SkipsAdded: Math.Max(0, after.Summary.Skipped - before.Summary.Skipped),
            Log: log.ToString());
        return new GateInput(changes, tests, evidence);
    }

    private async Task<StepResult> BuildAndTestAsync(string workItemId, string label, string commit, StringBuilder log, CancellationToken ct)
    {
        var path = Path.Combine(worktreeRoot, $"{workItemId}-{label}");
        if (Directory.Exists(path))
        {
            await git.RemoveWorktreeAsync(path, ct).ConfigureAwait(false);
        }

        await git.AddDetachedWorktreeAsync(path, commit, ct).ConfigureAwait(false);
        try
        {
            log.AppendLine($"== {label} ({commit[..Math.Min(12, commit.Length)]}): {config.Commands.Build}");
            var build = await ShellRunner.RunAsync(config.Commands.Build, path, CommandTimeout, CommandEnvironment, ct).ConfigureAwait(false);
            AppendLog(log, build);
            if (!build.Succeeded)
            {
                return new StepResult(false, false, TestSummary.None);
            }

            log.AppendLine($"== {label}: {config.Commands.Test}");
            var test = await ShellRunner.RunAsync(config.Commands.Test, path, CommandTimeout, CommandEnvironment, ct).ConfigureAwait(false);
            AppendLog(log, test);
            return new StepResult(true, test.Succeeded, TestSummaryParser.Parse(test.Output));
        }
        finally
        {
            await git.RemoveWorktreeAsync(path, CancellationToken.None).ConfigureAwait(false);
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

    private sealed record StepResult(bool BuildSucceeded, bool TestsSucceeded, TestSummary Summary);
}
