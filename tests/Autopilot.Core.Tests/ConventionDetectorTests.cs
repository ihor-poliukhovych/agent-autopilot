using Autopilot.Core.Analysis;

namespace Autopilot.Core.Tests;

public sealed class ConventionDetectorTests
{
    private static ConventionInput Input(
        string[]? branches = null, string[]? commits = null, string[]? prs = null, string[]? files = null,
        string? defaultBranch = null, string? remote = null, string? packageJson = null) =>
        new(branches ?? [], commits ?? [], prs, files ?? [], defaultBranch, remote, packageJson);

    private static AnalyzerFolder Remote(AnalyzerFolder repo, params string[] branches)
    {
        foreach (var b in branches)
        {
            repo.Git("update-ref", "refs/remotes/origin/" + b, "HEAD");
        }

        return repo;
    }

    [Fact]
    public void Branch_pattern_reports_the_share_of_branches_that_follow_it()
    {
        var p = ConventionDetector.Detect(Input(
            branches: ["main", "task/task-1", "task/task-2", "task/task-3", "task/task-4", "hotfix"],
            commits: ["x"]));

        Assert.Equal("task/task-<id>", p.BranchPattern.Value);
        Assert.Equal(0.8, p.BranchPattern.Share);
        Assert.Equal(3, p.BranchPattern.Examples.Count);
        Assert.InRange(p.BranchPattern.Confidence, 0.0, 1.0);
    }

    [Fact]
    public void Commit_pattern_tells_hash_id_conventional_and_plain_apart()
    {
        Assert.Equal("#<id> <subject>", Commit("#12 add login", "#13 fix it", "#14 more"));
        Assert.Equal("conventional", Commit("feat: add login", "fix(api): crash", "chore: deps"));
        Assert.Equal("plain", Commit("Add login", "Fix the crash", "Update deps"));
    }

    private static string Commit(params string[] subjects) =>
        ConventionDetector.Detect(Input(commits: subjects)).CommitPattern.Value;

    [Fact]
    public void Findings_have_at_most_three_examples_and_confidence_between_zero_and_one()
    {
        var p = ConventionDetector.Detect(Input(
            branches: ["main", "a/1", "a/2", "a/3", "a/4", "a/5"],
            commits: ["#1 a", "#2 b", "#3 c", "#4 d", "#5 e"],
            prs: ["#1 a", "#2 b", "#3 c", "#4 d"],
            files: ["App.slnx", "src/a.cs", "tests/a.cs", ".github/ci.yml"]));

        foreach (var f in new[] { p.BaseBranch, p.BranchPattern, p.CommitPattern, p.PullRequestTitlePattern, p.TaskIdPattern, p.TaskTracker, p.BuildCommand, p.TestCommand, p.WritablePaths, p.RiskyPaths })
        {
            Assert.True(f.Examples.Count <= 3);
            Assert.InRange(f.Confidence, 0.0, 1.0);
        }

        Assert.Equal("#<id> <subject>", p.PullRequestTitlePattern.Value);
        Assert.Equal("#<number>", p.TaskIdPattern.Value);
        Assert.Equal("src/**, tests/**", p.WritablePaths.Value);
        Assert.Equal(".github/**", p.RiskyPaths.Value);
    }

    [Fact]
    public void Task_id_and_tracker_are_read_from_keys()
    {
        var jira = ConventionDetector.Detect(Input(commits: ["ABC-12 a", "ABC-13 b", "ABC-14 c"]));
        Assert.Equal("<KEY>-<number>", jira.TaskIdPattern.Value);
        Assert.Equal("jira", jira.TaskTracker.Value);

        var ado = ConventionDetector.Detect(Input(commits: ["AB#1 a", "AB#2 b", "AB#3 c"]));
        Assert.Equal("azure-devops", ado.TaskTracker.Value);
    }

    [Fact]
    public void Dotnet_commands_come_from_the_solution_file()
    {
        var p = ConventionDetector.Detect(Input(commits: ["x"], files: ["src/A/A.csproj", "My.slnx"]));
        Assert.Equal("dotnet build \"My.slnx\"", p.BuildCommand.Value);
        Assert.Equal("dotnet test \"My.slnx\"", p.TestCommand.Value);
    }

    [Fact]
    public void Node_commands_come_from_package_json_scripts()
    {
        var p = ConventionDetector.Detect(Input(commits: ["x"], files: ["package.json"],
            packageJson: "{\"scripts\":{\"build\":\"tsc\",\"test\":\"jest\"}}"));
        Assert.Equal("npm run build", p.BuildCommand.Value);
        Assert.Equal("npm test", p.TestCommand.Value);

        var none = ConventionDetector.Detect(Input(commits: ["x"], packageJson: "{\"scripts\":{}}"));
        Assert.Equal(string.Empty, none.BuildCommand.Value);
    }

