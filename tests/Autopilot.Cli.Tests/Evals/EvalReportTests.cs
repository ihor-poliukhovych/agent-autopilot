using System.Text.Json;
using Autopilot.Core;
using Autopilot.Core.Evals;

namespace Autopilot.Cli.Tests.Evals;

public sealed class EvalReportTests
{
    private static EvalCase Case(string id) =>
        new(id, "001", "desc", new EvalExpectation(TriageLabel.AgentReady, [EvalOutcome.Published]), Path.Combine(Path.GetTempPath(), id));

    private static EvalRunResult Run(int number, bool passed, long outputTokens = 1000, int turns = 10, double seconds = 30, string? infrastructure = null) => new(
        number,
        passed,
        "agent-ready",
        passed ? EvalOutcome.Published : EvalOutcome.Rejected,
        passed ? [] : ["Outcome was rejected (The merge gate rejected the change.); expected published."],
        "message",
        turns,
        new TokenUsage(2000, outputTokens, 30_000, 4000),
        [],
        TimeSpan.FromSeconds(seconds),
        passed ? true : null,
        infrastructure);

    private static EvalResult Result(IReadOnlyList<EvalCaseResult> cases, string? stopped = null) => new(
        new DateTimeOffset(2026, 11, 25, 14, 3, 0, TimeSpan.FromHours(1)),
        TimeSpan.FromSeconds(185),
        "0.1.0",
        new string('a', 64),
        "git tracked files",
        42,
        new string('b', 40),
        3,
        new Dictionary<string, string> { ["triage"] = "default", ["implementer"] = "opus", ["reviewer"] = "default" },
        cases,
        stopped);

    [Fact]
    public void Pass_rate_and_pass_hat_n_follow_the_runs()
    {
        var mixed = new EvalCaseResult(Case("a"), 3, [Run(1, true), Run(2, false), Run(3, true)]);
        var all = new EvalCaseResult(Case("b"), 3, [Run(1, true), Run(2, true), Run(3, true)]);
        var none = new EvalCaseResult(Case("c"), 3, [Run(1, false), Run(2, false), Run(3, false)]);
        var stopped = new EvalCaseResult(Case("d"), 3, [Run(1, true), Run(2, false, infrastructure: "Claude Code is not signed in.")]);

        Assert.Equal(2, mixed.Passes);
        Assert.Equal(2.0 / 3, mixed.PassRate!.Value, 6);
        Assert.False(mixed.PassAll);
        Assert.True(all.PassAll);
        Assert.False(none.PassAll);
        Assert.Equal(0.0, none.PassRate);

        // An infrastructure failure is not graded, and pass^N is unknown with fewer than N graded runs.
        Assert.Single(stopped.GradedRuns);
        Assert.Equal(1.0, stopped.PassRate);
        Assert.Null(stopped.PassAll);

        var result = Result([mixed, all, none, stopped]);
        Assert.Equal((2.0 / 3 + 1 + 0 + 1) / 4, result.PassAt1!.Value, 6);
        Assert.Equal(1.0 / 3, result.PassAllRate!.Value, 6);
        Assert.Equal(1, result.CasesAllPassed);
    }

    [Fact]
    public void Markdown_has_the_summary_totals_and_failures_formatted_for_people()
    {
        var mixed = new EvalCaseResult(Case("e01-add"), 3, [Run(1, true, 12_000, 10, 30), Run(2, false, 13_000, 12, 95), Run(3, true, 14_000, 14, 65)]);
        var markdown = EvalReport.Markdown(Result([mixed]));

        Assert.Contains("| Case | Passes | Pass rate | pass^3 | Mean turns | Mean output tokens | Mean time |", markdown);
        Assert.Contains("| e01-add | 2/3 | 67% | no | 12.0 | 13,000 | 1 min 03 s |", markdown);
        Assert.Contains("| pass@1 (mean pass rate over cases) | 67% |", markdown);
        Assert.Contains("| pass^3 over cases | 0 of 1 cases (0%) |", markdown);
        Assert.Contains("| Output tokens | 39,000 |", markdown);
        Assert.Contains("| Cache read tokens | 90,000 |", markdown);
        Assert.Contains("Snapshot: `aaaaaaaaaaaa` (SHA-256 of 42 files from git tracked files, commit `bbbbbbbbbbbb`)", markdown);
        Assert.Contains("Tool version: 0.1.0", markdown);
        Assert.Contains("implementer `opus`", markdown);
        Assert.Contains("## Failures", markdown);
        Assert.Contains("### e01-add, run 2", markdown);
        Assert.Contains("- Outcome was rejected (The merge gate rejected the change.); expected published.", markdown);
        Assert.Contains("Wall time: 3 min 05 s", markdown);
        Assert.DoesNotContain("Stopped early", markdown);
    }

