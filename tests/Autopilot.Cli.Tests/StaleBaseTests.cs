using Autopilot.Cli.Commands;
using Autopilot.Core.Config;

namespace Autopilot.Cli.Tests;

/// <summary>A local base branch that is behind origin/&lt;base&gt; is reported, never silently used and never changed.</summary>
public sealed class StaleBaseTests
{
    /// <summary>A repo whose origin/main is <paramref name="ahead"/> commits ahead of the local main.</summary>
    private static async Task<TestRepo> RepoAsync(int? ahead)
    {
        var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { [".gitignore"] = ".autopilot/out/\n", ["src/App.cs"] = "class App { }\n" });
        if (ahead is { } n)
        {
            for (var i = 1; i <= n; i++)
            {
                File.WriteAllText(Path.Combine(repo.Root, "src", $"F{i}.cs"), $"class F{i} {{ }}\n");
                await repo.GitAsync("add", "--all");
                await repo.GitAsync("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", $"c{i}");
            }

            await repo.GitAsync("update-ref", "refs/remotes/origin/main", "HEAD");
            if (n > 0)
            {
                await repo.GitAsync("reset", "--hard", $"HEAD~{n}");
            }
        }

        ConfigLoader.Save(new AutopilotConfig { WritablePaths = ["src/**"] }, repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, ".autopilot", "tasks"));
        return repo;
    }

    private static Task<IReadOnlyList<CheckResult>> DoctorAsync(TestRepo repo) =>
        DoctorCommand.RunChecksAsync(repo.Root, new DoctorOptions { SkipBuild = true, CheckTools = false, WorktreeRoot = repo.WorktreeRoot },
            TextWriter.Null, CancellationToken.None);

    [Fact]
    public async Task Behind_count_is_the_number_of_origin_commits_the_local_branch_lacks()
    {
        using var repo = await RepoAsync(3);
        Assert.Equal(3, await repo.Git.GetCommitsBehindOriginAsync("main"));
    }

    [Fact]
    public async Task Behind_count_is_zero_when_up_to_date_and_null_without_a_remote_ref_or_branch()
    {
        using (var upToDate = await RepoAsync(0))
        {
            Assert.Equal(0, await upToDate.Git.GetCommitsBehindOriginAsync("main"));
        }

        using var noRemote = await RepoAsync(null);
        Assert.Null(await noRemote.Git.GetCommitsBehindOriginAsync("main"));
        Assert.Null(await noRemote.Git.GetCommitsBehindOriginAsync("no-such-branch"));
        Assert.Null(await noRemote.Git.GetCommitsBehindOriginAsync(""));
    }

    [Fact]
    public async Task Doctor_warns_with_the_count_and_commit_when_the_base_is_behind()
    {
        using var repo = await RepoAsync(2);
        var localCommit = await repo.Git.GetCommitAsync("main");

        var results = await DoctorAsync(repo);

        var warn = results.Single(r => r.Name == "base freshness");
        Assert.Equal(CheckStatus.Warn, warn.Status);
        Assert.Contains("main is 2 commits behind origin/main", warn.Detail);
        Assert.Contains(localCommit[..12], warn.Detail);
        Assert.Equal(CheckStatus.Ok, results.Single(r => r.Name == "base branch").Status);
        Assert.DoesNotContain(results, r => r.Status == CheckStatus.Fail);
        Assert.Equal(localCommit, await repo.Git.GetCommitAsync("main")); // nothing was changed
    }

    [Fact]
    public async Task Doctor_has_no_freshness_warning_when_up_to_date_or_without_a_remote()
    {
        using (var upToDate = await RepoAsync(0))
        {
            Assert.DoesNotContain(await DoctorAsync(upToDate), r => r.Name == "base freshness");
        }

        using var noRemote = await RepoAsync(null);
        var results = await DoctorAsync(noRemote);
        Assert.DoesNotContain(results, r => r.Name == "base freshness");
        Assert.DoesNotContain(results, r => r.Status == CheckStatus.Fail);
    }
}
