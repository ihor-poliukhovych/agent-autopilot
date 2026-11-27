using System.Text.Json;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Git;
using Autopilot.Core.Pool;
using Autopilot.Core.Process;
using Autopilot.Core.Scheduling;
using Autopilot.Core.Settings;
using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;
using Ws = Autopilot.Core.Workspace.Workspace;

namespace Autopilot.Cli.Tests;

/// <summary>The integration branch against a real temporary git repository, and the unattended pool through the command line with a fake agent.</summary>
public sealed class PoolUnattendedCliTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeSchedulerForPool _scheduler = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string WorkspacePath => _temp.Combine("ws");

    private async Task<(int Exit, string Out, string Err)> RunWithAsync(Func<CliContext, CliContext> adjust, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var context = adjust(new CliContext(TextReader.Null, output, error)
        {
            DoctorChecksTools = false,
            Scheduler = _scheduler,
            IsProcessAlive = _ => true,
            Program = ("autopilot", []),
        });
        var exit = await CliApp.RunAsync([.. args, "--workspace", WorkspacePath], context);
        return (exit, output.ToString(), error.ToString());
    }

    private Task<(int Exit, string Out, string Err)> Cli(params string[] args) => RunWithAsync(c => c, args);

    // ---------- integration branch on a real repository ----------

    private static AutopilotConfig Config(Func<AutopilotConfig, AutopilotConfig>? change = null) =>
        (change ?? (c => c))(new AutopilotConfig { Commands = new CommandsConfig { Build = "echo build", Test = "echo test" } });

    private IntegrationService Service(TestRepo repo, AutopilotConfig config, Func<string, CancellationToken, Task<(bool Ok, string Message)>>? verify = null) =>
        new(new Ws(WorkspacePath), "demo", repo.Git, config, repo.WorktreeRoot, verify ?? ((_, _) => Task.FromResult((true, string.Empty))));

    private static async Task<string> TaskBranchAsync(TestRepo repo, string name, string file, string content)
    {
        await repo.GitAsync("checkout", "-q", "-b", name, "main");
        repo.Write(file, content);
        await repo.Git.CommitAllAsync(repo.Root, "task " + name);
        await repo.GitAsync("checkout", "-q", "main");
        return name;
    }

    [Fact]
    public async Task The_integration_branch_starts_from_the_base_and_a_green_task_is_merged_into_it_only()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["src/a.cs"] = "class A {}\n" });
        var mainBefore = (await repo.GitAsync("rev-parse", "main")).Trim();
        var service = Service(repo, Config());
        var now = new DateTimeOffset(2026, 11, 28, 1, 0, 0, TimeSpan.Zero);

        var branch = await service.EnsureBranchAsync(now, default);
        Assert.StartsWith("integration/", branch, StringComparison.Ordinal);
        Assert.Equal(branch, await service.EnsureBranchAsync(now.AddDays(3), default)); // the same branch for the next run
        Assert.Equal(mainBefore, (await repo.GitAsync("rev-parse", branch!)).Trim());

        await TaskBranchAsync(repo, "autopilot/1", "src/one.cs", "class One {}\n");
        var merged = await service.MergeAsync("1", "autopilot/1", default);

        Assert.True(merged.Merged, merged.Message);
        Assert.Contains("class One {}", await repo.GitAsync("show", $"{branch}:src/one.cs"), StringComparison.Ordinal);
        Assert.Equal(mainBefore, (await repo.GitAsync("rev-parse", "main")).Trim()); // the base branch is untouched
        var info = service.Info();
        Assert.Equal(branch, info.Branch);
        Assert.Equal("1", Assert.Single(info.Merges).WorkItemId);
        Assert.False(Directory.Exists(Path.Combine(repo.WorktreeRoot, IntegrationService.WorktreeName)));
    }

    [Fact]
    public async Task A_task_whose_build_or_tests_fail_is_not_kept_in_the_integration_branch()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["src/a.cs"] = "class A {}\n" });
        var service = Service(repo, Config(), (_, _) => Task.FromResult((false, "Test failed.")));
        var branch = (await service.EnsureBranchAsync(DateTimeOffset.UtcNow, default))!;
        var before = (await repo.GitAsync("rev-parse", branch)).Trim();
        await TaskBranchAsync(repo, "autopilot/1", "src/one.cs", "class One {}\n");

        var merged = await service.MergeAsync("1", "autopilot/1", default);

        Assert.False(merged.Merged);
        Assert.Contains("Test failed.", merged.Message, StringComparison.Ordinal);
        Assert.Equal(before, (await repo.GitAsync("rev-parse", branch)).Trim());
        Assert.Empty(service.Info().Merges);
    }

    [Fact]
    public async Task A_task_that_conflicts_with_earlier_work_is_refused_and_the_branch_is_left_alone()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["src/a.cs"] = "line\n" });
        var service = Service(repo, Config());
        var branch = (await service.EnsureBranchAsync(DateTimeOffset.UtcNow, default))!;
        await TaskBranchAsync(repo, "autopilot/1", "src/a.cs", "first\n");
        await TaskBranchAsync(repo, "autopilot/2", "src/a.cs", "second\n");

        Assert.True((await service.MergeAsync("1", "autopilot/1", default)).Merged);
        var head = (await repo.GitAsync("rev-parse", branch)).Trim();
        var conflict = await service.MergeAsync("2", "autopilot/2", default);

        Assert.False(conflict.Merged);
        Assert.Contains("conflicts", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(head, (await repo.GitAsync("rev-parse", branch)).Trim());
    }

    [Theory]
    [InlineData("main", "main")]
    [InlineData("integration/x", "integration/x")]
    public async Task The_pool_refuses_to_use_the_base_branch_as_the_integration_branch(string branch, string baseBranch)
    {
        using var repo = await TestRepo.CreateAsync();
        var config = Config(c => c with { BaseBranch = baseBranch, Pool = c.Pool with { Integration = new IntegrationConfig { Branch = branch } } });
        var service = Service(repo, config);

        await Assert.ThrowsAsync<GitException>(() => service.EnsureBranchAsync(DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public async Task Integration_off_makes_no_branch()
    {
        using var repo = await TestRepo.CreateAsync();
        var service = Service(repo, Config(c => c with { Pool = c.Pool with { Integration = new IntegrationConfig { Enabled = false } } }));
        Assert.Null(await service.EnsureBranchAsync(DateTimeOffset.UtcNow, default));
        Assert.Null(service.CurrentBranch());
    }

    [Fact]
    public async Task The_final_pr_is_opened_as_a_draft_and_nothing_is_marked_ready_or_merged()
    {
        using var repo = await TestRepo.CreateAsync();
        var service = Service(repo, Config(c => c with { Pool = c.Pool with { Integration = new IntegrationConfig { Branch = "integration/night" } } }));
        await service.EnsureBranchAsync(DateTimeOffset.UtcNow, default);
        await Assert.ThrowsAsync<GitException>(() => service.OpenFinalPrAsync(null, default)); // nothing merged yet

        await TaskBranchAsync(repo, "autopilot/7", "src/seven.cs", "class Seven {}\n");
        await service.MergeAsync("7", "autopilot/7", default);
        var calls = new List<string>();

        var url = await service.OpenFinalPrAsync((file, args, _) =>
        {
            calls.Add(file + " " + string.Join(' ', args.Take(4)));
            return Task.FromResult(new ProcessResult(0, "https://example.test/pr/9\n", string.Empty, string.Empty, false, TimeSpan.Zero));
        }, default);

        Assert.Equal("https://example.test/pr/9", url);
        Assert.Equal("https://example.test/pr/9", service.Info().FinalPrUrl);
        Assert.Equal(["git push --set-upstream origin integration/night", "gh pr create --draft --base"], calls);
        Assert.DoesNotContain(calls, c => c.Contains("merge", StringComparison.OrdinalIgnoreCase) || c.Contains("ready", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- command line ----------

    private async Task<TestRepo> AddProjectAsync()
    {
        var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { [".gitignore"] = ".autopilot/out/\n", ["src/a.cs"] = "class A {}\n" });
        Assert.Equal(0, (await Cli("project", "add", "demo", "--repo", repo.Root)).Exit);
        var settings = new ProjectSettingsService(repo.Root, new Ws(WorkspacePath).ProjectFolder("demo"));
        settings.Save(JsonNode("""{ "commands": { "build": "echo build", "test": "echo test" }, "writablePaths": ["src/**", "tests/**"] }"""));
        return repo;
    }

    private static System.Text.Json.Nodes.JsonObject JsonNode(string json) => (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!;

    [Fact]
    public async Task Pool_settings_are_shown_changed_and_checked()
    {
        using var repo = await AddProjectAsync();

        var changed = await Cli("pool", "settings", "--concurrency", "2", "--unattended", "true", "--max-agent-runs", "6", "--project", "demo");
        Assert.Equal(0, changed.Exit);
        var config = ConfigLoader.Load(repo.Root, new Ws(WorkspacePath).ProjectFolder("demo"));
        Assert.True(config.Pool.Unattended);
        Assert.Equal(2, config.Pool.Concurrency);
        Assert.Equal(6, config.Pool.Budget.MaxAgentRuns);
        Assert.Contains("\"concurrency\": 2", (await Cli("pool", "settings", "--project", "demo")).Out, StringComparison.Ordinal);

        var refused = await Cli("pool", "settings", "--concurrency", "9", "--project", "demo");
        Assert.Equal(1, refused.Exit);
        Assert.Contains("pool.concurrency", refused.Err, StringComparison.Ordinal);
        Assert.Equal(2, ConfigLoader.Load(repo.Root, new Ws(WorkspacePath).ProjectFolder("demo")).Pool.Concurrency);
    }

    [Fact]
    public async Task Schedule_set_takes_a_date_a_repeat_a_stop_time_and_a_limit()
    {
        using var repo = await AddProjectAsync();

        var plan = await Cli("schedule", "set", "--at", "2099-01-05 02:00", "--until", "06:00", "--limit", "8", "--unattended", "--project", "demo");
        Assert.Equal(0, plan.Exit);
        Assert.Contains("on 2099-01-05 at 02:00", plan.Out, StringComparison.Ordinal);
        Assert.Contains("Nothing was installed", plan.Out, StringComparison.Ordinal);
        Assert.Empty(_scheduler.Jobs);

        var installed = await Cli("schedule", "set", "--at", "2099-01-05 02:00", "--until", "06:00", "--limit", "8", "--unattended", "--yes", "--project", "demo");
        Assert.Contains("Installed", installed.Out, StringComparison.Ordinal);
        var command = _scheduler.Jobs["autopilot-demo"].Command;
        Assert.Contains("pool run --limit 8 --unattended --until 06:00 --only-on 2099-01-05 --scheduled", command, StringComparison.Ordinal);
        Assert.True(_scheduler.Jobs["autopilot-demo-catchup"].OnLogon);
        Assert.Contains("on 2099-01-05", (await Cli("schedule", "show", "--project", "demo")).Out, StringComparison.Ordinal);

        var weekly = await Cli("schedule", "set", "--at", "03:30", "--repeat", "mon,fri", "--yes", "--project", "demo");
        Assert.Equal(0, weekly.Exit);
        Assert.Equal("mon,fri", _scheduler.Jobs["autopilot-demo"].Repeat);
        Assert.Contains("mon,fri", (await Cli("schedule", "show", "--project", "demo")).Out, StringComparison.Ordinal);

        Assert.NotEqual(0, (await Cli("schedule", "set", "--at", "2020-01-01 02:00", "--yes", "--project", "demo")).Exit);
        Assert.NotEqual(0, (await Cli("schedule", "set", "--at", "02:00", "--repeat", "someday", "--yes", "--project", "demo")).Exit);
        Assert.Contains("No scheduled run was missed", (await Cli("schedule", "catch-up", "--project", "demo")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_one_time_run_does_nothing_on_another_date()
    {
        using var repo = await AddProjectAsync();
        var result = await Cli("pool", "run", "--only-on", "2099-01-05", "--project", "demo");
        Assert.Equal(0, result.Exit);
        Assert.Contains("does nothing today", result.Out, StringComparison.Ordinal);
    }

    private sealed class PassingFacts : IGateFactsCollector
    {
        public Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct) =>
            Task.FromResult(new GateInput([], new TestFacts(true, true, 1, 2, 0, "fake log"), evidence));
    }

    private Task<(int Exit, string Out, string Err)> PoolRunAsync(FakeAgentRunner runner, params string[] args) =>
        RunWithAsync(c => c with
        {
            RunnerFactory = () => runner,
            WorktreeRoot = _temp.Combine("worktrees"),
            FactsCollectorFactory = _ => new PassingFacts(),
            EvaluateGate = (input, _) => new GateDecision(GateOutcome.Passed, []),
        }, args);

    private static AgentRunResult Implementing(AgentRunRequest request)
    {
        File.WriteAllText(Path.Combine(request.WorkingDirectory, "src", "a.cs"), "class A { int Add(int a, int b) => a + b; }\n");
        Directory.CreateDirectory(Path.Combine(request.WorkingDirectory, "tests"));
        File.WriteAllText(Path.Combine(request.WorkingDirectory, "tests", "ATests.cs"), "// Add\n");
        return FakeAgentRunner.Answer(new { summary = "Added Add.", testsThatProveIt = new[] { "ATests.Add" }, doubts = Array.Empty<string>() });
    }

    [Fact]
    public async Task Pool_run_builds_on_the_integration_branch_and_leaves_the_base_branch_alone()
    {
        using var repo = await AddProjectAsync();
        var mainBefore = (await repo.GitAsync("rev-parse", "main")).Trim();
        await Cli("task", "add", "Add numbers", "--body", "Add(a, b) returns a + b.", "--project", "demo");
        await Cli("pool", "add", "1", "--project", "demo");
        var runner = new FakeAgentRunner((request, _) => request.Role switch
        {
            AgentRole.Triage => FakeAgentRunner.Answer(new { label = "agent-ready", reason = "Clear.", acceptanceCriteria = new[] { "Add works" }, risk = "low" }),
            AgentRole.Implementer => Implementing(request),
            _ => FakeAgentRunner.Answer(new { verdict = "approve", findings = Array.Empty<string>() }),
        });
        // The task has to be triaged first: this is what a person does before adding it to the pool.
        Assert.Equal(0, (await PoolRunAsync(runner, "triage", "1", "--project", "demo")).Exit);

        var run = await PoolRunAsync(runner, "pool", "run", "--project", "demo");

        Assert.Equal(0, run.Exit);
        Assert.Contains("Integration branch: integration/", run.Out, StringComparison.Ordinal);
        Assert.Contains("Done 1", run.Out, StringComparison.Ordinal);
        var integration = (await Cli("pool", "integration", "--json", "--project", "demo")).Out;
        using var json = JsonDocument.Parse(integration);
        var branch = json.RootElement.GetProperty("branch").GetString()!;
        Assert.Equal("main", json.RootElement.GetProperty("baseBranch").GetString());
        Assert.Single(json.RootElement.GetProperty("merges").EnumerateArray());
        Assert.Contains("a + b", await repo.GitAsync("show", $"{branch}:src/a.cs"), StringComparison.Ordinal);
        Assert.Equal(mainBefore, (await repo.GitAsync("rev-parse", "main")).Trim());
    }

    [Fact]
    public async Task An_unattended_pool_run_lets_the_master_answer_a_question_and_goes_on()
    {
        using var repo = await AddProjectAsync();
        await Cli("task", "add", "Sort the list", "--body", "Sort the list. Ask which order.", "--project", "demo");
        await Cli("pool", "add", "1", "--project", "demo");
        var triaged = 0;
        var runner = new FakeAgentRunner((request, _) =>
        {
            if (request.OutputJsonSchema?.Contains("reversible", StringComparison.Ordinal) == true)
            {
                return FakeAgentRunner.Answer(new { decide = true, option = 1, answer = "Sort by name.", reason = "It is the recommended option.", category = "other", reversible = true });
            }

            return request.Role switch
            {
                AgentRole.Triage when ++triaged == 1 => FakeAgentRunner.Answer(new { label = "needs-human", reason = "Which order? Option 1: by name (recommended). Option 2: by date.", acceptanceCriteria = Array.Empty<string>(), risk = "low" }),
                AgentRole.Triage => FakeAgentRunner.Answer(new { label = "agent-ready", reason = "Clear now.", acceptanceCriteria = new[] { "Sorted by name" }, risk = "low" }),
                AgentRole.Implementer => Implementing(request),
                _ => FakeAgentRunner.Answer(new { verdict = "approve", findings = Array.Empty<string>() }),
            };
        });
        Assert.Equal(0, (await PoolRunAsync(runner, "triage", "1", "--project", "demo")).Exit);

        var run = await PoolRunAsync(runner, "pool", "run", "--unattended", "--project", "demo");

        Assert.Equal(0, run.Exit);
        Assert.Contains("Done 1", run.Out, StringComparison.Ordinal);
        var decisions = JsonDocument.Parse((await Cli("pool", "decisions", "--json", "--project", "demo")).Out).RootElement;
        var decision = Assert.Single(decisions.EnumerateArray());
        Assert.Equal("Sort by name.", decision.GetProperty("answer").GetString());
        var show = (await Cli("task", "show", "1", "--project", "demo")).Out;
        Assert.Contains("Answer: option 1 (pool master)", show, StringComparison.Ordinal);

        // A person undoes the decision: the answer leaves the task and the task is back in the pool.
        var id = decision.GetProperty("id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, (await Cli("pool", "decisions", "revert", id, "--project", "demo")).Exit);
        Assert.DoesNotContain("(pool master)", (await Cli("task", "show", "1", "--project", "demo")).Out, StringComparison.Ordinal);
        Assert.Contains("1", (await Cli("pool", "list", "--project", "demo")).Out, StringComparison.Ordinal);
        Assert.Equal(1, (await Cli("pool", "decisions", "revert", id, "--project", "demo")).Exit);
    }

    private sealed class FakeSchedulerForPool : IScheduler
    {
        public Dictionary<string, ScheduledJob> Jobs { get; } = [];

        public string Description => "a fake scheduler";

        public Task InstallAsync(ScheduledJob job, CancellationToken ct)
        {
            Jobs[job.Name] = job;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string name, CancellationToken ct) => Task.FromResult(Jobs.Remove(name));

        public Task<bool> ExistsAsync(string name, CancellationToken ct) => Task.FromResult(Jobs.ContainsKey(name));
    }
}
