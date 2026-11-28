using Autopilot.Core.Tasks;
using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Tests;

public sealed class TaskTreeTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);
    private readonly LocalTaskService _tasks;

    public TaskTreeTests()
    {
        _tasks = new LocalTaskService(new Workspace.Workspace(Path.Combine(_temp, "ws")), "demo");
        for (var i = 1; i <= 4; i++)
        {
            _tasks.Add("Task " + i);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    [Fact]
    public void SetParent_sets_and_clears_the_parent()
    {
        Assert.Equal(1, _tasks.SetParent(2, 1).ParentNumber);
        Assert.Equal(1, _tasks.Get(2)!.ParentNumber);
        Assert.Equal([2], _tasks.Children(1).Select(t => t.Number));
        Assert.Null(_tasks.SetParent(2, null).ParentNumber);
        Assert.Empty(_tasks.Children(1));
    }

    [Fact]
    public void SetParent_rejects_unknown_task_or_parent_without_change()
    {
        _tasks.SetParent(2, 1);
        Assert.Throws<WorkspaceException>(() => _tasks.SetParent(99, 1));
        Assert.Throws<WorkspaceException>(() => _tasks.SetParent(2, 99));
        Assert.Equal(1, _tasks.Get(2)!.ParentNumber);
    }

    [Fact]
    public void SetParent_rejects_a_task_as_its_own_parent()
    {
        Assert.Throws<WorkspaceException>(() => _tasks.SetParent(1, 1));
        Assert.Null(_tasks.Get(1)!.ParentNumber);
    }

    [Fact]
    public void SetParent_rejects_a_descendant_as_parent_and_keeps_the_stored_parent()
    {
        _tasks.SetParent(2, 1);
        _tasks.SetParent(3, 2);
        Assert.Throws<WorkspaceException>(() => _tasks.SetParent(1, 2));
        Assert.Throws<WorkspaceException>(() => _tasks.SetParent(1, 3));
        Assert.Null(_tasks.Get(1)!.ParentNumber);
        Assert.Equal(2, _tasks.Get(3)!.ParentNumber);
    }

    [Fact]
    public void Tree_returns_roots_and_children_ordered_by_number()
    {
        _tasks.SetParent(4, 1);
        _tasks.SetParent(2, 1);
        _tasks.SetParent(3, 2);
        var tree = _tasks.Tree();
        Assert.Equal([1], tree.Select(n => n.Task.Number));
        Assert.Equal([2, 4], tree[0].Children.Select(n => n.Task.Number));
        Assert.Equal([3], tree[0].Children[0].Children.Select(n => n.Task.Number));
    }

    [Fact]
    public void Tree_lists_tasks_without_a_parent_as_roots()
    {
        _tasks.SetParent(2, 1);
        Assert.Equal([1, 3, 4], _tasks.Tree().Select(n => n.Task.Number));
    }

    [Fact]
    public void A_task_with_children_is_a_group_and_reads_its_kind_from_the_label()
    {
        _tasks.SetParent(2, 1);
        _tasks.ChangeLabels(1, ["kind:Sprint"], null);
        _tasks.ChangeLabels(3, ["kind:bogus"], null);
        var tree = _tasks.Tree();
        Assert.True(tree[0].IsGroup);
        Assert.Equal("sprint", tree[0].Kind);
        Assert.False(tree[0].Children[0].IsGroup);
        Assert.Null(tree[0].Children[0].Kind);
        Assert.Null(tree[1].Kind);
    }

    [Fact]
    public void SetKind_replaces_a_previous_kind_label()
    {
        _tasks.ChangeLabels(1, ["bug"], null);
        _tasks.SetKind(1, "epic");
        var labels = _tasks.SetKind(1, "release");
        Assert.Equal(["bug", "kind:release"], labels);
        Assert.Equal("release", TaskKinds.FromLabels(_tasks.Get(1)!.Labels));
    }

    [Theory]
    [InlineData("")]
    [InlineData("story")]
    [InlineData("kind:epic")]
    public void SetKind_rejects_names_outside_the_allowed_values(string name)
    {
        Assert.Throws<WorkspaceException>(() => _tasks.SetKind(1, name));
        Assert.Empty(_tasks.Get(1)!.Labels);
    }
}
