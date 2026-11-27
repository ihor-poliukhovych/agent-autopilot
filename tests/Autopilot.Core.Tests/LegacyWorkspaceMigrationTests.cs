using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Tests;

public sealed class LegacyWorkspaceMigrationTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "autopilot-tests", "profile-" + Guid.NewGuid().ToString("N")[..12]);

    public LegacyWorkspaceMigrationTests() => Directory.CreateDirectory(_profile);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_profile, recursive: true);
    }

    private string Legacy => Path.Combine(_profile, ".dev-autopilot");

    private string Current => Path.Combine(_profile, ".autopilot");

    private void MakeLegacyWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(Legacy, "projects", "demo"));
        File.WriteAllText(Path.Combine(Legacy, "autopilot.db"), "database bytes");
        File.WriteAllText(Path.Combine(Legacy, "projects", "demo", "autopilot.json"), "{ \"version\": 1 }");
    }

    [Fact]
    public void Nothing_happens_without_an_old_folder()
    {
        var result = LegacyWorkspaceMigration.Apply(_profile);

        Assert.Equal(LegacyMigrationOutcome.NotNeeded, result.Outcome);
        Assert.Null(result.Message);
        Assert.False(Directory.Exists(Current));
    }

    [Fact]
    public void The_old_folder_is_moved_with_all_its_files_and_a_note_is_left()
    {
        MakeLegacyWorkspace();

        var result = LegacyWorkspaceMigration.Apply(_profile);

        Assert.Equal(LegacyMigrationOutcome.Moved, result.Outcome);
        Assert.Equal(Current, result.Root);
        Assert.False(Directory.Exists(Legacy));
        Assert.Equal("database bytes", File.ReadAllText(Path.Combine(Current, "autopilot.db")));
        Assert.Equal("{ \"version\": 1 }", File.ReadAllText(Path.Combine(Current, "projects", "demo", "autopilot.json")));
        Assert.True(File.Exists(Path.Combine(Current, LegacyWorkspaceMigration.NoteFileName)));
    }

    [Fact]
    public void An_existing_new_folder_is_never_overwritten_and_the_old_one_stays()
    {
        MakeLegacyWorkspace();
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Current, "autopilot.db"), "new data");

        var result = LegacyWorkspaceMigration.Apply(_profile);

        Assert.Equal(LegacyMigrationOutcome.BothExist, result.Outcome);
        Assert.Equal(Current, result.Root);
        Assert.Contains(Legacy, result.Message, StringComparison.Ordinal);
        Assert.Equal("new data", File.ReadAllText(Path.Combine(Current, "autopilot.db")));
        Assert.Equal("database bytes", File.ReadAllText(Path.Combine(Legacy, "autopilot.db")));
    }

    [Fact]
    public void Resolve_migrates_only_for_the_default_location()
    {
        MakeLegacyWorkspace();
        var elsewhere = Path.Combine(_profile, "elsewhere");

        var explicitRoot = Workspace.Workspace.Resolve(elsewhere, _ => null, userProfile: _profile);
        var fromEnvironment = Workspace.Workspace.Resolve(null, _ => elsewhere, userProfile: _profile);
        Assert.Equal(elsewhere, explicitRoot.Root);
        Assert.Equal(elsewhere, fromEnvironment.Root);
        Assert.True(Directory.Exists(Legacy));

        var notices = new List<string>();
        var byDefault = Workspace.Workspace.Resolve(null, _ => null, notices.Add, _profile);
        Assert.Equal(Current, byDefault.Root);
        Assert.False(Directory.Exists(Legacy));
        Assert.Single(notices);
    }

    [Fact]
    public void A_moved_workspace_opens_and_keeps_its_data()
    {
        var old = new Workspace.Workspace(Legacy);
        using (old.Open())
        {
        }

        var key = "demo";
        Directory.CreateDirectory(Path.Combine(Legacy, "projects", key));
        File.WriteAllText(Path.Combine(Legacy, "projects", key, "autopilot.json"), "{}");
        SqliteConnection.ClearAllPools();

        var moved = Workspace.Workspace.Resolve(null, _ => null, userProfile: _profile);
        using var connection = moved.Open();

        Assert.Equal(Current, moved.Root);
        Assert.True(File.Exists(Path.Combine(moved.ProjectFolder(key), "autopilot.json")));
    }
}
