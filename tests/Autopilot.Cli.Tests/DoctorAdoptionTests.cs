using Autopilot.Cli.Commands;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;

namespace Autopilot.Cli.Tests;

/// <summary>Doctor on a clean checkout, failure excerpts and logs, remote-only base branches, context and module notes.</summary>
public sealed class DoctorAdoptionTests
{
    /// <summary>An environment variable name that is never set, so no Azure DevOps request is ever made.</summary>
    private const string UnsetToken = "AUTOPILOT_TEST_NEVER_SET_TOKEN";

    private static async Task<TestRepo> RepoAsync(Dictionary<string, string>? files = null)
    {
        var all = new Dictionary<string, string> { [".gitignore"] = ".autopilot/out/\n", ["src/App.cs"] = "class App { }\n" };
        foreach (var (key, value) in files ?? [])
        {
            all[key] = value;
        }

        return await TestRepo.CreateAsync(all);
    }

    private static void Save(TestRepo repo, AutopilotConfig config)
    {
        ConfigLoader.Save(config with { WritablePaths = ["src/**"] }, repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, ".autopilot", "tasks"));
    }

    private static Task<IReadOnlyList<CheckResult>> RunAsync(TestRepo repo, bool skipBuild = false, bool inPlace = false, TextWriter? output = null) =>
        DoctorCommand.RunChecksAsync(repo.Root, new DoctorOptions { SkipBuild = skipBuild, InPlace = inPlace, CheckTools = false, WorktreeRoot = repo.WorktreeRoot },
            output ?? TextWriter.Null, CancellationToken.None);

    private static CheckResult Check(IReadOnlyList<CheckResult> results, string name) => results.Single(r => r.Name == name);

    [Fact]
    public async Task Build_runs_on_a_clean_checkout_of_the_base_commit_by_default()
    {
        using var repo = await RepoAsync();
        repo.Write("marker.txt", "only in the working tree\n");
        Save(repo, new AutopilotConfig { Commands = new CommandsConfig { Build = Shell.PrintFile("marker.txt"), Test = "echo tested" } });

        var clean = await RunAsync(repo);
        var inPlace = await RunAsync(repo, inPlace: true);

        Assert.Equal(CheckStatus.Fail, Check(clean, "build command").Status);
        Assert.Contains("on a clean checkout of main", Check(clean, "build command").Detail, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Ok, Check(inPlace, "build command").Status);
        Assert.Contains("in the project folder", Check(inPlace, "build command").Detail, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Ok, Check(inPlace, "test command").Status);
        Assert.Empty(Directory.GetDirectories(repo.WorktreeRoot));
    }

    [Fact]
    public async Task Failed_build_shows_error_lines_first_and_saves_the_full_log()
    {
        var log = string.Join('\n', Enumerable.Range(1, 40).Select(i => i == 7 ? "src/App.cs(3,5): error CS1002: ; expected" : $"noise line {i}")) + "\n";
        using var repo = await RepoAsync(new Dictionary<string, string> { ["build-output.txt"] = log });
        Save(repo, new AutopilotConfig { Commands = new CommandsConfig { Build = Shell.PrintFile("build-output.txt") + " && exit 1", Test = "echo tested" } });
        var output = new StringWriter();

        var results = await RunAsync(repo, output: output);

        var build = Check(results, "build command");
        Assert.Equal(CheckStatus.Fail, build.Status);
        var shown = build.Detail.Split('\n').Where(l => l.TrimStart().StartsWith('|')).Select(l => l.Trim().TrimStart('|').Trim()).ToList();
        Assert.Equal("src/App.cs(3,5): error CS1002: ; expected", shown[0]);
        Assert.True(shown.Count <= DoctorCommand.MaxExcerptLines + 1, string.Join('\n', shown));
        Assert.Contains("noise line 40", shown);
        Assert.DoesNotContain("noise line 2", shown);
        var logFile = Path.Combine(repo.Root, ".autopilot", "out", "doctor", "build.log");
        Assert.Contains($"Full log: {logFile}", build.Detail, StringComparison.Ordinal);
        Assert.Contains("noise line 2", File.ReadAllText(logFile), StringComparison.Ordinal);
        Assert.Equal("the build failed", Check(results, "test command").Detail);
        Assert.Contains("Full log:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_test_command_names_its_own_log_file()
    {
        using var repo = await RepoAsync();
        Save(repo, new AutopilotConfig { Commands = new CommandsConfig { Build = "echo built", Test = "echo [FAIL] LoanTests.Limit && exit 1" } });

        var test = Check(await RunAsync(repo), "test command");

        Assert.Equal(CheckStatus.Fail, test.Status);
        Assert.Contains("[FAIL] LoanTests.Limit", test.Detail, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(".autopilot", "out", "doctor", "test.log"), test.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Relevant_lines_put_errors_first_and_skip_zero_counts()
    {
        var output = "Restore done\nBuild started\n  0 Error(s)\nFailed: 0\nerror X1: first\nmore\nerror X1: first\nlast line";

        var lines = DoctorCommand.RelevantLines(output, max: 4);

        Assert.Equal(["error X1: first", "...", "Failed: 0", "more", "last line"], lines);
    }

    [Fact]
    public async Task Base_branch_only_on_the_remote_is_accepted_with_the_branch_command()
    {
        using var repo = await RepoAsync();
        await repo.GitAsync("remote", "add", "origin", "https://example.invalid/shop.git");
        await repo.GitAsync("update-ref", "refs/remotes/origin/develop", await repo.Git.GetCommitAsync("main"));
        Save(repo, new AutopilotConfig { BaseBranch = "develop", Commands = new CommandsConfig { Build = "echo built", Test = "echo tested" } });

        var results = await RunAsync(repo);

        var branch = Check(results, "base branch");
        Assert.Equal(CheckStatus.Warn, branch.Status);
        Assert.Contains("exists only as origin/develop", branch.Detail, StringComparison.Ordinal);
        Assert.Equal("git branch develop origin/develop", branch.Fix);
        Assert.Contains("Fix: git branch develop origin/develop", DoctorCommand.Format(branch), StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Ok, Check(results, "build command").Status);
        Assert.DoesNotContain(results, r => r.Status == CheckStatus.Fail);
    }

    [Fact]
    public async Task Missing_base_branch_fix_mentions_fetch()
    {
        using var repo = await RepoAsync();
        Save(repo, new AutopilotConfig { BaseBranch = "release" });

        var branch = Check(await RunAsync(repo, skipBuild: true), "base branch");

        Assert.Equal(CheckStatus.Fail, branch.Status);
        Assert.Contains("git fetch", branch.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_must_be_committed_to_the_base_branch()
    {
        using var repo = await RepoAsync(new Dictionary<string, string> { ["docs/agent-rules.md"] = "Rule one.\n" });
        repo.Write("docs/draft-rules.md", "not committed\n");
        Save(repo, new AutopilotConfig { Context = ["docs/agent-rules.md", "docs/draft-rules.md"] });

        var failed = Check(await RunAsync(repo, skipBuild: true), "context");
        Save(repo, new AutopilotConfig { Context = ["docs/agent-rules.md"] });
        var ok = Check(await RunAsync(repo, skipBuild: true), "context");
        Save(repo, new AutopilotConfig());
        var none = Check(await RunAsync(repo, skipBuild: true), "context");

        Assert.Equal(CheckStatus.Fail, failed.Status);
        Assert.Contains("docs/draft-rules.md", failed.Detail, StringComparison.Ordinal);
        Assert.Contains("Commit docs/draft-rules.md", failed.Fix, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Ok, ok.Status);
        Assert.StartsWith("1 file(s), 9 characters", ok.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Skip, none.Status);
    }

    [Fact]
    public async Task Empty_writable_paths_fail_the_config_check_with_what_to_fill_in()
    {
        using var repo = await RepoAsync();
        ConfigLoader.Save(new AutopilotConfig { WritablePaths = [] }, repo.Root);

        var config = Check(await RunAsync(repo, skipBuild: true), "config");

        Assert.Equal(CheckStatus.Fail, config.Status);
        Assert.Contains("writablePaths is empty. Fill in", config.Detail, StringComparison.Ordinal);
    }

    private static AutopilotConfig AzureItems(string recordTriage) => new()
    {
        Modules = new ModulesConfig
        {
            WorkItems = new ModuleConfig
            {
                Type = "azure-devops",
                Settings = new Dictionary<string, string>
                {
                    ["organizationUrl"] = "https://dev.azure.com/contoso",
                    ["project"] = "Shop",
                    ["tokenVariable"] = UnsetToken,
                    ["recordTriage"] = recordTriage,
                },
            },
        },
    };

    [Fact]
    public async Task Remote_work_items_warn_about_open_local_task_files_and_show_the_triage_mode()
    {
        Assert.Null(Environment.GetEnvironmentVariable(UnsetToken));
        using var repo = await RepoAsync();
        Save(repo, AzureItems("local"));
        repo.Write(".autopilot/tasks/001-a.md", "# A\nOpen task.\n");
        repo.Write(".autopilot/tasks/002-b.md", "# B\nOpen task.\n");
        repo.Write(".autopilot/tasks/003-c.md", "# C\nStatus: done\n");

        var results = await RunAsync(repo, skipBuild: true);

        var tasks = Check(results, "local tasks");
        Assert.Equal(CheckStatus.Warn, tasks.Status);
        Assert.Equal("2 local task files are not read while workItems is azure-devops", tasks.Detail);
        Assert.Contains("--source local", tasks.Fix, StringComparison.Ordinal);
        var mode = Check(results, "azure devops: triage results");
        Assert.StartsWith("local:", mode.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Fail, Check(results, "azure devops: Azure DevOps token").Status);
    }

    [Fact]
    public async Task Remote_triage_mode_is_printed_and_no_warning_without_local_tasks()
    {
        using var repo = await RepoAsync();
        Save(repo, AzureItems("remote"));

        var results = await RunAsync(repo, skipBuild: true);

        Assert.DoesNotContain(results, r => r.Name == "local tasks");
        Assert.StartsWith("remote:", Check(results, "azure devops: triage results").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Source_local_reads_task_files_while_work_items_are_remote()
    {
        Assert.Null(Environment.GetEnvironmentVariable(UnsetToken));
        using var repo = await RepoAsync();
        Save(repo, AzureItems("remote"));
        repo.Write(".autopilot/tasks/001-hello.md", "# Say hello\nAdd hello.\n");
        var runner = new FakeAgentRunner((_, _) => FakeAgentRunner.Answer(new { label = "needs-human", reason = "Unclear.", acceptanceCriteria = Array.Empty<string>(), risk = "low" }));
        CliContext Context() => new(TextReader.Null, new StringWriter(), new StringWriter())
        {
            RunnerFactory = () => runner,
            WorktreeRoot = repo.WorktreeRoot,
            FactsCollectorFactory = _ => new NoFacts(),
        };

        var local = Context();
        var localExit = await CliApp.RunAsync(["triage", "--source", "local", "--project", repo.Root], local);
        var remote = Context();
        var remoteExit = await CliApp.RunAsync(["triage", "--project", repo.Root], remote);
        var bad = Context();
        var badExit = await CliApp.RunAsync(["run", "--source", "remote", "--project", repo.Root], bad);

        Assert.Equal(0, localExit);
        Assert.Contains("--source local: work items come from .autopilot/tasks/", local.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("001  needs-human", local.Out.ToString(), StringComparison.Ordinal);
        Assert.NotEqual(0, remoteExit);
        Assert.Single(runner.Requests);
        Assert.Equal(2, badExit);
    }

    private sealed class NoFacts : IGateFactsCollector
    {
        public Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct) =>
            throw new InvalidOperationException("not used");
    }
}
