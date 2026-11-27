using Autopilot.Core.Process;

namespace Autopilot.AzureDevOps.Tests;

public sealed class AzureCliLauncherTests
{
    private static ProcessResult Failed(string stdout, string stderr) => new(1, stdout, stderr, stdout + stderr, false, TimeSpan.Zero);

    [Fact]
    public void Resolve_returns_the_full_path_of_az_cmd_from_the_first_matching_folder()
    {
        var found = AzureCliLauncher.Resolve(true, @"C:\Windows;""C:\Tools\wbin"";C:\Other", p => p == @"C:\Tools\wbin\az.cmd" || p == @"C:\Other\az.cmd");

        Assert.Equal(@"C:\Tools\wbin\az.cmd", found);
    }

    [Fact]
    public void Resolve_returns_null_when_az_is_not_on_path()
    {
        Assert.Null(AzureCliLauncher.Resolve(false, "/usr/bin:/bin", _ => false));
        Assert.Null(AzureCliLauncher.Resolve(true, null, _ => true));
    }

    [Fact]
    public void Build_sets_quiet_errors_and_removes_python_variables_for_the_child()
    {
        var launch = AzureCliLauncher.Build(@"C:\Tools\wbin\az.cmd");

        Assert.Equal(@"C:\Tools\wbin\az.cmd", launch.FileName);
        Assert.Equal("true", launch.Environment["AZURE_CORE_ONLY_SHOW_ERRORS"]);
        Assert.Null(launch.Environment["PYTHONHOME"]);
        Assert.Null(launch.Environment["PYTHONPATH"]);
    }

    [Fact]
    public async Task A_failed_launch_names_the_first_line_of_the_error_and_never_the_output_of_a_success()
    {
        var provider = new AzureCliTokenProvider((_, _) => Task.FromResult(Failed(string.Empty, "ERROR: Please run 'az login'\nmore text\n")));

        var error = await Assert.ThrowsAsync<AzureDevOpsAuthException>(() => provider.GetCredentialAsync(default));

        Assert.Contains("exit code 1", error.Message);
        Assert.Contains("ERROR: Please run 'az login'", error.Message);
        Assert.DoesNotContain("more text", error.Message);
    }

    [Fact]
    public async Task When_stderr_is_empty_the_first_line_of_a_failed_stdout_is_used()
    {
        var provider = new AzureCliTokenProvider((_, _) => Task.FromResult(Failed("Failed to load python executable.\r\n", string.Empty)));

        var error = await Assert.ThrowsAsync<AzureDevOpsAuthException>(() => provider.GetCredentialAsync(default));

        Assert.Contains("Failed to load python executable.", error.Message);
    }
}
