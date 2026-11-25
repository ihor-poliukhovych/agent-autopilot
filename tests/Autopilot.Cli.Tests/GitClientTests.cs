using Autopilot.Core.Gate;
using Autopilot.Core.Git;

namespace Autopilot.Cli.Tests;

public sealed class GitClientTests
{
    [Fact]
    public async Task Root_commit_and_branch_queries_work()
    {
        using var repo = await TestRepo.CreateAsync();

        var root = await GitClient.FindRepositoryRootAsync(repo.Root);
        var commit = await repo.Git.GetCommitAsync("main");

        Assert.Equal(Path.GetFullPath(repo.Root), root, ignoreCase: OperatingSystem.IsWindows());
        Assert.Matches("^[0-9a-f]{40}$", commit);
        Assert.True(await repo.Git.BranchExistsAsync("main"));
        Assert.False(await repo.Git.BranchExistsAsync("nope"));
        Assert.Null(await repo.Git.TryGetCommitAsync("nope"));
        Assert.Equal("main", await repo.Git.GetCurrentBranchAsync());
    }

    [Fact]
    public async Task FindRepositoryRoot_outside_a_repo_is_null()
    {
        using var temp = new TempFolder();
        Assert.Null(await GitClient.FindRepositoryRootAsync(temp.Path));
    }

    [Fact]
    public async Task Worktree_commit_and_diff_with_rename()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string>
        {
            ["src/Old.cs"] = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n",
            ["src/Edit.cs"] = "a\nb\nc\n",
            ["src/Gone.cs"] = "x\ny\n",
        });
        var baseCommit = await repo.Git.GetCommitAsync("main");
        var worktree = Path.Combine(repo.WorktreeRoot, "001");

        await repo.Git.AddWorktreeAsync(worktree, "autopilot/001", "main");
        try
        {
            File.Move(Path.Combine(worktree, "src", "Old.cs"), Path.Combine(worktree, "src", "New.cs"));
            File.AppendAllText(Path.Combine(worktree, "src", "New.cs"), "line 21\n");
            File.WriteAllText(Path.Combine(worktree, "src", "Edit.cs"), "a\nB\nc\nd\n");
            File.Delete(Path.Combine(worktree, "src", "Gone.cs"));
            Directory.CreateDirectory(Path.Combine(worktree, "tests"));
            File.WriteAllText(Path.Combine(worktree, "tests", "NewTests.cs"), "t1\nt2\nt3\n");

            var head = await repo.Git.CommitAllAsync(worktree, "001: change things");
            Assert.NotNull(head);
            Assert.Null(await repo.Git.CommitAllAsync(worktree, "nothing"));

            var author = await repo.GitAsync("log", "-1", "--format=%an <%ae>", "autopilot/001");
            Assert.Equal("Agent Autopilot <autopilot@localhost>", author.Trim());

            var changes = (await repo.Git.GetChangesAsync(baseCommit, "autopilot/001")).ToDictionary(c => c.Path);
            Assert.Equal(4, changes.Count);
            Assert.Equal(new FileChange("src/New.cs", FileChangeKind.Renamed, 1, 0, "src/Old.cs"), changes["src/New.cs"]);
            Assert.Equal(new FileChange("src/Edit.cs", FileChangeKind.Modified, 2, 1), changes["src/Edit.cs"]);
            Assert.Equal(new FileChange("src/Gone.cs", FileChangeKind.Deleted, 0, 2), changes["src/Gone.cs"]);
            Assert.Equal(new FileChange("tests/NewTests.cs", FileChangeKind.Added, 3, 0), changes["tests/NewTests.cs"]);

            var diff = await repo.Git.GetDiffAsync(baseCommit, "autopilot/001");
            Assert.Contains("rename from src/Old.cs", diff);
            Assert.Contains("+t2", diff);
        }
        finally
        {
            await repo.Git.RemoveWorktreeAsync(worktree);
        }

        Assert.False(Directory.Exists(worktree));
        Assert.True(await repo.Git.BranchExistsAsync("autopilot/001"));
        var list = await repo.GitAsync("worktree", "list", "--porcelain");
        Assert.DoesNotContain("001", list);
    }

    [Fact]
    public async Task Detached_worktree_checks_out_a_commit()
    {
        using var repo = await TestRepo.CreateAsync();
        var commit = await repo.Git.GetCommitAsync("main");
        var path = Path.Combine(repo.WorktreeRoot, "detached");

        await repo.Git.AddDetachedWorktreeAsync(path, commit);
        Assert.True(File.Exists(Path.Combine(path, "README.md")));
        await repo.Git.RemoveWorktreeAsync(path);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void ParseChanges_handles_binary_and_copy()
    {
        var numstat = "-\t-\timg.png\0" + "5\t0\t\0a.cs\0b.cs\0";
        var nameStatus = "A\0img.png\0C090\0a.cs\0b.cs\0";

        var changes = GitClient.ParseChanges(numstat, nameStatus);

        Assert.Equal(
            [new FileChange("img.png", FileChangeKind.Added, 0, 0, Binary: true), new FileChange("b.cs", FileChangeKind.Added, 5, 0)],
            changes);
    }

    [Fact]
    public void Default_worktree_root_is_outside_the_project_and_stable()
    {
        var a = GitClient.DefaultWorktreeRoot(Path.Combine(Path.GetTempPath(), "my-repo"));
        var b = GitClient.DefaultWorktreeRoot(Path.Combine(Path.GetTempPath(), "my-repo"));
        var other = GitClient.DefaultWorktreeRoot(Path.Combine(Path.GetTempPath(), "x", "my-repo"));

        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
        Assert.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agent-autopilot", "worktrees"), a);
        Assert.Matches(@"my-repo-[0-9a-f]{8}$", a);
    }
}
