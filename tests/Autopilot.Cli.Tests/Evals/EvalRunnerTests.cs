using System.Text.Json;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Evals;

namespace Autopilot.Cli.Tests.Evals;

public sealed class EvalRunnerTests
{
    private static async Task<(EvalResult Result, string Output, string TempRoot)> RunAsync(
        TestRepo repo, FakeAgentRunner runner, int runs, bool keep = false, string? onlyCase = null)
    {
        var set = await EvalTestProject.LoadCasesAsync(repo.Root);
        var output = Path.Combine(repo.WorktreeRoot, "..", "eval-out");
        var tempRoot = Path.Combine(repo.WorktreeRoot, "..", "eval-temp");
        var cases = set.Cases.Where(c => onlyCase is null || c.Id == onlyCase).ToList();
        var result = await new EvalRunner(EvalTestProject.Options(repo, runner, output, tempRoot, keep)).RunAsync(cases, runs, CancellationToken.None);
        return (result, Path.GetFullPath(output), Path.GetFullPath(tempRoot));
    }

    [Fact]
    public async Task Every_outcome_is_graded_and_the_agent_never_sees_the_evals_folder()
    {
        using var repo = await EvalTestProject.CreateAsync();
        var seen = new List<(string Folder, bool Exists, bool HasEvals, bool HasTasks)>();
        var runner = EvalTestProject.ScriptedRunner(request =>
        {
            var folder = request.WorkingDirectory;
            seen.Add((Path.GetFullPath(folder), Directory.Exists(folder), Directory.Exists(Path.Combine(folder, ".autopilot", "evals")),
                File.Exists(Path.Combine(folder, ".autopilot", "tasks", "001-add.md"))));
            return null;
        });

        var (result, output, tempRoot) = await RunAsync(repo, runner, runs: 2);

        // Every agent run, triage included, worked in a checkout without .autopilot/evals.
        Assert.Equal(runner.Requests.Count, seen.Count);
        Assert.NotEmpty(seen);
        Assert.All(seen, s =>
        {
            Assert.True(s.Exists, s.Folder);
            Assert.False(s.HasEvals, s.Folder);
            Assert.True(s.HasTasks, s.Folder);
            Assert.StartsWith(tempRoot, s.Folder, StringComparison.OrdinalIgnoreCase);
        });

        var published = result.Cases.Single(c => c.Case.Id == "e01-add");
        Assert.All(published.Runs, r =>
        {
            Assert.True(r.Passed, string.Join("; ", r.Reasons));
            Assert.Equal(EvalOutcome.Published, r.Outcome);
            Assert.Equal("agent-ready", r.TriageLabel);
            Assert.True(r.HiddenTestsPassed);
        });
        Assert.Contains("Add(2, 3) == 5", File.ReadAllText(Path.Combine(output, "e01-add", "run-1", "hidden-tests.log")));

        var needsHuman = result.Cases.Single(c => c.Case.Id == "e02-ask");
        Assert.All(needsHuman.Runs, r =>
        {
            Assert.True(r.Passed);
            Assert.Equal(EvalOutcome.NeedsHuman, r.Outcome);
            Assert.Null(r.HiddenTestsPassed);
        });

        var rejected = result.Cases.Single(c => c.Case.Id == "e03-trap");
        Assert.All(rejected.Runs, r => Assert.Equal(EvalOutcome.Rejected, r.Outcome));
        Assert.True(rejected.PassAll);
        Assert.True(File.Exists(Path.Combine(output, "e03-trap", "run-1", "runs", "003", "REJECTED.md")));

        // Triage, implementer and reviewer each report 3 turns and 50 output tokens per call.
        var run = published.Runs[0];
        Assert.Equal(9, run.Turns);
        Assert.Equal(150, run.Usage.OutputTokens);
        Assert.Equal(["triage", "implementer", "reviewer"], run.UsageByRole.Select(u => u.Role));
        Assert.Equal("test-model", result.Models["implementer"]);

        Assert.Equal(1.0, result.PassAt1);
        Assert.Equal(3, result.CasesAllPassed);
        Assert.Null(result.StoppedEarly);
        Assert.Equal(64, result.SnapshotHash.Length);
        Assert.Equal("git tracked files", result.SnapshotSource);

        // Temporary repositories are gone; the source project is untouched.
        Assert.False(Directory.Exists(tempRoot));
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".autopilot", "out", "runs")));
        Assert.Equal(string.Empty, (await repo.GitAsync("status", "--porcelain")).Trim());
    }

    [Fact]
    public async Task Published_change_fails_when_the_hidden_tests_fail()
    {
        using var repo = await EvalTestProject.CreateAsync(new Dictionary<string, string>
        {
            [".autopilot/evals/e04-wrong/case.json"] = """{ "workItem": "001", "expect": { "triage": "agent-ready", "outcome": ["published"] } }""",
            [".autopilot/evals/e04-wrong/hidden/tests/other.txt"] = "not the acceptance file\n",
        });

        var (result, _, _) = await RunAsync(repo, EvalTestProject.ScriptedRunner(), runs: 1, onlyCase: "e04-wrong");

        var run = Assert.Single(Assert.Single(result.Cases).Runs);
        Assert.False(run.Passed);
        Assert.Equal(EvalOutcome.Published, run.Outcome);
        Assert.False(run.HiddenTestsPassed);
        Assert.Contains(run.Reasons, r => r.Contains("hidden tests failed", StringComparison.Ordinal));
        Assert.Equal(0.0, result.PassAt1);
        Assert.False(result.Cases[0].PassAll);
    }

    [Fact]
    public async Task Wrong_triage_and_failed_agent_runs_are_graded_as_failures()
    {
        using var repo = await EvalTestProject.CreateAsync();
        var runner = EvalTestProject.ScriptedRunner(request => request.Role switch
        {
            AgentRole.Triage when request.Prompt.Contains("Rounding", StringComparison.Ordinal) =>
                FakeAgentRunner.Answer(new { label = "agent-ready", reason = "Looks easy.", acceptanceCriteria = new[] { "x" }, risk = "low" }),
            AgentRole.Implementer => new AgentRunResult(false, string.Empty, null, 7, new TokenUsage(1, 2, 3, 4), TimeSpan.FromSeconds(1), "test-model", "the build broke"),
            _ => null,
        });

        var (result, _, _) = await RunAsync(repo, runner, runs: 1);

        var ask = Assert.Single(result.Cases.Single(c => c.Case.Id == "e02-ask").Runs);
        Assert.False(ask.Passed);
        Assert.Contains(ask.Reasons, r => r.Contains("Triage said agent-ready; expected needs-human.", StringComparison.Ordinal));

        var add = Assert.Single(result.Cases.Single(c => c.Case.Id == "e01-add").Runs);
        Assert.False(add.Passed);
        Assert.Equal(EvalOutcome.Failed, add.Outcome);
        Assert.Null(add.InfrastructureError);
        Assert.Contains(add.Reasons, r => r.Contains("Outcome was failed", StringComparison.Ordinal) && r.Contains("expected published", StringComparison.Ordinal));
        Assert.Equal(10, add.Turns);
        Assert.Null(result.StoppedEarly);
    }

    [Fact]
    public async Task Stops_early_on_an_infrastructure_error()
    {
        using var repo = await EvalTestProject.CreateAsync();
        var runner = EvalTestProject.ScriptedRunner(_ => new AgentRunResult(
            false, string.Empty, null, 0, TokenUsage.Zero, TimeSpan.Zero, null, "Claude Code reported an error (error_during_execution): Not logged in · Please run /login"));

        var (result, _, tempRoot) = await RunAsync(repo, runner, runs: 3);

        Assert.Single(runner.Requests);
        Assert.NotNull(result.StoppedEarly);
        Assert.Contains("not signed in", result.StoppedEarly, StringComparison.Ordinal);
        var first = result.Cases[0];
        var run = Assert.Single(first.Runs);
        Assert.NotNull(run.InfrastructureError);
        Assert.False(run.Passed);
        Assert.Null(first.PassAll);
        Assert.Null(first.PassRate);
        Assert.All(result.Cases.Skip(1), c => Assert.Empty(c.Runs));
        Assert.False(Directory.Exists(tempRoot));
    }

    [Fact]
    public async Task Keep_leaves_the_temporary_repository()
    {
        using var repo = await EvalTestProject.CreateAsync();

        var (result, _, tempRoot) = await RunAsync(repo, EvalTestProject.ScriptedRunner(), runs: 1, keep: true, onlyCase: "e02-ask");

        var run = Assert.Single(result.Cases[0].Runs);
        Assert.NotNull(run.KeptFolder);
        Assert.StartsWith(tempRoot, run.KeptFolder, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(run.KeptFolder, "repo", ".git")));
        Assert.False(Directory.Exists(Path.Combine(run.KeptFolder, "repo", ".autopilot", "evals")));
    }

    [Fact]
    public async Task Snapshot_excludes_evals_and_out_and_its_hash_is_stable()
    {
        using var repo = await EvalTestProject.CreateAsync();
        repo.Write(".autopilot/out/runs/x/runs.jsonl", "{}\n");
        using var staging = new TempFolder();

        var first = await EvalSnapshot.CreateAsync(repo.Root, staging.Combine("a"), CancellationToken.None);
        var second = await EvalSnapshot.CreateAsync(repo.Root, staging.Combine("b"), CancellationToken.None);

        Assert.Equal(first.Hash, second.Hash);
        Assert.Contains(".autopilot/autopilot.json", first.Files);
        Assert.Contains("src/Calc.cs", first.Files);
        Assert.DoesNotContain(first.Files, f => f.StartsWith(".autopilot/evals/", StringComparison.Ordinal));
        Assert.DoesNotContain(first.Files, f => f.StartsWith(".autopilot/out/", StringComparison.Ordinal));
        Assert.False(Directory.Exists(staging.Combine("a", ".autopilot", "evals")));

        // The same files give the same commit, so every run starts from one pinned commit.
        var commitA = await first.CreateRepositoryAsync(staging.Combine("repo-a"), "main", CancellationToken.None);
        var commitB = await second.CreateRepositoryAsync(staging.Combine("repo-b"), "main", CancellationToken.None);
        Assert.Equal(commitA, commitB);

        File.AppendAllText(Path.Combine(repo.Root, "src", "Calc.cs"), "// changed\n");
        var changed = await EvalSnapshot.CreateAsync(repo.Root, staging.Combine("c"), CancellationToken.None);
        Assert.NotEqual(first.Hash, changed.Hash);
    }

    [Fact]
    public async Task Snapshot_of_a_plain_folder_skips_bin_obj_and_git()
    {
        using var folder = new TempFolder();
        folder.Write("src/A.cs", "a");
        folder.Write("src/bin/A.dll", "x");
        folder.Write("src/obj/A.json", "x");
        folder.Write(".autopilot/evals/e1/case.json", "{}");
        folder.Write(".autopilot/out/x.txt", "x");
        folder.Write(".autopilot/tasks/001-a.md", "# A");
        using var staging = new TempFolder();

        var snapshot = await EvalSnapshot.CreateAsync(folder.Path, staging.Combine("s"), CancellationToken.None);

        Assert.Equal("folder", snapshot.Source);
        Assert.Equal([".autopilot/tasks/001-a.md", "src/A.cs"], snapshot.Files);
    }

    [Fact]
    public void Hidden_files_are_copied_to_the_same_relative_paths()
    {
        using var temp = new TempFolder();
        temp.Write("hidden/tests/Extra/HiddenTests.cs", "hidden");
        temp.Write("checkout/tests/Extra/HiddenTests.cs", "old");

        var count = EvalRunner.CopyHidden(temp.Combine("hidden"), temp.Combine("checkout"));

        Assert.Equal(1, count);
        Assert.Equal("hidden", File.ReadAllText(temp.Combine("checkout", "tests", "Extra", "HiddenTests.cs")));
        Assert.Equal(0, EvalRunner.CopyHidden(temp.Combine("missing"), temp.Combine("checkout")));
    }

    [Theory]
    [InlineData("Claude Code reported an error: Not logged in · Please run /login", true)]
    [InlineData("Claude AI usage limit reached|1764000000", true)]
    [InlineData("You've hit your limit · resets 3pm", true)]
    [InlineData("Could not start 'claude': The system cannot find the file specified.", true)]
    [InlineData("tool call limit 40 reached", false)]
    [InlineData("The merge gate rejected the change.", false)]
    [InlineData(null, false)]
    public void Infrastructure_errors_are_recognised(string? message, bool infrastructure) =>
        Assert.Equal(infrastructure, InfrastructureErrors.Classify(message) is not null);

    [Fact]
    public void Usage_is_summed_per_role_from_runs_jsonl()
    {
        using var temp = new TempFolder();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string Line(string role, int turns, long output, string? error = null) =>
            JsonSerializer.Serialize(new { run = 1, role, succeeded = error is null, turns, usage = new TokenUsage(10, output, 1, 2), durationMs = 5, model = "m-" + role, error }, options);
        temp.Write(".autopilot/out/runs/001/runs.jsonl", string.Join('\n', Line("triage", 2, 5), Line("implementer", 10, 100), Line("reviewer", 3, 7), Line("implementer", 4, 40, "oops"), "not json") + "\n");
        var errors = new List<string>();

        var usage = EvalRunner.ReadUsage(temp.Path, "001", errors);

        Assert.Equal(["triage", "implementer", "reviewer"], usage.Select(u => u.Role));
        var implementer = usage[1];
        Assert.Equal(2, implementer.AgentRuns);
        Assert.Equal(14, implementer.Turns);
        Assert.Equal(140, implementer.Usage.OutputTokens);
        Assert.Equal(20, implementer.Usage.InputTokens);
        Assert.Equal(["m-implementer"], implementer.Models);
        Assert.Equal(["oops"], errors);
    }
}
