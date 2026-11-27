using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private Workspace.Workspace NewWorkspace() => new(Path.Combine(_temp, "ws"));

    private string FakeRepo(string name = "repo")
    {
        var folder = Path.Combine(_temp, name);
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        return folder;
    }

    [Fact]
    public void Resolve_prefers_the_explicit_path_then_the_environment_then_the_profile()
    {
        var explicitPath = Path.Combine(_temp, "a");
        Assert.Equal(explicitPath, Workspace.Workspace.ResolveRoot(explicitPath, _ => "ignored"));
        var fromEnvironment = Path.Combine(_temp, "b");
        Assert.Equal(fromEnvironment, Workspace.Workspace.ResolveRoot(null, name => name == "AUTOPILOT_WORKSPACE" ? fromEnvironment : null));
        var fallback = Workspace.Workspace.ResolveRoot(null, _ => null);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".autopilot"), fallback);
    }

    [Fact]
    public void Open_applies_migrations_once_and_records_the_version()
    {
        var workspace = NewWorkspace();
        workspace.Open().Dispose();
        workspace.Open().Dispose();

        using var connection = workspace.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MAX(version) FROM schema_version";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(Workspace.Workspace.LatestVersion, reader.GetInt32(1));
        Assert.Equal(Workspace.Workspace.LatestVersion, reader.GetInt32(0));
        Assert.True(File.Exists(workspace.DatabasePath));
    }

    [Fact]
    public void A_database_from_a_newer_version_is_refused()
    {
        var workspace = NewWorkspace();
        using (var connection = workspace.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO schema_version (version, name, applied_at) VALUES (999, 'future', 'x')";
            command.ExecuteNonQuery();
        }

        Assert.Throws<WorkspaceException>(() => workspace.Open());
    }

    [Fact]
    public void Add_list_get_update_and_remove()
    {
        var registry = new ProjectRegistry(NewWorkspace());
        Assert.Empty(registry.List());
        Assert.False(File.Exists(registry.Workspace.DatabasePath));

        var repo = FakeRepo();
        var added = registry.Add(new NewProject("shop", repo, "The Shop", "develop"));
        Assert.Equal("The Shop", added.DisplayName);
        Assert.True(Directory.Exists(Path.Combine(registry.Workspace.ProjectFolder("shop"), "tasks")));
        Assert.Equal(["shop"], registry.List().Select(p => p.Key));
        Assert.Equal("develop", registry.Get("shop")!.BaseBranch);

        var updated = registry.Update("shop", new ProjectUpdate(DisplayName: "Shop 2", TaskSource: "azure-devops"));
        Assert.Equal("Shop 2", updated.DisplayName);
        Assert.Equal("azure-devops", registry.Get("shop")!.TaskSource);

        Assert.True(registry.Remove("shop"));
        Assert.Null(registry.Get("shop"));
        Assert.True(Directory.Exists(registry.Workspace.ProjectFolder("shop")));
        Assert.False(registry.Remove("shop"));
    }

    [Fact]
    public void Remove_deletes_workspace_data_only_when_asked_and_never_touches_the_repository()
    {
        var registry = new ProjectRegistry(NewWorkspace());
        var repo = FakeRepo();
        File.WriteAllText(Path.Combine(repo, "keep.txt"), "x");
        registry.Add(new NewProject("one", repo));
        registry.Remove("one", deleteData: true);

        Assert.False(Directory.Exists(registry.Workspace.ProjectFolder("one")));
        Assert.Equal([".git", "keep.txt"], Directory.EnumerateFileSystemEntries(repo).Select(p => Path.GetFileName(p)).Order());
    }

    [Theory]
    [InlineData("shop", true)]
    [InlineData("a1_b-2", true)]
    [InlineData("Shop", false)]
    [InlineData("-x", false)]
    [InlineData("a/b", false)]
    [InlineData("..", false)]
    [InlineData("con", false)]
    [InlineData("", false)]
    public void Keys_must_be_filesystem_safe(string key, bool valid) => Assert.Equal(valid, ProjectRegistry.IsValidKey(key));

    [Fact]
    public void Validate_reports_every_problem()
    {
        var registry = new ProjectRegistry(NewWorkspace());
        var plain = Path.Combine(_temp, "plain");
        Directory.CreateDirectory(plain);
        registry.Add(new NewProject("taken", FakeRepo()));

        Assert.Contains(registry.Validate(new NewProject("taken", FakeRepo("r2"))), p => p.Contains("already exists", StringComparison.Ordinal));
        Assert.Contains(registry.Validate(new NewProject("ok", plain)), p => p.Contains("not the root of a git repository", StringComparison.Ordinal));
        Assert.Contains(registry.Validate(new NewProject("ok", Path.Combine(_temp, "missing"))), p => p.Contains("does not exist", StringComparison.Ordinal));
        Assert.Contains(registry.Validate(new NewProject("Bad Key", FakeRepo("r3"))), p => p.Contains("not valid", StringComparison.Ordinal));
        Assert.Contains(registry.Validate(new NewProject("ok", FakeRepo("r4"), TaskSource: "jira")), p => p.Contains("not known", StringComparison.Ordinal));
        Assert.Contains(registry.Validate(new NewProject("ok", FakeRepo("r5"), SettingsJson: "[1]")), p => p.Contains("JSON object", StringComparison.Ordinal));
        Assert.Empty(registry.Validate(new NewProject("fine", FakeRepo("r6"))));
        Assert.Throws<WorkspaceException>(() => registry.Add(new NewProject("taken", FakeRepo("r7"))));
    }
}
