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
        // Compare the listed worktree paths, not the whole text: the porcelain output also holds commit
        // hashes and the temp folder name, which contain "001" by chance about once in a hundred runs.
        var paths = await WorktreePathsAsync(repo);
        Assert.DoesNotContain(paths, p => SamePath(p, worktree) || p.EndsWith(Path.DirectorySeparatorChar + "001", StringComparison.Ordinal));
        Assert.Single(paths);
    }

    /// <summary>The paths in <c>git worktree list --porcelain</c>, one per "worktree " line.</summary>
    internal static async Task<IReadOnlyList<string>> WorktreePathsAsync(TestRepo repo) =>
        (await repo.GitAsync("worktree", "list", "--porcelain"))
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(l => Path.GetFullPath(l["worktree ".Length..]))
            .ToList();

    private static bool SamePath(string a, string b) => string.Equals(
        Path.GetFullPath(a).TrimEnd('\\', '/'),
        Path.GetFullPath(b).TrimEnd('\\', '/'),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [Fact]
    public void Worktree_list_check_ignores_hashes_that_contain_the_id()
    {
        // The old check searched the whole output for "001" and failed when a hash contained it.
        var porcelain = "worktree C:/tmp/repo\nHEAD 3f2a001bc0ffee0000000000000000000000abcd\nbranch refs/heads/main\n";
        var paths = porcelain.Split('\n').Where(l => l.StartsWith("worktree ", StringComparison.Ordinal)).Select(l => l["worktree ".Length..]);

        Assert.Contains("001", porcelain, StringComparison.Ordinal);
        Assert.DoesNotContain(paths, p => p.EndsWith("/001", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolve_branch_prefers_local_then_remote_tracking()
    {
        using var repo = await TestRepo.CreateAsync();
        var commit = await repo.Git.GetCommitAsync("main");
        await repo.GitAsync("remote", "add", "origin", "https://example.invalid/shop.git");
        await repo.GitAsync("update-ref", "refs/remotes/origin/develop", commit);

        var local = await repo.Git.ResolveBranchAsync("main");
        var remote = await repo.Git.ResolveBranchAsync("develop");

        Assert.NotNull(local);
        Assert.False(local.RemoteOnly);
        Assert.Equal("refs/heads/main", local.Ref);
        Assert.Null(local.RemoteNote);
        Assert.NotNull(remote);
        Assert.True(remote.RemoteOnly);
        Assert.Equal("refs/remotes/origin/develop", remote.Ref);
        Assert.Equal(commit, remote.Commit);
        Assert.Contains("git branch develop origin/develop", remote.RemoteNote, StringComparison.Ordinal);
        Assert.Null(await repo.Git.ResolveBranchAsync("nope"));
        Assert.False(await repo.Git.BranchExistsAsync("develop"));
    }

    [Fact]
    public async Task Worktree_can_start_from_a_remote_only_base_branch()
    {
        using var repo = await TestRepo.CreateAsync();
        await repo.GitAsync("update-ref", "refs/remotes/origin/develop", await repo.Git.GetCommitAsync("main"));
        await repo.GitAsync("remote", "add", "origin", "https://example.invalid/shop.git");
        var resolved = await repo.Git.ResolveBranchAsync("develop");
        var path = Path.Combine(repo.WorktreeRoot, "002");

        await repo.Git.AddWorktreeAsync(path, "autopilot/002", resolved!.Commit);
        try
        {
            Assert.True(File.Exists(Path.Combine(path, "README.md")));
        }
        finally
        {
            await repo.Git.RemoveWorktreeAsync(path);
        }

        Assert.True(await repo.Git.BranchExistsAsync("autopilot/002"));
    }

    [Fact]
    public async Task Files_and_text_are_read_from_a_commit_not_the_working_tree()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string>
        {
            ["docs/agent-rules.md"] = "Use the repository layer.\n",
            ["docs/decisions/0002-b.md"] = "B\n",
            ["docs/decisions/0001-a.md"] = "A\n",
            ["docs/agent-rules-old.md"] = "old\n",
        });
        var commit = await repo.Git.GetCommitAsync("main");
        repo.Write("docs/agent-rules.md", "uncommitted edit\n");

        Assert.Equal(["docs/decisions/0001-a.md", "docs/decisions/0002-b.md"], await repo.Git.ListFilesAtAsync(commit, "docs/decisions/"));
        Assert.Equal(["docs/agent-rules.md"], await repo.Git.ListFilesAtAsync(commit, "docs/agent-rules.md"));
        Assert.Empty(await repo.Git.ListFilesAtAsync(commit, "docs/missing.md"));
        Assert.Equal("Use the repository layer.", (await repo.Git.ReadFileAtAsync(commit, "docs/agent-rules.md")).Trim());
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