    [Fact]
    public void Markdown_and_console_say_when_the_eval_stopped_early()
    {
        var first = new EvalCaseResult(Case("e01-add"), 3, [Run(1, false, infrastructure: "Claude Code is not signed in.")]);
        var second = new EvalCaseResult(Case("e02-ask"), 3, []);
        var result = Result([first, second], "e01-add run 1: Claude Code is not signed in.");

        var markdown = EvalReport.Markdown(result);
        var console = EvalReport.ConsoleSummary(result, "C:/out");

        Assert.Contains("**Stopped early**", markdown);
        Assert.Contains("| e01-add | 0/3 (3 not graded) | - | unknown |", markdown);
        Assert.Contains("- e02-ask: 0 of 3 runs happened before the eval stopped.", markdown);
        Assert.Contains("Stopped early: e01-add run 1: Claude Code is not signed in.", console);
        Assert.Contains("report.md", console);
    }

    [Fact]
    public void Json_has_cases_runs_and_totals()
    {
        var mixed = new EvalCaseResult(Case("e01-add"), 3, [Run(1, true), Run(2, false), Run(3, true)]);

        using var json = JsonDocument.Parse(EvalReport.Json(Result([mixed])));
        var root = json.RootElement;

        Assert.Equal("0.1.0", root.GetProperty("toolVersion").GetString());
        Assert.Equal(new string('a', 64), root.GetProperty("snapshot").GetProperty("hash").GetString());
        Assert.Equal(3, root.GetProperty("runsPerCase").GetInt32());
        Assert.Equal(0.667, root.GetProperty("totals").GetProperty("passAt1").GetDouble());
        Assert.Equal(3000, root.GetProperty("totals").GetProperty("usage").GetProperty("outputTokens").GetInt64());
        var c = root.GetProperty("cases")[0];
        Assert.Equal("e01-add", c.GetProperty("id").GetString());
        Assert.Equal(2, c.GetProperty("passes").GetInt32());
        Assert.False(c.GetProperty("passAll").GetBoolean());
        Assert.Equal("published", c.GetProperty("expect").GetProperty("outcome")[0].GetString());
        var run = c.GetProperty("runs")[1];
        Assert.Equal("rejected", run.GetProperty("outcome").GetString());
        Assert.False(run.GetProperty("passed").GetBoolean());
        Assert.Equal(30, run.GetProperty("wallSeconds").GetDouble());
    }

    [Theory]
    [InlineData(4.24, "4.2 s")]
    [InlineData(42.4, "42 s")]
    [InlineData(185, "3 min 05 s")]
    [InlineData(3720, "1 h 02 min")]
    public void Durations_read_well(double seconds, string expected) =>
        Assert.Equal(expected, EvalReport.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Output_folder_is_timestamped_and_never_reused()
    {
        using var temp = new TempFolder();
        var now = new DateTimeOffset(2026, 11, 25, 14, 3, 9, TimeSpan.Zero);

        var first = EvalReport.CreateOutputFolder(temp.Path, now);
        var second = EvalReport.CreateOutputFolder(temp.Path, now);

        Assert.Equal(temp.Combine(".autopilot", "out", "evals", "20261125-140309"), first);
        Assert.Equal(first + "-2", second);
    }

    [Fact]
    public async Task Report_files_are_written()
    {
        using var temp = new TempFolder();
        var result = Result([new EvalCaseResult(Case("e01-add"), 3, [Run(1, true), Run(2, true), Run(3, true)])]);

        await EvalReport.WriteAsync(result, temp.Path, CancellationToken.None);

        Assert.Contains("None.", File.ReadAllText(temp.Combine("report.md")));
        using var json = JsonDocument.Parse(File.ReadAllText(temp.Combine("report.json")));
        Assert.True(json.RootElement.GetProperty("cases")[0].GetProperty("passAll").GetBoolean());
    }
}