    [Fact]
    public void No_commits_means_no_history_and_defaults()
    {
        var p = ConventionDetector.Detect(Input(branches: ["main"]));
        Assert.False(p.HasHistory);
        Assert.NotNull(p.Note);
        Assert.Equal(ConventionDetector.DefaultBranchPattern, p.BranchPattern.Value);
        Assert.Equal(ConventionDetector.DefaultCommitPattern, p.CommitPattern.Value);
        Assert.Equal(0, p.BranchPattern.Confidence);
    }

    [Fact]
    public async Task Git_repo_with_task_branches_and_hash_commits()
    {
        using var repo = new AnalyzerFolder().File("App.slnx", "<Solution/>").File("src/a.cs", "x");
        repo.Git("init", "-q", "-b", "main").Git("add", "-A").Git("commit", "-q", "-m", "#1 first");
        repo.Git("commit", "-q", "--allow-empty", "-m", "#2 second").Git("commit", "-q", "--allow-empty", "-m", "#3 third");
        Remote(repo, "main", "task/task-1", "task/task-2", "task/task-3", "task/task-4", "misc");
        repo.Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");

        var p = ConventionDetector.Detect(await ConventionGitReader.ReadAsync(repo.Root));

        Assert.True(p.HasHistory);
        Assert.Equal("main", p.BaseBranch.Value);
        Assert.Equal("task/task-<id>", p.BranchPattern.Value);
        Assert.Equal(0.8, p.BranchPattern.Share);
        Assert.Equal("#<id> <subject>", p.CommitPattern.Value);
        Assert.Equal("dotnet build \"App.slnx\"", p.BuildCommand.Value);
    }

    [Fact]
    public async Task Git_repo_with_conventional_commits_and_jira_branches()
    {
        using var repo = new AnalyzerFolder().File("package.json", "{\"scripts\":{\"build\":\"vite build\",\"test\":\"vitest\"}}");
        repo.Git("init", "-q", "-b", "master").Git("add", "-A").Git("commit", "-q", "-m", "feat: start");
        repo.Git("commit", "-q", "--allow-empty", "-m", "fix(ui): crash").Git("commit", "-q", "--allow-empty", "-m", "chore: deps");
        Remote(repo, "master", "feature/ABC-1-login", "feature/ABC-2-logout", "feature/ABC-3-profile");

        var p = ConventionDetector.Detect(await ConventionGitReader.ReadAsync(
            repo.Root, _ => Task.FromResult<IReadOnlyList<string>>(["feat: login ABC-1", "fix: logout ABC-2"])));

        Assert.Equal("master", p.BaseBranch.Value);
        Assert.Equal("feature/ABC-<id>-<slug>", p.BranchPattern.Value);
        Assert.Equal("conventional", p.CommitPattern.Value);
        Assert.Equal("conventional", p.PullRequestTitlePattern.Value);
        Assert.Equal("jira", p.TaskTracker.Value);
        Assert.Equal("npm run build", p.BuildCommand.Value);
    }

    [Fact]
    public async Task Git_repo_with_plain_commits_and_a_failing_pr_lister_skips_pr_titles()
    {
        using var repo = new AnalyzerFolder().File("a.txt", "x");
        repo.Git("init", "-q", "-b", "main").Git("add", "-A").Git("commit", "-q", "-m", "Add the login page");
        repo.Git("commit", "-q", "--allow-empty", "-m", "Update the readme");
        Remote(repo, "main", "login-page", "readme-update");

        var input = await ConventionGitReader.ReadAsync(repo.Root, _ => throw new InvalidOperationException("no tracker"));
        var p = ConventionDetector.Detect(input);

        Assert.Null(input.PullRequestTitles);
        Assert.Equal("plain", p.CommitPattern.Value);
        Assert.Equal("<slug>", p.BranchPattern.Value);
        Assert.Equal(ConventionDetector.DefaultPrTitlePattern, p.PullRequestTitlePattern.Value);
    }

    [Fact]
    public async Task Git_repo_with_no_history_offers_the_defaults()
    {
        using var repo = new AnalyzerFolder().File("package.json", "{\"scripts\":{\"test\":\"jest\"}}");
        repo.Git("init", "-q", "-b", "main");

        var p = ConventionDetector.Detect(await ConventionGitReader.ReadAsync(repo.Root));

        Assert.False(p.HasHistory);
        Assert.Equal("main", p.BaseBranch.Value);
        Assert.Equal(ConventionDetector.DefaultBranchPattern, p.BranchPattern.Value);
        Assert.Equal("npm test", p.TestCommand.Value);
    }
}
