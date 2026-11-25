using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Git;
using Autopilot.Core.Local;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Process;

namespace Autopilot.Core.Evals;

/// <summary>What the eval runner needs. The CLI builds the real pipeline per run; tests pass fakes.</summary>
public sealed record EvalRunnerOptions
{
    /// <summary>The project whose cases are run. Its files are copied; it is never changed.</summary>
    public required string ProjectRoot { get; init; }

    /// <summary>The project's config; the base branch, the test command and the role models come from here.</summary>
    public required AutopilotConfig Config { get; init; }

    /// <summary>Builds the pipeline for one temporary repository (project root, worktree root).</summary>
    public required Func<string, string, CancellationToken, Task<AutopilotPipeline>> CreatePipeline { get; init; }

    /// <summary>Where per-run files (agent logs, hidden test logs) are copied: the report folder.</summary>
    public required string OutputFolder { get; init; }

    /// <summary>Parent of the temporary repositories. Default: a new folder under the system temp folder.</summary>
    public string? TempRoot { get; init; }

    /// <summary>Keep the temporary repositories instead of deleting them after each run.</summary>
    public bool Keep { get; init; }

    public string ToolVersion { get; init; } = "unknown";

    /// <summary>Time limit for the hidden test command.</summary>
    public TimeSpan TestTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Progress lines for a person. Null for silence.</summary>
    public TextWriter? Log { get; init; }
}

/// <summary>
/// Runs eval cases as <c>docs/EVALS.md</c> describes: every run starts from the same pinned snapshot of
/// the project (without <c>.autopilot/evals/</c>) in a fresh temporary git repository, runs triage and,
/// when agent-ready, implement, and grades the result in code.
/// </summary>
public sealed class EvalRunner(EvalRunnerOptions options)
{
    public const string HiddenWorktreeName = "@eval-hidden";

