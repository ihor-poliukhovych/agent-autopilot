using DevAutopilot.Core;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Facts;
using DevAutopilot.Core.Gate;
using DevAutopilot.Core.Local;
using DevAutopilot.Core.Pipeline;

namespace DevAutopilot.Cli.Tests;

public sealed class PipelineTests
{
    private static readonly AutopilotConfig Config = new()
    {
        Commands = new CommandsConfig { Build = "echo build", Test = "echo test" },
        WritablePaths = ["src/**", "tests/**"],
    };

    /// <summary>Records what the gate would get and returns fixed test facts.</summary>
    private sealed class FakeFactsCollector : IGateFactsCollector
    {
        public List<(string Base, string Head, Evidence Evidence)> Calls { get; } = [];

        public Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct)
        {
            Calls.Add((baseCommit, headCommit, evidence));
            return Task.FromResult(new GateInput([], new TestFacts(true, true, 1, 2, 0, "fake log"), evidence));
        }
    }

    private static async Task<TestRepo> CreateProjectAsync()
    {
        var repo = await TestRepo.CreateAsync(new Dictionary<string, string>
        {
            [".gitignore"] = ".autopilot/out/\n",
            ["src/Calc.cs"] = "public static class Calc { }\n",
        });
        repo.Write(".autopilot/tasks/001-add.md", "# Add numbers\nCalc.Add(a, b) should return a + b.\n");
        return repo;
    }

    private static FakeAgentRunner ScriptedRunner(bool reviewerAsksForChanges = true) => new((request, call) =>
    {
        switch (request.Role)
        {
            case AgentRole.Triage:
                return FakeAgentRunner.Answer(new { label = "agent-ready", reason = "Small and clear.", acceptanceCriteria = new[] { "Add(2, 3) returns 5" }, risk = "low" });

            case AgentRole.Implementer when call == 1:
                File.WriteAllText(Path.Combine(request.WorkingDirectory, "src", "Calc.cs"), "public static class Calc { public static int Add(int a, int b) => a + b; }\n");
                Directory.CreateDirectory(Path.Combine(request.WorkingDirectory, "tests"));
                File.WriteAllText(Path.Combine(request.WorkingDirectory, "tests", "CalcTests.cs"), "// Add(2, 3) == 5\n");
                return FakeAgentRunner.Answer(new { summary = "Added Calc.Add.", testsThatProveIt = new[] { "CalcTests.Add" }, doubts = Array.Empty<string>() });

            case AgentRole.Implementer:
                File.AppendAllText(Path.Combine(request.WorkingDirectory, "tests", "CalcTests.cs"), "// Add(-1, 1) == 0\n");
                return FakeAgentRunner.Answer(new { summary = "Added Calc.Add and a negative case.", testsThatProveIt = new[] { "CalcTests.Add", "CalcTests.Negative" }, doubts = Array.Empty<string>() });

            case AgentRole.Reviewer when call == 1 && reviewerAsksForChanges:
                return FakeAgentRunner.Answer(new { verdict = "request-changes", findings = new[] { "tests/CalcTests.cs: add a negative case" } });

            default:
                return FakeAgentRunner.Answer(new { verdict = "approve", findings = Array.Empty<string>() });
        }
    });

    private static AutopilotPipeline CreatePipeline(TestRepo repo, IAgentRunner runner, IGateFactsCollector facts, Func<GateInput, AutopilotConfig, GateDecision> gate) =>
        new(new AutopilotPipelineOptions
        {
            ProjectRoot = repo.Root,
            Config = Config,
            WorkItems = new LocalWorkItemSource(repo.Root),
            Runner = runner,
            Publisher = new LocalPublisher(repo.Root),
            Git = repo.Git,
            Facts = facts,
            WorktreeRoot = repo.WorktreeRoot,
            EvaluateGate = gate,
        });

    private static GateDecision PassWhenApproved(GateInput input, AutopilotConfig config) =>
        input.Evidence.Review?.Verdict == ReviewVerdict.Approve
            ? new GateDecision(GateOutcome.Passed, [])
            : new GateDecision(GateOutcome.Rejected, [new GateFinding("evidence.review", "The reviewer did not approve.")]);

    [Fact]
    public async Task Full_run_with_one_review_round_publishes_a_draft()
    {
        using var repo = await CreateProjectAsync();
        var runner = ScriptedRunner();
        var facts = new FakeFactsCollector();
        var pipeline = CreatePipeline(repo, runner, facts, PassWhenApproved);

        var summary = await pipeline.RunAsync(limit: null, CancellationToken.None);

        var triage = Assert.Single(summary.Triage);
        Assert.Equal(TriageLabel.AgentReady, triage.Triage?.Label);
        var result = Assert.Single(summary.Implemented);
        Assert.Equal(ImplementOutcome.Published, result.Outcome);
        Assert.Equal("autopilot/001", result.BranchName);

        var runFolder = ProjectPaths.RunFolder(repo.Root, "001");
        Assert.Equal(runFolder, result.Location);
        var pr = File.ReadAllText(Path.Combine(runFolder, "PULL_REQUEST.md"));
        Assert.Contains("Added Calc.Add and a negative case.", pr);
        Assert.Contains("+// Add(-1, 1) == 0", File.ReadAllText(Path.Combine(runFolder, "change.patch")));

        // Five agent runs, each with its own numbered folder and a line in runs.jsonl.
        Assert.Equal([AgentRole.Triage, AgentRole.Implementer, AgentRole.Reviewer, AgentRole.Implementer, AgentRole.Reviewer], runner.Requests.Select(r => r.Role));
        Assert.Equal(
            ["1-triage", "2-implementer", "3-reviewer", "4-implementer", "5-reviewer"],
            Directory.GetDirectories(runFolder).Select(Path.GetFileName).Order());
        var lines = File.ReadAllLines(Path.Combine(runFolder, "runs.jsonl"));
        Assert.Equal(5, lines.Length);
        Assert.Contains("\"role\":\"reviewer\"", lines[2]);
        Assert.Contains("\"model\":\"test-model\"", lines[2]);
        Assert.Contains("\"turns\":3", lines[2]);

        // The agent works outside the project, with the configured writable paths.
        var implementer = runner.Requests[1];
        Assert.Equal(Config.RiskPaths, implementer.ProtectedPaths);
        Assert.Equal(Path.Combine(repo.WorktreeRoot, "001"), implementer.WorkingDirectory);
        Assert.Equal(Config.WritablePaths, implementer.WritablePaths);
        Assert.NotNull(implementer.OutputJsonSchema);
        Assert.StartsWith(runFolder, implementer.RunDirectory);
        Assert.Contains("+public static class Calc { public static int Add", runner.Requests[2].Prompt);
        Assert.Contains("add a negative case", runner.Requests[3].Prompt);

        // The gate saw the final self-review and the approving review.
        var gateCall = Assert.Single(facts.Calls);
        Assert.Equal("Added Calc.Add and a negative case.", gateCall.Evidence.SelfReview?.Summary);
        Assert.Equal(ReviewVerdict.Approve, gateCall.Evidence.Review?.Verdict);
        Assert.Equal(await repo.Git.GetCommitAsync("main"), gateCall.Base);
        Assert.Equal(await repo.Git.GetCommitAsync("autopilot/001"), gateCall.Head);

        // Branch kept with two harness commits, worktrees gone.
        Assert.Equal("2", (await repo.GitAsync("rev-list", "--count", "main..autopilot/001")).Trim());
        Assert.False(Directory.Exists(Path.Combine(repo.WorktreeRoot, "001")));
        Assert.False(Directory.Exists(Path.Combine(repo.WorktreeRoot, AutopilotPipeline.TriageWorktreeName)));
        Assert.Single((await repo.GitAsync("worktree", "list", "--porcelain")).Split('\n'), l => l.StartsWith("worktree ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Real_gate_and_facts_collector_pass_a_small_tested_change()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // The fake dotnet that writes TRX files is a cmd script.
        }

        using var fake = new GateFactsCollectorTests.FakeDotnetOnPath();
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string>
        {
            [".gitignore"] = ".autopilot/out/\n",
            ["src/Calc.cs"] = "public static class Calc { }\n",
            ["result.trx"] = GateFactsCollectorTests.Trx(2, 0),
        });
        repo.Write(".autopilot/tasks/001-add.md", "# Add numbers\nCalc.Add(a, b) should return a + b.\n");
        var config = Config with { Commands = new CommandsConfig { Build = "echo build", Test = "dotnet test" } };
        var pipeline = new AutopilotPipeline(new AutopilotPipelineOptions
        {
            ProjectRoot = repo.Root,
            Config = config,
            WorkItems = new LocalWorkItemSource(repo.Root),
            Runner = ScriptedRunner(reviewerAsksForChanges: false),
            Publisher = new LocalPublisher(repo.Root),
            Git = repo.Git,
            Facts = new GateFactsCollector(repo.Git, config, repo.WorktreeRoot),
            WorktreeRoot = repo.WorktreeRoot,
        });

        var summary = await pipeline.RunAsync(1, CancellationToken.None);

        var result = Assert.Single(summary.Implemented);
        Assert.True(result.Outcome == ImplementOutcome.Published,
            result.Message + string.Join("; ", result.Decision?.Findings.Select(f => f.RuleId + ": " + f.Message) ?? []));
        Assert.Empty(Directory.GetDirectories(repo.WorktreeRoot));
    }

    [Fact]
    public async Task Rejected_change_writes_REJECTED_md_and_keeps_the_branch()
    {
        using var repo = await CreateProjectAsync();
        var pipeline = CreatePipeline(repo, ScriptedRunner(reviewerAsksForChanges: false), new FakeFactsCollector(),
            (_, _) => new GateDecision(GateOutcome.Rejected, [new GateFinding("files.max", "Too many files changed."), new GateFinding("risk.path", "Touches auth.", HighRisk: true)]));

        await pipeline.TriageAsync("001", CancellationToken.None);
        var result = await pipeline.ImplementAsync("001", CancellationToken.None);

        Assert.Equal(ImplementOutcome.Rejected, result.Outcome);
        var runFolder = ProjectPaths.RunFolder(repo.Root, "001");
        var rejected = File.ReadAllText(Path.Combine(runFolder, "REJECTED.md"));
        Assert.Contains("[files.max] Too many files changed.", rejected);
        Assert.Contains("(high risk)", rejected);
        Assert.False(File.Exists(Path.Combine(runFolder, "PULL_REQUEST.md")));
        Assert.True(File.Exists(Path.Combine(runFolder, "gate.json")));
        Assert.True(await repo.Git.BranchExistsAsync("autopilot/001"));
        Assert.False(Directory.Exists(Path.Combine(repo.WorktreeRoot, "001")));
        Assert.Equal(3, File.ReadAllLines(Path.Combine(runFolder, "runs.jsonl")).Length);
    }

    [Fact]
    public async Task Implement_requires_agent_ready_triage()
    {
        using var repo = await CreateProjectAsync();
        var runner = new FakeAgentRunner((r, _) => FakeAgentRunner.Answer(new { label = "needs-human", reason = "Which rounding rule?", acceptanceCriteria = Array.Empty<string>(), risk = "medium" }));
        var pipeline = CreatePipeline(repo, runner, new FakeFactsCollector(), PassWhenApproved);

        var before = await pipeline.ImplementAsync("001", CancellationToken.None);
        await pipeline.TriageAsync(null, CancellationToken.None);
        var after = await pipeline.ImplementAsync("001", CancellationToken.None);
        var missing = await pipeline.ImplementAsync("404", CancellationToken.None);

        Assert.Equal(ImplementOutcome.NotReady, before.Outcome);
        Assert.Contains("autopilot triage 001", before.Message);
        Assert.Equal(ImplementOutcome.NotReady, after.Outcome);
        Assert.Contains("Which rounding rule?", after.Message);
        Assert.Equal(ImplementOutcome.NotFound, missing.Outcome);
        Assert.False(await repo.Git.BranchExistsAsync("autopilot/001"));
        Assert.Contains("needs-human", File.ReadAllText(repo.Root + "/.autopilot/out/triage/001.md"));
    }

    [Fact]
    public async Task Existing_branch_is_not_overwritten_and_run_skips_it()
    {
        using var repo = await CreateProjectAsync();
        await repo.GitAsync("branch", "autopilot/001", "main");
        var runner = ScriptedRunner();
        var pipeline = CreatePipeline(repo, runner, new FakeFactsCollector(), PassWhenApproved);

        var summary = await pipeline.RunAsync(null, CancellationToken.None);
        var direct = await pipeline.ImplementAsync("001", CancellationToken.None);

        Assert.Empty(summary.Implemented);
        Assert.Contains(summary.Skipped, s => s.Contains("already exists", StringComparison.Ordinal));
        Assert.Equal(ImplementOutcome.Failed, direct.Outcome);
        Assert.Contains("git branch -D autopilot/001", direct.Message);
        Assert.DoesNotContain(runner.Requests, r => r.Role == AgentRole.Implementer);
    }

    [Fact]
    public async Task Implementer_without_changes_is_rejected()
    {
        using var repo = await CreateProjectAsync();
        var runner = new FakeAgentRunner((request, _) => request.Role == AgentRole.Triage
            ? FakeAgentRunner.Answer(new { label = "agent-ready", reason = "ok", acceptanceCriteria = new[] { "x" }, risk = "low" })
            : FakeAgentRunner.Answer(new { summary = "Nothing to do.", testsThatProveIt = Array.Empty<string>(), doubts = Array.Empty<string>() }));
        var pipeline = CreatePipeline(repo, runner, new FakeFactsCollector(), PassWhenApproved);

        await pipeline.TriageAsync("001", CancellationToken.None);
        var result = await pipeline.ImplementAsync("001", CancellationToken.None);

        Assert.Equal(ImplementOutcome.Rejected, result.Outcome);
        Assert.Contains("no changes", result.Message);
        Assert.True(File.Exists(Path.Combine(ProjectPaths.RunFolder(repo.Root, "001"), "REJECTED.md")));
    }

    [Fact]
    public async Task Runner_exception_is_reported_not_thrown()
    {
        using var repo = await CreateProjectAsync();
        var runner = new FakeAgentRunner((_, _) => throw new NotImplementedException("runner not built"));
        var pipeline = CreatePipeline(repo, runner, new FakeFactsCollector(), PassWhenApproved);

        var results = await pipeline.TriageAsync(null, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.False(result.Succeeded);
        Assert.Contains("runner not built", result.Error);
    }

    [Fact]
    public async Task Implement_refuses_when_the_work_item_changed_after_triage()
    {
        using var repo = await CreateProjectAsync();
        var runner = ScriptedRunner();
        var pipeline = CreatePipeline(repo, runner, new FakeFactsCollector(), PassWhenApproved);
        await pipeline.TriageAsync("001", CancellationToken.None);

        repo.Write(".autopilot/tasks/001-add.md", "# Add numbers\nCalc.Add(a, b) should return a * b.\n");
        var result = await pipeline.ImplementAsync("001", CancellationToken.None);

        Assert.Equal(ImplementOutcome.NotReady, result.Outcome);
        Assert.Contains("changed since it was triaged", result.Message);
        Assert.Contains("autopilot triage 001", result.Message);
        Assert.DoesNotContain(runner.Requests, r => r.Role == AgentRole.Implementer);
        Assert.False(await repo.Git.BranchExistsAsync("autopilot/001"));
    }

    [Fact]
    public async Task Triage_record_stores_a_hash_of_title_and_body()
    {
        using var repo = await CreateProjectAsync();
        var pipeline = CreatePipeline(repo, ScriptedRunner(), new FakeFactsCollector(), PassWhenApproved);
        await pipeline.TriageAsync("001", CancellationToken.None);

        var record = await new TriageStore(repo.Root).LoadRecordAsync("001", CancellationToken.None);
        var item = await new LocalWorkItemSource(repo.Root).GetAsync("001", CancellationToken.None);

        Assert.NotNull(record?.TextHash);
        Assert.Equal(TriageStore.TextHash(item!), record!.TextHash);
        Assert.Equal(64, record.TextHash!.Length);
        Assert.NotEqual(TriageStore.TextHash(item! with { Body = item.Body + "!" }), record.TextHash);
    }

    [Fact]
    public async Task Invalid_ids_are_reported_and_skipped_without_stopping_the_run()
    {
        using var repo = await CreateProjectAsync();
        repo.Write(".autopilot/tasks/bad id.md", "# Bad\nbody\n");
        repo.Write(".autopilot/tasks/_hidden.md", "# Hidden\nbody\n");
        var runner = ScriptedRunner(reviewerAsksForChanges: false);
        var pipeline = CreatePipeline(repo, runner, new FakeFactsCollector(), PassWhenApproved);

        var summary = await pipeline.RunAsync(null, CancellationToken.None);

        Assert.Equal(3, summary.Triage.Count);
        Assert.Contains(summary.Triage, t => t.WorkItemId == "bad id" && t.Error!.Contains("not valid", StringComparison.Ordinal));
        Assert.Contains(summary.Triage, t => t.WorkItemId == "_hidden" && t.Error!.Contains("not valid", StringComparison.Ordinal));
        Assert.Equal(ImplementOutcome.Published, Assert.Single(summary.Implemented).Outcome);
        Assert.Equal(1, runner.Requests.Count(r => r.Role == AgentRole.Triage));

        var direct = await pipeline.ImplementAsync("../x", CancellationToken.None);
        Assert.Equal(ImplementOutcome.Failed, direct.Outcome);
        Assert.Contains("not valid", direct.Message);
        var triageOne = Assert.Single(await pipeline.TriageAsync("a/b", CancellationToken.None));
        Assert.Contains("not valid", triageOne.Error);
    }
}

public sealed class WorkItemIdTests
{
    [Theory]
    [InlineData("001")]
    [InlineData("fix-404")]
    [InlineData("Bug_12")]
    [InlineData("a")]
    public void Valid_ids(string id) => Assert.True(ProjectPaths.IsValidId(id));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("_triage")]
    [InlineData(".hidden")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("@triage")]
    [InlineData("café")]
    [InlineData("a.b")]
    public void Invalid_ids(string? id) => Assert.False(ProjectPaths.IsValidId(id));

    [Fact]
    public void Ids_are_at_most_64_characters()
    {
        Assert.True(ProjectPaths.IsValidId(new string('a', 64)));
        Assert.False(ProjectPaths.IsValidId(new string('a', 65)));
    }

    [Fact]
    public void Internal_worktree_names_cannot_be_ids()
    {
        Assert.False(ProjectPaths.IsValidId(AutopilotPipeline.TriageWorktreeName));
        Assert.False(ProjectPaths.IsValidId(GateFactsCollector.InternalName("001", "gate-base")));
    }
}
