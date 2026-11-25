using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Local;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Prompts;

namespace Autopilot.Cli.Tests;

/// <summary>Project context, path rules for triage, configured commands and remote-only base branches in the pipeline.</summary>
public sealed class PipelineAdoptionTests
{
    private const string Rules = "Business logic lives in Logic/, never in controllers.";

    private sealed class Facts : IGateFactsCollector
    {
        public Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct) =>
            Task.FromResult(new GateInput([], new TestFacts(true, true, 1, 2, 0, "fake log"), evidence));
    }

    private static AutopilotConfig Config(IReadOnlyList<string>? context = null, string baseBranch = "main") => new()
    {
        BaseBranch = baseBranch,
        Commands = new CommandsConfig { Build = "dotnet build \"Shop.sln\" -p:Deterministic=true", Test = "dotnet test \"Shop.sln\" --no-build" },
        WritablePaths = ["src/**", "tests/**"],
        Context = context ?? [],
    };

    private static async Task<TestRepo> ProjectAsync()
    {
        var repo = await TestRepo.CreateAsync(new Dictionary<string, string>
        {
            [".gitignore"] = ".autopilot/out/\n",
            ["src/Calc.cs"] = "public static class Calc { }\n",
            ["docs/agent-rules.md"] = Rules + "\n",
            ["docs/decisions/0001-layers.md"] = "Use three layers.\n",
        });
        repo.Write(".autopilot/tasks/001-add.md", "# Add numbers\nCalc.Add(a, b) should return a + b.\n");
        return repo;
    }

    private static FakeAgentRunner Runner() => new((request, _) => request.Role switch
    {
        AgentRole.Triage => FakeAgentRunner.Answer(new { label = "agent-ready", reason = "Small.", acceptanceCriteria = new[] { "Add(2, 3) returns 5" }, risk = "low" }),
        AgentRole.Implementer => Change(request.WorkingDirectory),
        _ => FakeAgentRunner.Answer(new { verdict = "approve", findings = Array.Empty<string>() }),
    });

    private static AgentRunResult Change(string folder)
    {
        File.WriteAllText(Path.Combine(folder, "src", "Calc.cs"), "public static class Calc { public static int Add(int a, int b) => a + b; }\n");
        return FakeAgentRunner.Answer(new { summary = "Added Add.", testsThatProveIt = new[] { "CalcTests.Add" }, doubts = Array.Empty<string>() });
    }

    private static AutopilotPipeline Pipeline(TestRepo repo, AutopilotConfig config, IAgentRunner runner, TextWriter? log = null) => new(new AutopilotPipelineOptions
    {
        ProjectRoot = repo.Root,
        Config = config,
        WorkItems = new LocalWorkItemSource(repo.Root),
        Runner = runner,
        Publisher = new LocalPublisher(repo.Root),
        Git = repo.Git,
        Facts = new Facts(),
        WorktreeRoot = repo.WorktreeRoot,
        EvaluateGate = (_, _) => new GateDecision(GateOutcome.Passed, []),
        Log = log,
    });

    [Fact]
    public async Task Context_from_the_base_commit_reaches_every_prompt_and_is_protected()
    {
        using var repo = await ProjectAsync();
        repo.Write("docs/agent-rules.md", "UNCOMMITTED EDIT\n");
        var runner = Runner();
        var pipeline = Pipeline(repo, Config(["docs/agent-rules.md", "docs/decisions/"]), runner);

        var summary = await pipeline.RunAsync(null, CancellationToken.None);

        Assert.Equal(ImplementOutcome.Published, Assert.Single(summary.Implemented).Outcome);
        Assert.Equal([AgentRole.Triage, AgentRole.Implementer, AgentRole.Reviewer], runner.Requests.Select(r => r.Role));
        foreach (var request in runner.Requests)
        {
            Assert.Contains(ProjectContext.Heading, request.Prompt, StringComparison.Ordinal);
            Assert.Contains(Rules, request.Prompt, StringComparison.Ordinal);
            Assert.Contains("<file path=\"docs/decisions/0001-layers.md\">", request.Prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("UNCOMMITTED EDIT", request.Prompt, StringComparison.Ordinal);
            Assert.Contains("docs/agent-rules.md", request.ProtectedPaths!);
            Assert.Contains("docs/decisions/**", request.ProtectedPaths!);
            Assert.Contains("**/*.csproj", request.ProtectedPaths!);
        }
    }

    [Fact]
    public async Task Missing_context_path_stops_triage_with_a_clear_message()
    {
        using var repo = await ProjectAsync();
        repo.Write("docs/new-rules.md", "only in the working tree\n");
        var runner = Runner();

        var result = Assert.Single(await Pipeline(repo, Config(["docs/new-rules.md"]), runner).TriageAsync(null, CancellationToken.None));

        Assert.False(result.Succeeded);
        Assert.Contains("docs/new-rules.md", result.Error, StringComparison.Ordinal);
        Assert.Contains("base commit", result.Error, StringComparison.Ordinal);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Triage_prompt_lists_risk_and_protected_paths_with_the_rule()
    {
        using var repo = await ProjectAsync();
        var runner = Runner();

        await Pipeline(repo, Config(["docs/agent-rules.md"]), runner).TriageAsync("001", CancellationToken.None);

        var prompt = Assert.Single(runner.Requests).Prompt;
        Assert.Contains("## Paths an agent may not change", prompt, StringComparison.Ordinal);
        Assert.Contains("- `**/*.csproj`", prompt, StringComparison.Ordinal);
        Assert.Contains("- `**/appsettings*.json`", prompt, StringComparison.Ordinal);
        Assert.Contains("- `.autopilot/**`", prompt, StringComparison.Ordinal);
        Assert.Contains("- `docs/agent-rules.md`", prompt, StringComparison.Ordinal);
        Assert.Contains(PromptLibrary.PathRule, prompt, StringComparison.Ordinal);
        Assert.Contains("needs-human", PromptLibrary.SystemPrompt(AgentRole.Triage), StringComparison.Ordinal);
        Assert.Contains("risk path or a protected path", PromptLibrary.SystemPrompt(AgentRole.Triage), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Implementer_gets_the_configured_commands_and_other_roles_do_not()
    {
        using var repo = await ProjectAsync();
        var runner = Runner();
        var config = Config();

        await Pipeline(repo, config, runner).RunAsync(null, CancellationToken.None);

        var implementer = runner.Requests.Single(r => r.Role == AgentRole.Implementer);
        Assert.Equal([config.Commands.Build, config.Commands.Test], implementer.ConfiguredCommands!);
        Assert.Contains(config.Commands.Build, implementer.RoleConfig.AllowedCommands);
        Assert.Contains(config.Commands.Test, implementer.RoleConfig.AllowedCommands);
        Assert.Contains("dotnet build", implementer.RoleConfig.AllowedCommands);
        Assert.All(runner.Requests.Where(r => r.Role != AgentRole.Implementer), r => Assert.Null(r.ConfiguredCommands));
        Assert.DoesNotContain(config.Commands.Build, runner.Requests.Single(r => r.Role == AgentRole.Reviewer).RoleConfig.AllowedCommands);
    }

    [Fact]
    public async Task Base_branch_that_exists_only_on_the_remote_is_used_with_a_note()
    {
        using var repo = await ProjectAsync();
        var commit = await repo.Git.GetCommitAsync("main");
        await repo.GitAsync("remote", "add", "origin", "https://example.invalid/shop.git");
        await repo.GitAsync("update-ref", "refs/remotes/origin/develop", commit);
        var log = new StringWriter();
        var runner = Runner();

        var summary = await Pipeline(repo, Config(baseBranch: "develop"), runner, log).RunAsync(null, CancellationToken.None);

        Assert.True(Assert.Single(summary.Triage).Succeeded);
        var implemented = Assert.Single(summary.Implemented);
        Assert.Equal(ImplementOutcome.Published, implemented.Outcome);
        Assert.Equal(commit, (await repo.GitAsync("merge-base", "autopilot/001", "refs/remotes/origin/develop")).Trim());
        Assert.Contains("exists only as origin/develop", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("git branch develop origin/develop", log.ToString(), StringComparison.Ordinal);
        Assert.False(await repo.Git.BranchExistsAsync("develop"));
    }

    [Fact]
    public async Task Missing_base_branch_says_to_fetch()
    {
        using var repo = await ProjectAsync();

        var result = Assert.Single(await Pipeline(repo, Config(baseBranch: "release"), Runner()).TriageAsync(null, CancellationToken.None));

        Assert.Contains("remote-tracking", result.Error, StringComparison.Ordinal);
        Assert.Contains("git fetch", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Pull_request_text_keeps_triage_criteria_under_their_own_heading()
    {
        var item = new WorkItem("7", "Late fee rounding", "Fees are off by a cent.\n\n## Acceptance criteria\n\nFrom the work item itself.", []);
        var triage = new TriageResult(TriageLabel.AgentReady, "Clear.", ["A fee of 0.125 rounds to 0.13"], RiskLevel.Low);
        var request = new PublishRequest(item, "autopilot/7", "diff", new GateDecision(GateOutcome.Passed, []),
            new Evidence("7", triage, new SelfReview("Fixed rounding.", ["FeeTests.Rounds"], []), new ReviewResult(ReviewVerdict.Approve, [])));

        var text = LocalPublisher.PullRequestText(request).Replace("\r\n", "\n", StringComparison.Ordinal);

        var workItem = text.IndexOf("## Work item", StringComparison.Ordinal);
        var fromTriage = text.IndexOf("## Acceptance criteria from triage\n", StringComparison.Ordinal);
        Assert.True(workItem >= 0 && fromTriage > workItem);
        Assert.Contains("From the work item itself.", text[workItem..fromTriage], StringComparison.Ordinal);
        Assert.Contains("- A fee of 0.125 rounds to 0.13", text[fromTriage..], StringComparison.Ordinal);
        Assert.DoesNotContain("\nAcceptance criteria:\n", text, StringComparison.Ordinal);
    }
}

public sealed class ProjectContextTests
{
    [Fact]
    public async Task Text_past_the_limit_is_cut_with_a_note()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string>
        {
            ["rules/a.md"] = new string('a', 30) + "\n",
            ["rules/b.md"] = new string('b', 30) + "\n",
            ["rules/c.md"] = "c\n",
        });
        var commit = await repo.Git.GetCommitAsync("main");

        var context = await ProjectContext.LoadAsync(repo.Git, commit, ["rules"], CancellationToken.None, maxChars: 50);

        Assert.True(context.Truncated);
        Assert.Equal(50, context.TotalChars);
        Assert.Equal(["rules/a.md", "rules/b.md"], context.Files.Select(f => f.Path));
        Assert.Contains(context.Notes, n => n.StartsWith("rules/b.md: cut after 20 of 30", StringComparison.Ordinal));
        Assert.Contains(context.Notes, n => n.StartsWith("rules/c.md: not shown", StringComparison.Ordinal));
        Assert.Contains("cut at its size limit", context.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Binary_files_are_skipped_and_missing_paths_are_reported()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["rules/a.md"] = "rule\n" });
        File.WriteAllBytes(Path.Combine(repo.Root, "rules", "logo.bin"), [0x89, 0x50, 0x00, 0x01, 0x00]);
        await repo.Git.CommitAllAsync(repo.Root, "logo");
        var commit = await repo.Git.GetCommitAsync("main");

        var context = await ProjectContext.LoadAsync(repo.Git, commit, ["rules/", "docs/none.md"], CancellationToken.None);

        Assert.Equal(["rules/a.md"], context.Files.Select(f => f.Path));
        Assert.Contains(context.Notes, n => n.Contains("binary", StringComparison.Ordinal));
        Assert.Equal(["docs/none.md"], context.Missing);
    }

    [Fact]
    public void No_context_renders_nothing()
    {
        Assert.Equal(string.Empty, ProjectContext.Empty.Render());
        var item = new WorkItem("1", "T", "B", []);
        Assert.DoesNotContain(ProjectContext.Heading, PromptLibrary.TriagePrompt(item), StringComparison.Ordinal);
        Assert.Equal(40 * 1024, ProjectContext.MaxChars);
    }
}
