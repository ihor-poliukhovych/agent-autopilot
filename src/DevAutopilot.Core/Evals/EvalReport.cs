using System.Globalization;
using System.Text;
using System.Text.Json;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Local;

namespace DevAutopilot.Core.Evals;

/// <summary>Writes <c>report.md</c> and <c>report.json</c> for an eval and prints a short summary.</summary>
public static class EvalReport
{
    public const string MarkdownFileName = "report.md";
    public const string JsonFileName = "report.json";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Creates <c>&lt;project&gt;/.autopilot/out/evals/&lt;yyyyMMdd-HHmmss&gt;/</c> (with a suffix when it exists).</summary>
    public static string CreateOutputFolder(string projectRoot, DateTimeOffset now, string? dataFolder = null)
    {
        var parent = Path.Combine(ProjectPaths.OutFolder(projectRoot, dataFolder), "evals");
        var name = now.ToString("yyyyMMdd-HHmmss", Invariant);
        var folder = Path.Combine(parent, name);
        for (var i = 2; Directory.Exists(folder); i++)
        {
            folder = Path.Combine(parent, $"{name}-{i}");
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    public static async Task WriteAsync(EvalResult result, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, MarkdownFileName), Markdown(result), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(folder, JsonFileName), Json(result), ct).ConfigureAwait(false);
    }

    public static string Markdown(EvalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var n = result.RunsPerCase;
        var text = new StringBuilder();
        text.AppendLine("# Eval report");
        text.AppendLine();
        text.AppendLine($"- Date: {result.StartedAt.ToString("yyyy-MM-dd HH:mm", Invariant)} (UTC{result.StartedAt.ToString("zzz", Invariant)})");
        text.AppendLine($"- Tool version: {result.ToolVersion}");
        text.AppendLine($"- Snapshot: `{Short(result.SnapshotHash)}` (SHA-256 of {Count(result.SnapshotFiles)} files from {result.SnapshotSource}{(result.SnapshotCommit.Length > 0 ? $", commit `{Short(result.SnapshotCommit)}`" : string.Empty)})");
        text.AppendLine($"- Models: {string.Join(", ", result.Models.Select(m => $"{m.Key} `{m.Value}`"))}");
        text.AppendLine($"- Runs per case: {n}");
        text.AppendLine($"- Wall time: {Duration(result.WallTime)}");
        if (result.StoppedEarly is not null)
        {
            text.AppendLine();
            text.AppendLine($"**Stopped early** because of an infrastructure error, so some runs did not happen: {result.StoppedEarly}");
        }

        text.AppendLine();
        text.AppendLine("## Summary");
        text.AppendLine();
        text.AppendLine($"| Case | Passes | Pass rate | pass^{n} | Mean turns | Mean output tokens | Mean time |");
        text.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var c in result.Cases)
        {
            text.AppendLine($"| {c.Case.Id} | {c.Passes}/{c.PlannedRuns}{NotGraded(c)} | {Percent(c.PassRate)} | {YesNo(c.PassAll)} | {Number(c.MeanTurns, 1)} | {Number(c.MeanOutputTokens, 0)} | {Seconds(c.MeanWallSeconds)} |");
        }

        text.AppendLine();
        text.AppendLine("## Totals");
        text.AppendLine();
        var usage = result.TotalUsage;
        text.AppendLine("| Measure | Value |");
        text.AppendLine("|---|---|");
        text.AppendLine($"| pass@1 (mean pass rate over cases) | {Percent(result.PassAt1)} |");
        text.AppendLine($"| pass^{n} over cases | {result.CasesAllPassed} of {result.Cases.Count} cases ({Percent(result.PassAllRate)}) |");
        text.AppendLine($"| Runs | {Count(result.TotalRuns)} |");
        text.AppendLine($"| Turns | {Count(result.TotalTurns)} |");
        text.AppendLine($"| Input tokens | {Count(usage.InputTokens)} |");
        text.AppendLine($"| Output tokens | {Count(usage.OutputTokens)} |");
        text.AppendLine($"| Cache read tokens | {Count(usage.CacheReadInputTokens)} |");
        text.AppendLine($"| Cache write tokens | {Count(usage.CacheCreationInputTokens)} |");
        text.AppendLine();

