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
    public async Task Init_without_src_tests_or_projects_writes_an_empty_list_and_says_to_fill_it_in()
    {
        using var temp = new TempFolder();

        var (exit, output, _) = await RunCliAsync("init", "--project", temp.Path);

        Assert.Equal(0, exit);
        var config = ConfigLoader.Load(temp.Path);
        Assert.Empty(config.WritablePaths);
        Assert.DoesNotContain("\"**\"", File.ReadAllText(ConfigLoader.ConfigPath(temp.Path)), StringComparison.Ordinal);
        Assert.Equal("dotnet build", config.Commands.Build);
        Assert.Contains(ConfigLoader.Validate(config), p => p.StartsWith("writablePaths is empty. Fill in", StringComparison.Ordinal));
        Assert.Contains("Fill in writablePaths", output, StringComparison.Ordinal);
        Assert.Contains(".autopilot/out/", File.ReadAllText(temp.Combine(".gitignore")));
    }

    [Fact]
    public async Task Init_without_src_or_tests_lists_the_project_folders()
    {
        using var temp = new TempFolder();
        temp.Write("Shop.Api/Shop.Api.csproj", "<Project />");
        temp.Write("Shop.Api/Nested/Tool/Tool.csproj", "<Project />");
        temp.Write("libs/Shop.Core/Shop.Core.csproj", "<Project />");
        temp.Write("Shop.Api.Tests/Shop.Api.Tests.csproj", "<Project />");
        temp.Write("Shop.Api/bin/Debug/Copy.csproj", "<Project />");
        temp.Write(".tools/Hidden/Hidden.csproj", "<Project />");
        temp.Write("Root.csproj", "<Project />");

        Assert.Equal(0, (await RunCliAsync("init", "--project", temp.Path)).Exit);

        var config = ConfigLoader.Load(temp.Path);
        Assert.Equal(["libs/Shop.Core/**", "Shop.Api/**", "Shop.Api.Tests/**"], config.WritablePaths);
        Assert.Empty(ConfigLoader.Validate(config));
    }

    [Fact]
    public async Task Init_takes_the_base_branch_from_the_option_and_says_how_to_change_it()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["src/App.cs"] = "class App { }\n" });
        await repo.GitAsync("branch", "develop");

        var (exit, output, _) = await RunCliAsync("init", "--base-branch", "develop", "--project", repo.Root);

        Assert.Equal(0, exit);
        Assert.Equal("develop", ConfigLoader.Load(repo.Root).BaseBranch);
        Assert.Contains("Base branch:   develop (from --base-branch)", output, StringComparison.Ordinal);
        Assert.Contains("autopilot init --force --base-branch <name>", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_keeps_the_default_branch_and_prints_how_it_was_chosen()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["src/App.cs"] = "class App { }\n" });
        await repo.GitAsync("checkout", "-q", "-b", "feature/x");

        var (_, output, _) = await RunCliAsync("init", "--project", repo.Root);

        Assert.Equal("main", ConfigLoader.Load(repo.Root).BaseBranch);
        Assert.Contains("Base branch:   main (detected: main, else master, else the current branch)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_finds_a_main_branch_that_exists_only_on_the_remote()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["src/App.cs"] = "class App { }\n" });
        await repo.GitAsync("remote", "add", "origin", "https://example.invalid/shop.git");
        await repo.GitAsync("update-ref", "refs/remotes/origin/main", await repo.Git.GetCommitAsync("main"));
        await repo.GitAsync("checkout", "-q", "-b", "feature/x");
        await repo.GitAsync("branch", "-D", "main");

        var (_, output, _) = await RunCliAsync("init", "--project", repo.Root);

        Assert.Equal("main", ConfigLoader.Load(repo.Root).BaseBranch);
        Assert.Contains("only origin/main exists", output, StringComparison.Ordinal);
        Assert.Contains("git branch main origin/main", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_rejects_an_empty_base_branch()
    {
        using var temp = new TempFolder();

        var (exit, _, error) = await RunCliAsync("init", "--base-branch", " ", "--project", temp.Path);

        Assert.Equal(2, exit);
        Assert.Contains("--base-branch needs a branch name", error, StringComparison.Ordinal);
        Assert.False(File.Exists(ConfigLoader.ConfigPath(temp.Path)));
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
