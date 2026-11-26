using DevAutopilot.Core.Process;

namespace DevAutopilot.AzureDevOps.Tests;

/// <summary>Real git pushes to a temporary bare repository. A local remote ignores http headers, so a dummy token is safe.</summary>
public sealed class GitPusherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ado-push-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _work;
    private readonly string _bare;

    public GitPusherTests()
    {
        _work = Path.Combine(_root, "work");
        _bare = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(_work);
    }

    private async Task SetUpAsync()
    {
        await Git(_root, "init", "--bare", "--quiet", _bare);
        await Git(_work, "init", "--quiet", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(_work, "README.md"), "hello\n");
        await Git(_work, "add", "README.md");
        await Git(_work, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "init");
        await Git(_work, "branch", "autopilot/42");
        await Git(_work, "remote", "add", "origin", _bare);
    }

    [Fact]
    public async Task Pushes_the_agent_branch_and_stores_no_token()
    {
        await SetUpAsync();
        var credential = AzureDevOpsCredential.FromPat(Ado.Token);

        await new GitPusher(new Uri(Ado.Org)).PushAsync(_work, "origin", "autopilot/42", "autopilot/", credential, default);

        var remoteBranches = await Git(_bare, "branch", "--list");
        Assert.Contains("autopilot/42", remoteBranches, StringComparison.Ordinal);
        Assert.DoesNotContain("main", remoteBranches, StringComparison.Ordinal);
        foreach (var config in new[] { Path.Combine(_work, ".git", "config"), Path.Combine(_bare, "config") })
        {
            var text = await File.ReadAllTextAsync(config);
            Assert.DoesNotContain(Ado.Token, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Ado.TokenBase64, text, StringComparison.Ordinal);
            Assert.DoesNotContain("extraHeader", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Refuses_to_push_the_base_branch()
    {
        await SetUpAsync();

        var ex = await Assert.ThrowsAsync<AzureDevOpsPublishException>(() =>
            new GitPusher(new Uri(Ado.Org)).PushAsync(_work, "origin", "main", "autopilot/", AzureDevOpsCredential.FromPat(Ado.Token), default));

        Assert.Contains("Refusing to push 'main'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("main", await Git(_bare, "branch", "--list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_diverged_remote_branch_is_not_overwritten()
    {
        await SetUpAsync();
        var pusher = new GitPusher(new Uri(Ado.Org));
        var credential = AzureDevOpsCredential.FromPat(Ado.Token);
        await pusher.PushAsync(_work, "origin", "autopilot/42", "autopilot/", credential, default);
        var pushed = (await Git(_bare, "rev-parse", "autopilot/42")).Trim();

        // Rewrite the local branch so a push would need --force.
        await Git(_work, "checkout", "--quiet", "autopilot/42");
        await File.WriteAllTextAsync(Path.Combine(_work, "README.md"), "changed\n");
        await Git(_work, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "--amend", "-am", "rewritten");

        var ex = await Assert.ThrowsAsync<AzureDevOpsPublishException>(() =>
            pusher.PushAsync(_work, "origin", "autopilot/42", "autopilot/", credential, default));

        Assert.Contains("git push", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Ado.Token, ex.Message, StringComparison.Ordinal);
        Assert.Equal(pushed, (await Git(_bare, "rev-parse", "autopilot/42")).Trim());
    }

    [Fact]
    public async Task Missing_remote_is_explained()
    {
        await SetUpAsync();

        var ex = await Assert.ThrowsAsync<AzureDevOpsPublishException>(() =>
            new GitPusher(new Uri(Ado.Org)).PushAsync(_work, "upstream", "autopilot/42", "autopilot/", AzureDevOpsCredential.FromPat(Ado.Token), default));

        Assert.Contains("remote 'upstream' does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://dev.azure.com/contoso/Shop/_git/shop-api", true)]
    [InlineData("https://contoso@dev.azure.com/contoso/Shop/_git/shop-api", true)]
    [InlineData("git@ssh.dev.azure.com:v3/contoso/Shop/shop-api", true)]
    [InlineData("C:/repos/shop.git", true)]
    [InlineData("https://example.com/contoso/shop-api.git", false)]
    [InlineData("http://dev.azure.com/contoso/Shop/_git/shop-api", false)]
    public void Token_only_goes_to_the_organisation_host(string remoteUrl, bool allowed)
    {
        var check = () => GitPusher.EnsureRemoteHost(remoteUrl, new Uri(Ado.Org));
        if (allowed)
        {
            check();
        }
        else
        {
            Assert.Throws<AzureDevOpsPublishException>(check);
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Temp folder; left for the OS to clean.
        }
    }

    private static async Task<string> Git(string directory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, directory, TimeSpan.FromMinutes(1),
            new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" });
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)} failed: {result.StandardError}");
        return result.StandardOutput;
    }
}