        text.AppendLine("## Mean tokens per run");
        text.AppendLine();
        text.AppendLine("| Case | Input | Output | Cache read | Cache write |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var c in result.Cases)
        {
            text.AppendLine($"| {c.Case.Id} | {Number(c.MeanInputTokens, 0)} | {Number(c.MeanOutputTokens, 0)} | {Number(c.MeanCacheReadTokens, 0)} | {Number(c.MeanCacheWriteTokens, 0)} |");
        }

        text.AppendLine();
        text.AppendLine("## Failures");
        text.AppendLine();
        var failures = result.Cases.SelectMany(c => c.Runs.Where(r => !r.Passed).Select(r => (Case: c, Run: r))).ToList();
        if (failures.Count == 0)
        {
            text.AppendLine("None.");
        }

        foreach (var (c, run) in failures)
        {
            text.AppendLine($"### {c.Case.Id}, run {run.Run}");
            text.AppendLine();
            text.AppendLine($"Expected triage {(c.Case.Expect.Triage is { } t ? TriageStore.Label(t) : "any")} and {string.Join(" or ", c.Case.Expect.Outcomes.Select(EvalCaseLoader.OutcomeName))}; got triage {run.TriageLabel ?? "none"} and {EvalCaseLoader.OutcomeName(run.Outcome)}.");
            text.AppendLine();
            foreach (var reason in run.Reasons)
            {
                text.AppendLine($"- {reason}");
            }

            text.AppendLine();
            text.AppendLine($"Agent logs and gate files: `{c.Case.Id}/run-{run.Run}/` next to this report.");
            text.AppendLine();
        }

        var notRun = result.Cases.Where(c => c.Runs.Count < c.PlannedRuns).ToList();
        if (notRun.Count > 0)
        {
            text.AppendLine("## Not run");
            text.AppendLine();
            foreach (var c in notRun)
            {
                text.AppendLine($"- {c.Case.Id}: {c.Runs.Count} of {c.PlannedRuns} runs happened before the eval stopped.");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    public static string Json(EvalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var report = new
        {
            date = result.StartedAt,
            toolVersion = result.ToolVersion,
            snapshot = new { hash = result.SnapshotHash, source = result.SnapshotSource, files = result.SnapshotFiles, commit = result.SnapshotCommit },
            models = result.Models,
            runsPerCase = result.RunsPerCase,
            wallSeconds = Round(result.WallTime.TotalSeconds),
            stoppedEarly = result.StoppedEarly,
            totals = new
            {
                cases = result.Cases.Count,
                runs = result.TotalRuns,
                passAt1 = Round(result.PassAt1),
                passAll = Round(result.PassAllRate),
                casesAllPassed = result.CasesAllPassed,
                turns = result.TotalTurns,
                usage = result.TotalUsage,
            },
            cases = result.Cases.Select(c => new
            {
                id = c.Case.Id,
                workItem = c.Case.WorkItem,
                description = c.Case.Description,
                expect = new
                {
                    triage = c.Case.Expect.Triage is { } t ? TriageStore.Label(t) : "any",
                    outcome = c.Case.Expect.Outcomes.Select(EvalCaseLoader.OutcomeName).ToList(),
                },
                plannedRuns = c.PlannedRuns,
                gradedRuns = c.GradedRuns.Count,
                passes = c.Passes,
                passRate = Round(c.PassRate),
                passAll = c.PassAll,
                meanTurns = Round(c.MeanTurns),
                meanUsage = new
                {
                    inputTokens = Round(c.MeanInputTokens),
                    outputTokens = Round(c.MeanOutputTokens),
                    cacheReadInputTokens = Round(c.MeanCacheReadTokens),
                    cacheCreationInputTokens = Round(c.MeanCacheWriteTokens),
                },
                meanWallSeconds = Round(c.MeanWallSeconds),
                runs = c.Runs.Select(r => new
                {
                    run = r.Run,
                    passed = r.Passed,
                    triage = r.TriageLabel,
                    outcome = EvalCaseLoader.OutcomeName(r.Outcome),
                    reasons = r.Reasons,
                    message = r.Message,
                    hiddenTestsPassed = r.HiddenTestsPassed,
                    turns = r.Turns,
                    usage = r.Usage,
                    usageByRole = r.UsageByRole.Select(u => new { role = u.Role, agentRuns = u.AgentRuns, turns = u.Turns, usage = u.Usage, models = u.Models }),
                    wallSeconds = Round(r.WallTime.TotalSeconds),
                    infrastructureError = r.InfrastructureError,
                    keptFolder = r.KeptFolder,
                }),
            }),
        };
        return JsonSerializer.Serialize(report, ConfigLoader.JsonOptions);
    }

    /// <summary>A few lines for the console.</summary>
    public static string ConsoleSummary(EvalResult result, string folder)
    {
        ArgumentNullException.ThrowIfNull(result);
        var n = result.RunsPerCase;
        var width = result.Cases.Select(c => c.Case.Id.Length).DefaultIfEmpty(4).Max();
        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine($"Eval: {result.Cases.Count} case(s), {n} run(s) each, snapshot {Short(result.SnapshotHash)}");
        foreach (var c in result.Cases)
        {
            text.AppendLine($"  {c.Case.Id.PadRight(width)}  {c.Passes}/{c.PlannedRuns}  {Percent(c.PassRate),4}  pass^{n} {YesNo(c.PassAll)}");
        }

        text.AppendLine($"pass@1 {Percent(result.PassAt1)}, pass^{n} {result.CasesAllPassed} of {result.Cases.Count} case(s), {Count(result.TotalTurns)} turns, {Count(result.TotalUsage.OutputTokens)} output tokens, {Duration(result.WallTime)}.");
        if (result.StoppedEarly is not null)
        {
            text.AppendLine($"Stopped early: {result.StoppedEarly}");
        }

        text.AppendLine($"Report: {Path.Combine(folder, MarkdownFileName)}");
        return text.ToString();
    }

    /// <summary>"4.2 s", "42 s", "3 min 05 s", "1 h 02 min".</summary>
    public static string Duration(TimeSpan time)
    {
        if (time.TotalSeconds < 10)
        {
            return time.TotalSeconds.ToString("0.0", Invariant) + " s";
        }

        if (time.TotalSeconds < 60)
        {
            return Math.Round(time.TotalSeconds).ToString("0", Invariant) + " s";
        }

        var rounded = TimeSpan.FromSeconds(Math.Round(time.TotalSeconds));
        return rounded.TotalHours < 1
            ? $"{(int)rounded.TotalMinutes} min {rounded.Seconds:00} s"
            : $"{(int)rounded.TotalHours} h {rounded.Minutes:00} min";
    }

    public static string Percent(double? value) =>
        value is { } v ? Math.Round(v * 100).ToString("0", Invariant) + "%" : "-";

    private static string Seconds(double? seconds) => seconds is { } s ? Duration(TimeSpan.FromSeconds(s)) : "-";

    private static string Number(double? value, int decimals) =>
        value is { } v ? v.ToString(decimals == 0 ? "N0" : "N" + decimals.ToString(Invariant), Invariant) : "-";

    private static string Count(long value) => value.ToString("N0", Invariant);

    private static string YesNo(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        null => "unknown",
    };

    private static string NotGraded(EvalCaseResult c)
    {
        var missing = c.PlannedRuns - c.GradedRuns.Count;
        return missing > 0 ? $" ({missing} not graded)" : string.Empty;
    }

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 3) : null;
}
