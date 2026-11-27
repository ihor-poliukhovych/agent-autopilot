using Autopilot.Core.Pool;

namespace Autopilot.Cli.Tests;

public sealed class RunLeftoversTests
{
    [Fact]
    public async Task A_branch_with_nothing_new_and_its_worktree_are_removed()
    {
        using var repo = await TestRepo.CreateAsync();
        var worktree = Path.Combine(repo.WorktreeRoot, "7");
        await repo.Git.AddWorktreeAsync(worktree, "autopilot/7", "main"); // a run that died right after it started

        var said = await new RunLeftovers(repo.Git, "autopilot/", () => ["main"]).ReleaseAsync("7", default);

        Assert.Contains("Removed the branch autopilot/7", said, StringComparison.Ordinal);
        Assert.False(await repo.Git.BranchExistsAsync("autopilot/7"));
        Assert.False(Directory.Exists(worktree));
        await repo.Git.AddWorktreeAsync(worktree, "autopilot/7", "main"); // the next run can start
    }

    [Fact]
    public async Task A_branch_with_work_is_kept_under_a_new_name_and_the_name_is_free_again()
    {
        using var repo = await TestRepo.CreateAsync();
        var worktree = Path.Combine(repo.WorktreeRoot, "7");
        await repo.Git.AddWorktreeAsync(worktree, "autopilot/7", "main");
        File.WriteAllText(Path.Combine(worktree, "work.txt"), "half done\n");
        await repo.Git.CommitAllAsync(worktree, "7: half done");

        var said = await new RunLeftovers(repo.Git, "autopilot/", () => ["main"]).ReleaseAsync("7", default);

        Assert.Contains("autopilot/7-attempt-1", said, StringComparison.Ordinal);
        Assert.False(await repo.Git.BranchExistsAsync("autopilot/7"));
        Assert.True(await repo.Git.BranchExistsAsync("autopilot/7-attempt-1"));
        Assert.False(Directory.Exists(worktree));

        // A second dead run on the same task does not overwrite the first one's work.
        await repo.Git.AddWorktreeAsync(worktree, "autopilot/7", "main");
        File.WriteAllText(Path.Combine(worktree, "other.txt"), "again\n");
        await repo.Git.CommitAllAsync(worktree, "7: again");
        await new RunLeftovers(repo.Git, "autopilot/", () => ["main"]).ReleaseAsync("7", default);
        Assert.True(await repo.Git.BranchExistsAsync("autopilot/7-attempt-2"));
    }

    [Fact]
    public async Task Nothing_left_over_means_nothing_to_do()
    {
        using var repo = await TestRepo.CreateAsync();

        Assert.Null(await new RunLeftovers(repo.Git, "autopilot/", () => ["main"]).ReleaseAsync("7", default));
    }
}
