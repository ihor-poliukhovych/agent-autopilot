using System.Text.Json;
using DevAutopilot.Core.Config;
using Microsoft.Data.Sqlite;

namespace DevAutopilot.Cli.Tests;

public sealed class ProjectCommandTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string WorkspacePath => _temp.Combine("ws");

    private async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await CliApp.RunAsync([.. args, "--workspace", WorkspacePath], new CliContext(TextReader.Null, output, error) { DoctorChecksTools = false });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Add_list_show_work_against_a_temp_workspace_and_leave_the_repository_alone()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["src/a.cs"] = "class A {}" });
        var before = Directory.EnumerateFileSystemEntries(repo.Root, "*", SearchOption.AllDirectories).Where(p => !p.Contains(".git", StringComparison.Ordinal)).Order().ToList();

        var add = await RunAsync("project", "add", "demo", "--repo", repo.Root, "--name", "Demo");
        Assert.Equal(0, add.Exit);
        Assert.Contains("Added project demo", add.Out);
        Assert.True(File.Exists(Path.Combine(WorkspacePath, "projects", "demo", "autopilot.json")));
        Assert.True(Directory.Exists(Path.Combine(WorkspacePath, "projects", "demo", "tasks")));
        var after = Directory.EnumerateFileSystemEntries(repo.Root, "*", SearchOption.AllDirectories).Where(p => !p.Contains(".git", StringComparison.Ordinal)).Order().ToList();
        Assert.Equal(before, after);

        Assert.Contains("demo", (await RunAsync("project", "list")).Out);
        using var listJson = JsonDocument.Parse((await RunAsync("project", "list", "--json")).Out);
        Assert.Equal("demo", listJson.RootElement[0].GetProperty("key").GetString());

        Assert.Contains("Demo", (await RunAsync("project", "show", "demo")).Out);
        using var showJson = JsonDocument.Parse((await RunAsync("project", "show", "demo", "--json")).Out);
        Assert.Equal("local", showJson.RootElement.GetProperty("taskSource").GetString());
        Assert.Equal(Path.GetFullPath(repo.Root), showJson.RootElement.GetProperty("repositoryFolder").GetString());
    }

    [Fact]
    public async Task Add_refuses_a_duplicate_key_and_a_folder_that_is_not_a_repository()
    {
        using var repo = await TestRepo.CreateAsync();
        Assert.Equal(0, (await RunAsync("project", "add", "demo", "--repo", repo.Root)).Exit);

        var duplicate = await RunAsync("project", "add", "demo", "--repo", repo.Root);
        Assert.Equal(2, duplicate.Exit);
        Assert.Contains("already exists", duplicate.Err);

        var notRepo = await RunAsync("project", "add", "other", "--repo", _temp.Path);
        Assert.Equal(2, notRepo.Exit);
        Assert.Contains("git repository", notRepo.Err);
    }

    [Fact]
    public async Task Remove_keeps_data_unless_delete_data_and_yes_are_given()
    {
        using var repo = await TestRepo.CreateAsync();
        await RunAsync("project", "add", "demo", "--repo", repo.Root);
        var folder = Path.Combine(WorkspacePath, "projects", "demo");

        var refused = await RunAsync("project", "remove", "demo", "--delete-data");
        Assert.Equal(2, refused.Exit);
        Assert.Contains("--yes", refused.Err);
        Assert.Contains("demo", (await RunAsync("project", "list")).Out);

        Assert.Equal(0, (await RunAsync("project", "remove", "demo")).Exit);
        Assert.True(Directory.Exists(folder));

        await RunAsync("project", "add", "demo", "--repo", repo.Root);
        Assert.Equal(0, (await RunAsync("project", "remove", "demo", "--delete-data", "--yes")).Exit);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Import_local_copies_settings_and_tasks_and_leaves_the_source_untouched()
    {
        using var repo = await TestRepo.CreateAsync();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".autopilot", "tasks"));
        File.WriteAllText(Path.Combine(repo.Root, ".autopilot", "autopilot.json"), "{ \"baseBranch\": \"main\" }");
        var task = Path.Combine(repo.Root, ".autopilot", "tasks", "001-first.md");
        File.WriteAllText(task, "# First\n\nDo it.\n");

        var result = await RunAsync("project", "import-local", repo.Root, "--key", "imported");

        Assert.Equal(0, result.Exit);
        var folder = Path.Combine(WorkspacePath, "projects", "imported");
        Assert.Equal("main", ConfigLoader.LoadFile(Path.Combine(folder, "autopilot.json")).BaseBranch);
        Assert.Equal(File.ReadAllText(task), File.ReadAllText(Path.Combine(folder, "tasks", "001-first.md")));
        Assert.True(File.Exists(task));

        var missing = await RunAsync("project", "import-local", _temp.Path);
        Assert.Equal(2, missing.Exit);
    }

    [Fact]
    public async Task Project_key_selects_workspace_data_and_a_folder_still_selects_repository_data()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["src/a.cs"] = "class A {}" });
        await RunAsync("project", "add", "demo", "--repo", repo.Root);
        File.WriteAllText(Path.Combine(WorkspacePath, "projects", "demo", "tasks", "001-ws.md"), "# From workspace\n");

        var viaKey = await RunAsync("doctor", "--project", "demo", "--skip-build");
        Assert.Contains(Path.Combine(WorkspacePath, "projects", "demo", "autopilot.json"), viaKey.Out);
        Assert.Contains("1 open task(s)", viaKey.Out);

        // The old flow: a folder path reads .autopilot/ inside the repository, which has none here.
        var viaFolder = await RunAsync("doctor", "--project", repo.Root, "--skip-build");
        Assert.Contains("Config file not found", viaFolder.Out + viaFolder.Err, StringComparison.OrdinalIgnoreCase);

        var unknown = await RunAsync("doctor", "--project", "nothing");
        Assert.Equal(2, unknown.Exit);
        Assert.Contains("no project", unknown.Err);
    }
}
