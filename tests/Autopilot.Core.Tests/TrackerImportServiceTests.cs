using Autopilot.Core;
using Autopilot.Core.Tasks;
using Microsoft.Data.Sqlite;
using Ws = Autopilot.Core.Workspace.Workspace;

namespace Autopilot.Core.Tests;

public sealed class TrackerImportServiceTests : IDisposable
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

    private sealed class FakeSource(params WorkItem[] items) : IWorkItemSource
    {
        public List<WorkItem> Items { get; } = [.. items];

        public Task<IReadOnlyList<WorkItem>> ListOpenAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkItem>>(Items);

        public Task<WorkItem?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));

        public Task RecordTriageAsync(string id, TriageResult result, CancellationToken ct) => throw new InvalidOperationException("The import must not write to the tracker.");
    }

    private LocalTaskService Tasks() => new(new Ws(Path.Combine(_temp, "ws")), "demo");

    private static WorkItem Item(string id, string title, string state = "Active") =>
        new(id, title, "Body " + id, ["alpha", "beta"], "https://example.test/_workitems/edit/" + id, state);

    [Fact]
    public async Task A_second_run_creates_nothing_and_a_changed_title_and_state_are_updated()
    {
        var tasks = Tasks();
        var source = new FakeSource(Item("1", "One"), Item("2", "Two"));
        var importer = new TrackerImportService(tasks);

        var first = await importer.ImportAsync(source, null, false, default);
        var second = await importer.ImportAsync(source, null, false, default);
        source.Items[0] = Item("1", "One renamed", "Closed");
        var third = await importer.ImportAsync(source, null, false, default);

        Assert.Equal((2, 0, 0), (first.Created, first.Updated, first.Unchanged));
        Assert.Equal((0, 0, 2), (second.Created, second.Updated, second.Unchanged));
        Assert.Equal((0, 1, 1), (third.Created, third.Updated, third.Unchanged));
        var task = tasks.FindByExternalId("1")!;
        Assert.Equal("One renamed", task.Title);
        Assert.True(task.Closed);
        Assert.Equal(["alpha", "beta"], task.Labels.Order());
        Assert.Equal(2, tasks.List(includeClosed: true).Count);
    }

    [Fact]
    public async Task A_dry_run_counts_but_writes_nothing()
    {
        var tasks = Tasks();
        var importer = new TrackerImportService(tasks);
        await importer.ImportAsync(new FakeSource(Item("1", "One")), null, false, default);

        var report = await importer.ImportAsync(new FakeSource(Item("1", "Changed"), Item("2", "New")), null, true, default);

        Assert.Equal((1, 1, 0, true), (report.Created, report.Updated, report.Unchanged, report.DryRun));
        Assert.Equal("One", tasks.FindByExternalId("1")!.Title);
        Assert.Null(tasks.FindByExternalId("2"));
    }

    [Fact]
    public async Task The_limit_cuts_the_list()
    {
        var report = await new TrackerImportService(Tasks()).ImportAsync(new FakeSource(Item("1", "a"), Item("2", "b"), Item("3", "c")), 2, false, default);

        Assert.Equal(2, report.Created);
    }
}
