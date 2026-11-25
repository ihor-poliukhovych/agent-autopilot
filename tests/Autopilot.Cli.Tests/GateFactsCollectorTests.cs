using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;

namespace Autopilot.Cli.Tests;

public sealed class GateFactsCollectorTests
{
    private static readonly Evidence NoEvidence = new("001", null, null, null);

    [Fact]
    public async Task Runs_build_and_tests_on_clean_checkouts_of_base_and_branch()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string>
        {
            ["summary.txt"] = "Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3\n",
        });
        var baseCommit = await repo.Git.GetCommitAsync("main");
        var agentWorktree = Path.Combine(repo.WorktreeRoot, "001");
        await repo.Git.AddWorktreeAsync(agentWorktree, "autopilot/001", "main");
        File.WriteAllText(Path.Combine(agentWorktree, "summary.txt"), "Passed!  - Failed: 0, Passed: 4, Skipped: 1, Total: 5\n");
        var head = await repo.Git.CommitAllAsync(agentWorktree, "change");
        // Uncommitted work in the agent's worktree must not reach the gate.
        File.WriteAllText(Path.Combine(agentWorktree, "summary.txt"), "Passed!  - Failed: 0, Passed: 99, Skipped: 0, Total: 99\n");

        var config = new AutopilotConfig { Commands = new CommandsConfig { Build = "echo built", Test = Shell.PrintFile("summary.txt") } };
        var collector = new GateFactsCollector(repo.Git, config, repo.WorktreeRoot);

        var input = await collector.CollectAsync("001", baseCommit, head!, NoEvidence, CancellationToken.None);
        await repo.Git.RemoveWorktreeAsync(agentWorktree);

        Assert.True(input.Tests.BuildSucceeded);
        Assert.True(input.Tests.TestsSucceeded);
        Assert.Equal(3, input.Tests.TestCountBefore);
        Assert.Equal(5, input.Tests.TestCountAfter);
        Assert.Equal(1, input.Tests.SkipsAdded);
        Assert.Contains("built", input.Tests.Log);
        Assert.Equal(new FileChange("summary.txt", FileChangeKind.Modified, 1, 1), Assert.Single(input.Changes));
        Assert.Same(NoEvidence, input.Evidence);
        Assert.Empty(Directory.GetDirectories(repo.WorktreeRoot));
    }

    [Fact]
    public async Task Failed_build_skips_tests()
    {
        using var repo = await TestRepo.CreateAsync();
        var commit = await repo.Git.GetCommitAsync("main");
        var config = new AutopilotConfig { Commands = new CommandsConfig { Build = "exit 3", Test = "echo should not run" } };

        var input = await new GateFactsCollector(repo.Git, config, repo.WorktreeRoot).CollectAsync("001", commit, commit, NoEvidence, CancellationToken.None);

        Assert.False(input.Tests.BuildSucceeded);
        Assert.False(input.Tests.TestsSucceeded);
        Assert.Equal(0, input.Tests.TestCountAfter);
        Assert.DoesNotContain("should not run", input.Tests.Log);
        Assert.Contains("exit code 3", input.Tests.Log);
    }

    [Fact]
    public async Task Skips_added_never_goes_below_zero()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string>
        {
            ["summary.txt"] = "Test summary: total: 4, failed: 0, succeeded: 2, skipped: 2, duration: 1s\n",
        });
        var baseCommit = await repo.Git.GetCommitAsync("main");
        repo.Write("summary.txt", "Test summary: total: 4, failed: 1, succeeded: 3, skipped: 0, duration: 1s\n");
        var head = await repo.Git.CommitAllAsync(repo.Root, "unskip");
        var config = new AutopilotConfig { Commands = new CommandsConfig { Build = "echo ok", Test = Shell.PrintFile("summary.txt") } };

        var input = await new GateFactsCollector(repo.Git, config, repo.WorktreeRoot).CollectAsync("001", baseCommit, head!, NoEvidence, CancellationToken.None);

        Assert.Equal(0, input.Tests.SkipsAdded);
        Assert.Equal(4, input.Tests.TestCountBefore);
        Assert.Equal(4, input.Tests.TestCountAfter);
    }
}
