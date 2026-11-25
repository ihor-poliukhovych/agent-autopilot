using Autopilot.Core.Config;

namespace Autopilot.Cli.Tests;

public sealed class InitCommandTests
{
    private static async Task<(int Exit, string Out, string Err)> RunCliAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CliApp.RunAsync(args, new CliContext(TextReader.Null, output, error));
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Init_creates_files_adapted_to_the_project()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(temp.Combine("src"));
        Directory.CreateDirectory(temp.Combine("tests"));
        temp.Write("Library.slnx", "<Solution />");
        temp.Write(".gitignore", "bin/\nobj/");

        var (exit, output, _) = await RunCliAsync("init", "--project", temp.Path);

        Assert.Equal(0, exit);
        Assert.Contains("autopilot doctor", output);
        var config = ConfigLoader.Load(temp.Path);
        Assert.Equal("dotnet build \"Library.slnx\"", config.Commands.Build);
        Assert.Equal("dotnet test \"Library.slnx\"", config.Commands.Test);
        Assert.Equal(["src/**", "tests/**"], config.WritablePaths);
        Assert.Empty(ConfigLoader.Validate(config));
        Assert.True(File.Exists(temp.Combine(".autopilot", "README.md")));
        Assert.True(File.Exists(temp.Combine(".autopilot", "tasks", "001-example.md")));
        Assert.Equal("bin/\nobj/\n# Agent Autopilot run results\n.autopilot/out/\n", File.ReadAllText(temp.Combine(".gitignore")).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Init_without_src_or_tests_makes_everything_writable()
    {
        using var temp = new TempFolder();

        var (exit, _, _) = await RunCliAsync("init", "--project", temp.Path);

        Assert.Equal(0, exit);
        var config = ConfigLoader.Load(temp.Path);
        Assert.Equal(["**"], config.WritablePaths);
        Assert.Equal("dotnet build", config.Commands.Build);
        Assert.Empty(ConfigLoader.Validate(config));
        Assert.Contains(".autopilot/out/", File.ReadAllText(temp.Combine(".gitignore")));
    }

    [Fact]
    public async Task Init_never_overwrites_without_force_and_edits_gitignore_once()
    {
        using var temp = new TempFolder();
        Assert.Equal(0, (await RunCliAsync("init", "--project", temp.Path)).Exit);
        var configPath = ConfigLoader.ConfigPath(temp.Path);
        File.WriteAllText(configPath, "{ \"baseBranch\": \"mine\" }");
        File.WriteAllText(temp.Combine(".autopilot", "tasks", "001-example.md"), "# my own task");

        var (exit, _, error) = await RunCliAsync("init", "--project", temp.Path);

        Assert.Equal(1, exit);
        Assert.Contains("--force", error);
        Assert.Equal("{ \"baseBranch\": \"mine\" }", File.ReadAllText(configPath));

        Assert.Equal(0, (await RunCliAsync("init", "--force", "--project", temp.Path)).Exit);
        Assert.Equal("main", ConfigLoader.Load(temp.Path).BaseBranch);

        var gitignore = File.ReadAllText(temp.Combine(".gitignore"));
        Assert.Single(gitignore.Split('\n'), l => l.Trim() == ".autopilot/out/");
    }

    [Fact]
    public async Task Init_uses_master_when_the_repo_has_no_main()
    {
        using var repo = await TestRepo.CreateAsync();
        await repo.GitAsync("branch", "-m", "main", "master");

        Assert.Equal(0, (await RunCliAsync("init", "--project", repo.Root)).Exit);
        Assert.Equal("master", ConfigLoader.Load(repo.Root).BaseBranch);
    }

    [Fact]
    public async Task Missing_project_folder_is_a_usage_error()
    {
        var (exit, _, error) = await RunCliAsync("init", "--project", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Equal(2, exit);
        Assert.Contains("does not exist", error);
    }
}
