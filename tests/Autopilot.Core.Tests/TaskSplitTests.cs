using Autopilot.Core.Pool;
using Autopilot.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Tests;

public sealed class TaskSplitTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);
    private readonly LocalTaskService _tasks;
    private readonly PoolService _pool;
    private readonly TaskSplit _split;

    public TaskSplitTests()
    {
        var workspace = new Workspace.Workspace(Path.Combine(_temp, "ws"));
        _tasks = new LocalTaskService(workspace, "demo");
        _pool = new PoolService(workspace, "demo");
        _split = new TaskSplit(_tasks, _pool);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static List<SplitProposal> Proposal() =>
    [
        new("First part", "Do the first thing.", ["part"], ["src/A/**", "tests/A.Tests/**"]),
        new("Second part", "Do the second thing.", [], ["src/B/**"]),
        new("Third part", "", ["x"], []),
    ];

    [Fact]
    public void Split_creates_linked_children_queued_in_order_and_keeps_the_parent_open()
    {
        var original = _tasks.Add("Big task");
        var result = _split.Split(original.Number, Proposal());

        Assert.True(result.Success);
        Assert.Equal(["First part", "Second part", "Third part"], result.Children.Select(c => c.Title));
        Assert.All(result.Children, c => Assert.Equal(original.Number, c.ParentNumber));
        Assert.Equal(result.Children.Select(c => c.Number), _tasks.Children(original.Number).Select(c => c.Number));
        Assert.Equal(result.Children.Select(c => c.Id), _pool.List().Select(e => e.WorkItemId));
        Assert.Equal(["src/A/**", "tests/A.Tests/**"], PoolTaskHints.PathsFrom(result.Children[0].Body));
        Assert.Equal(["src/B/**"], PoolTaskHints.PathsFrom(result.Children[1].Body));
        Assert.Contains("part", result.Children[0].Labels);

        var parent = _tasks.Get(original.Number)!;
        Assert.False(parent.Closed);
        Assert.Null(parent.ParentNumber);
        Assert.DoesNotContain(original.Id, _pool.List().Select(e => e.WorkItemId));
        var history = _tasks.Show(original.Number)!.History;
        Assert.Contains(history, h => h.Kind == LocalTaskService.HistoryKind.Split && h.Summary.Contains($"#{result.Children[0].Number}") && h.Summary.Contains($"#{result.Children[2].Number}"));
    }

    [Fact]
    public void Parent_closes_automatically_after_the_last_child_closes()
    {
        var original = _tasks.Add("Big task");
        var children = _split.Split(original.Number, Proposal()).Children;

        _tasks.Close(children[0].Number);
        _tasks.Close(children[1].Number);
        Assert.False(_tasks.Get(original.Number)!.Closed);

        _tasks.Close(children[2].Number);
        var parent = _tasks.Get(original.Number)!;
        Assert.True(parent.Closed);
        Assert.Contains(_tasks.Show(original.Number)!.History, h => h.Kind == LocalTaskService.HistoryKind.Closed && h.Summary.Contains("automatically"));
    }

    [Fact]
    public void Empty_proposal_or_blank_title_creates_nothing_and_returns_an_error()
    {
        var original = _tasks.Add("Big task");

        var empty = _split.Split(original.Number, []);
        Assert.False(empty.Success);
        Assert.NotNull(empty.Error);

        var blank = _split.Split(original.Number, [new("Fine", "", [], []), new("  ", "", [], [])]);
        Assert.False(blank.Success);
        Assert.NotNull(blank.Error);

        Assert.Empty(_tasks.Children(original.Number));
        Assert.Empty(_pool.List());
        Assert.Single(_tasks.List(includeClosed: true));
    }
}