    private static readonly JsonSerializerOptions RunLogOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<EvalResult> RunAsync(IReadOnlyList<EvalCase> cases, int runs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentOutOfRangeException.ThrowIfLessThan(runs, 1);

        var started = DateTimeOffset.Now;
        var total = Stopwatch.StartNew();
        var sessionRoot = options.TempRoot is not null ? Path.GetFullPath(options.TempRoot) : Path.Combine(Path.GetTempPath(), "agent-autopilot-evals", started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(sessionRoot);
        Directory.CreateDirectory(options.OutputFolder);

        var snapshot = await EvalSnapshot.CreateAsync(options.ProjectRoot, Path.Combine(sessionRoot, "snapshot"), ct).ConfigureAwait(false);
        Write($"Snapshot: {snapshot.Files.Count} files ({snapshot.Source}), sha256 {snapshot.Hash[..12]}");

        var results = new List<EvalCaseResult>();
        var models = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string? stoppedEarly = null;
        string snapshotCommit = string.Empty;
        try
        {
            foreach (var evalCase in cases)
            {
                var caseRuns = new List<EvalRunResult>();
                results.Add(new EvalCaseResult(evalCase, runs, caseRuns));
                if (stoppedEarly is not null)
                {
                    continue;
                }

                for (var run = 1; run <= runs; run++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (result, commit) = await RunOnceAsync(evalCase, run, runs, snapshot, sessionRoot, models, ct).ConfigureAwait(false);
                    snapshotCommit = commit ?? snapshotCommit;
                    caseRuns.Add(result);
                    if (result.InfrastructureError is not null)
                    {
                        stoppedEarly = $"{evalCase.Id} run {run}: {result.InfrastructureError}";
                        Write($"Stopping the eval early: {result.InfrastructureError}");
                        break;
                    }
                }
            }
        }
        finally
        {
            if (!options.Keep)
            {
                await DeleteFolderAsync(sessionRoot).ConfigureAwait(false);
            }
        }

        return new EvalResult(
            started,
            total.Elapsed,
            options.ToolVersion,
            snapshot.Hash,
            snapshot.Source,
            snapshot.Files.Count,
            snapshotCommit,
            runs,
            ModelNames(models),
            results,
            stoppedEarly);
    }

    private async Task<(EvalRunResult Result, string? Commit)> RunOnceAsync(
        EvalCase evalCase, int run, int runs, EvalSnapshot snapshot, string sessionRoot, Dictionary<string, HashSet<string>> models, CancellationToken ct)
    {
        var runRoot = Path.Combine(sessionRoot, $"{evalCase.Id}-run{run}");
        var repo = Path.Combine(runRoot, "repo");
        var worktrees = Path.Combine(runRoot, "worktrees");
        var artefacts = Path.Combine(options.OutputFolder, evalCase.Id, $"run-{run}");
        var clock = Stopwatch.StartNew();
        string? commit = null;
        string? triageLabel = null;
        var outcome = EvalOutcome.Failed;
        var message = string.Empty;
        bool? hiddenPassed = null;
        string? hiddenProblem = null;
        string? infrastructure = null;
        var errors = new List<string>();

        try
        {
            commit = await snapshot.CreateRepositoryAsync(repo, options.Config.BaseBranch, ct).ConfigureAwait(false);
            var pipeline = await options.CreatePipeline(repo, worktrees, ct).ConfigureAwait(false);

            var triage = (await pipeline.TriageAsync(evalCase.WorkItem, ct).ConfigureAwait(false)).FirstOrDefault();
            if (triage?.Triage is null)
            {
                message = triage?.Error ?? "Triage returned nothing.";
                errors.Add(message);
            }
            else if (triage.Triage.Label == TriageLabel.NeedsHuman)
            {
                triageLabel = TriageStore.Label(triage.Triage.Label);
                outcome = EvalOutcome.NeedsHuman;
                message = triage.Triage.Reason;
            }
            else
            {
                triageLabel = TriageStore.Label(triage.Triage.Label);
                var implement = await pipeline.ImplementAsync(evalCase.WorkItem, ct).ConfigureAwait(false);
                message = implement.Message;
                outcome = implement.Outcome switch
                {
                    ImplementOutcome.Published => EvalOutcome.Published,
                    ImplementOutcome.Rejected => EvalOutcome.Rejected,
                    _ => EvalOutcome.Failed,
                };
                if (outcome == EvalOutcome.Failed)
                {
                    errors.Add(implement.Message);
                }

                if (outcome == EvalOutcome.Published && implement.BranchName is not null)
                {
                    (hiddenPassed, hiddenProblem) = await RunHiddenTestsAsync(evalCase, repo, worktrees, implement.BranchName, artefacts, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The harness itself broke (git, the file system, the pipeline setup): not the agent's fault.
            outcome = EvalOutcome.Failed;
            message = $"The run stopped with an error: {ex.Message}";
            infrastructure = message;
        }

        var usage = ReadUsage(repo, evalCase.WorkItem, errors);
        foreach (var role in usage)
        {
            if (!models.TryGetValue(role.Role, out var set))
            {
                models[role.Role] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.UnionWith(role.Models);
        }

        if (outcome == EvalOutcome.Failed)
        {
            infrastructure ??= errors.Select(InfrastructureErrors.Classify).FirstOrDefault(r => r is not null);
        }

        clock.Stop();
        var reasons = Grade(evalCase, triageLabel, outcome, message, hiddenPassed, hiddenProblem);
        var passed = reasons.Count == 0 && infrastructure is null;
        if (infrastructure is not null && !reasons.Contains(infrastructure))
        {
            reasons.Insert(0, "Infrastructure error: " + infrastructure);
        }

        CopyArtefacts(repo, artefacts);
        string? kept = null;
        if (options.Keep)
        {
            kept = runRoot;
        }
        else
        {
            await DeleteRunAsync(repo, runRoot).ConfigureAwait(false);
        }

        var result = new EvalRunResult(
            run,
            passed,
            triageLabel,
            outcome,
            reasons,
            message,
            usage.Sum(u => u.Turns),
            usage.Aggregate(TokenUsage.Zero, (sum, u) => sum.Add(u.Usage)),
            usage,
            clock.Elapsed,
            hiddenPassed,
            infrastructure,
            kept);
        Write($"{evalCase.Id} run {run}/{runs}: triage {triageLabel ?? "none"}, {EvalCaseLoader.OutcomeName(outcome)}, {(passed ? "PASS" : "FAIL")} ({EvalReport.Duration(clock.Elapsed)})");
        return (result, commit);
    }

    /// <summary>The grading rules of docs/EVALS.md. Returns every reason the run failed; empty means passed.</summary>
    public static List<string> Grade(EvalCase evalCase, string? triageLabel, EvalOutcome outcome, string message, bool? hiddenPassed, string? hiddenProblem)
    {
        ArgumentNullException.ThrowIfNull(evalCase);
        var reasons = new List<string>();
        var expectedLabel = TriageStore.Label(evalCase.Expect.Triage);
        if (!string.Equals(triageLabel, expectedLabel, StringComparison.Ordinal))
        {
            reasons.Add(triageLabel is null
                ? $"Triage gave no label ({Shorten(message)}); expected {expectedLabel}."
                : $"Triage said {triageLabel}; expected {expectedLabel}.");
        }

        if (!evalCase.Expect.Outcomes.Contains(outcome))
        {
            var expected = string.Join(" or ", evalCase.Expect.Outcomes.Select(EvalCaseLoader.OutcomeName));
            reasons.Add($"Outcome was {EvalCaseLoader.OutcomeName(outcome)} ({Shorten(message)}); expected {expected}.");
        }

        if (outcome == EvalOutcome.Published && hiddenPassed != true)
        {
            reasons.Add(hiddenProblem ?? "The hidden tests did not pass on the agent's branch.");
        }

        return reasons;
    }

    /// <summary>
    /// Checks the agent branch out into a fresh worktree (never the agent's), copies the case's hidden
    /// files over it at the same relative paths and runs the project's test command in the same clean
    /// environment the gate uses.
    /// </summary>
    private async Task<(bool Passed, string? Problem)> RunHiddenTestsAsync(
        EvalCase evalCase, string repo, string worktrees, string branch, string artefacts, CancellationToken ct)
    {
        var git = new GitClient(repo);
        var path = Path.Combine(worktrees, HiddenWorktreeName);
        if (Directory.Exists(path))
        {
            await git.RemoveWorktreeAsync(path, ct).ConfigureAwait(false);
        }

        await git.AddDetachedWorktreeAsync(path, branch, ct).ConfigureAwait(false);
        try
        {
            var copied = CopyHidden(evalCase.HiddenFolder, path);
            var command = options.Config.Commands.Test;
            var result = await ShellRunner.RunAsync(command, path, options.TestTimeout, GateFactsCollector.CommandEnvironment, ct, options.Config.PassEnvironment).ConfigureAwait(false);
            Directory.CreateDirectory(artefacts);
            await File.WriteAllTextAsync(
                Path.Combine(artefacts, "hidden-tests.log"),
                $"== {copied} hidden file(s) copied onto {branch}\n== {command}\n{result.Output}\n-> {(result.TimedOut ? "timed out" : "exit code " + result.ExitCode.ToString(CultureInfo.InvariantCulture))}\n",
                ct).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return (true, null);
            }

            return (false, result.TimedOut
                ? $"The hidden tests timed out after {EvalReport.Duration(options.TestTimeout)}."
                : $"The hidden tests failed on the agent's branch (exit code {result.ExitCode}); see {Path.GetRelativePath(options.OutputFolder, artefacts).Replace('\\', '/')}/hidden-tests.log.");
        }
        finally
        {
            await git.RemoveWorktreeAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Copies every file under <paramref name="hiddenFolder"/> to the same relative path under <paramref name="target"/>.</summary>
    public static int CopyHidden(string hiddenFolder, string target)
    {
        if (!Directory.Exists(hiddenFolder))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(hiddenFolder, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(hiddenFolder, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>Sums turns and tokens per role from the run's runs.jsonl. Agent errors are added to <paramref name="errors"/>.</summary>
    public static IReadOnlyList<EvalRoleUsage> ReadUsage(string repo, string workItemId, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var file = Path.Combine(ProjectPaths.RunFolder(repo, workItemId), "runs.jsonl");
        if (!File.Exists(file))
        {
            return [];
        }

        var byRole = new Dictionary<string, (int Runs, int Turns, TokenUsage Usage, HashSet<string> Models)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            RunLogLine? entry;
            try
            {
                entry = JsonSerializer.Deserialize<RunLogLine>(line, RunLogOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry?.Role is null)
            {
                continue;
            }

            if (!byRole.TryGetValue(entry.Role, out var sum))
            {
                sum = (0, 0, TokenUsage.Zero, new HashSet<string>(StringComparer.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(entry.Model))
            {
                sum.Models.Add(entry.Model);
            }

            if (!string.IsNullOrWhiteSpace(entry.Error))
            {
                errors.Add(entry.Error);
            }

            byRole[entry.Role] = (sum.Runs + 1, sum.Turns + entry.Turns, sum.Usage.Add(entry.Usage ?? TokenUsage.Zero), sum.Models);
        }

        string[] order = ["triage", "implementer", "reviewer"];
        return byRole
            .OrderBy(p => RoleOrder(order, p.Key))
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new EvalRoleUsage(p.Key, p.Value.Runs, p.Value.Turns, p.Value.Usage, p.Value.Models.Order(StringComparer.Ordinal).ToList()))
            .ToList();
    }

    private static int RoleOrder(string[] order, string role)
    {
        var index = Array.IndexOf(order, role);
        return index < 0 ? int.MaxValue : index;
    }

    private IReadOnlyDictionary<string, string> ModelNames(Dictionary<string, HashSet<string>> seen)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var key = role.ToString().ToLowerInvariant();
            if (seen.TryGetValue(key, out var models) && models.Count > 0)
            {
                names[key] = string.Join(", ", models.Order(StringComparer.Ordinal));
            }
            else
            {
                names[key] = options.Config.Roles?.For(role)?.Model ?? "default";
            }
        }

        return names;
    }

    private void CopyArtefacts(string repo, string artefacts)
    {
        var outFolder = ProjectPaths.OutFolder(repo);
        if (!Directory.Exists(outFolder))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(outFolder, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(artefacts, Path.GetRelativePath(outFolder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Write($"Could not copy the run files to {artefacts}: {ex.Message}");
        }
    }

    private async Task DeleteRunAsync(string repo, string runRoot)
    {
        try
        {
            // Worktrees live under the run folder, so deleting the folder removes them with the repository.
            await DeleteFolderAsync(runRoot).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Write($"Could not delete {runRoot} ({ex.Message}); delete it by hand. Repository: {repo}");
        }
    }

    internal static async Task DeleteFolderAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                await Task.Delay(200 * attempt).ConfigureAwait(false);
            }
        }
    }

    private static string Shorten(string text)
    {
        var line = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return line.Length <= 200 ? line : line[..200] + "...";
    }

    private void Write(string line) => options.Log?.WriteLine(line);

    private sealed record RunLogLine(string? Role, int Turns, TokenUsage? Usage, string? Model, string? Error);
}
