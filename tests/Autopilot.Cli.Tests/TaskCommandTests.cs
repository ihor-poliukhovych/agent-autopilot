using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Autopilot.Cli.Tests;

/// <summary>The task commands against a temp workspace.</summary>
public sealed class TaskCommandTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CliContext(TextReader.Null, output, error) { DoctorChecksTools = false };
        var exit = await CliApp.RunAsync([.. args, "--workspace", _temp.Combine("ws")], context);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<TestRepo> AddProjectAsync()
    {
        var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["src/a.cs"] = "class A {}" });
        Assert.Equal(0, (await RunAsync("project", "add", "demo", "--repo", repo.Root)).Exit);
        return repo;
    }

    [Fact]
    public async Task Add_list_show_comment_close_reopen_and_label_work_end_to_end()
    {
        using var repo = await AddProjectAsync();

        Assert.Contains("Created task 1: Fix the 404", (await RunAsync("task", "add", "Fix the 404", "--body", "Details", "--label", "bug", "--project", "demo")).Out);
        Assert.Equal(0, (await RunAsync("task", "add", "Second", "--project", "demo")).Exit);
        Assert.Equal(0, (await RunAsync("task", "comment", "1", "first look", "--project", "demo")).Exit);
        Assert.Equal(0, (await RunAsync("task", "label", "1", "--add", "ui", "--remove", "bug", "--project", "demo")).Exit);
        Assert.Contains("closed", (await RunAsync("task", "close", "#2", "--project", "demo")).Out);

        using var list = JsonDocument.Parse((await RunAsync("task", "list", "--json", "--project", "demo")).Out);
        var only = Assert.Single(list.RootElement.EnumerateArray());
        Assert.Equal("Fix the 404", only.GetProperty("title").GetString());
        Assert.Equal("ui", only.GetProperty("labels")[0].GetString());

        var show = (await RunAsync("task", "show", "1", "--project", "demo")).Out;
        Assert.Contains("first look", show);
        Assert.Contains("[labels]", show);

        Assert.Contains("reopened", (await RunAsync("task", "reopen", "2", "--project", "demo")).Out);
        Assert.Contains("2", (await RunAsync("task", "list", "--project", "demo")).Out);
    }

    [Fact]
    public async Task Errors_are_readable()
    {
        using var repo = await AddProjectAsync();
        Assert.Equal(2, (await RunAsync("task", "show", "abc", "--project", "demo")).Exit);
        Assert.Equal(2, (await RunAsync("task", "show", "5", "--project", "demo")).Exit);
        Assert.Equal(2, (await RunAsync("task", "label", "1", "--project", "demo")).Exit);
        var folder = await RunAsync("task", "list", "--project", repo.Root);
        Assert.Equal(2, folder.Exit);
        Assert.Contains("registered project", folder.Err);
    }

    [Fact]
    public async Task Import_files_loads_repository_task_files_once()
    {
        using var repo = await AddProjectAsync();
        var folder = Path.Combine(repo.Root, ".autopilot", "tasks");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "001-fix.md"), "# Fix it\nLabels: bug\nText");

        Assert.Contains("1 new", (await RunAsync("task", "import-files", "--project", "demo")).Out);
        Assert.Contains("1 unchanged", (await RunAsync("task", "import-files", "--project", "demo")).Out);
        Assert.Contains("Fix it", (await RunAsync("task", "list", "--project", "demo")).Out);
    }

    [Fact]
    public async Task List_command_reads_workspace_tasks_for_a_local_project()
    {
        using var repo = await AddProjectAsync();
        await RunAsync("task", "add", "Hub task", "--project", "demo");
        var list = await RunAsync("list", "--json", "--project", "demo");
        using var json = JsonDocument.Parse(list.Out);
        Assert.Equal("Hub task", Assert.Single(json.RootElement.EnumerateArray()).GetProperty("title").GetString());
    }
}
