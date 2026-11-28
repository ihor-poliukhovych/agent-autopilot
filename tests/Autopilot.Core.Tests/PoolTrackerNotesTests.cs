using System.Globalization;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Local;
using Autopilot.Core.Pool;
using Autopilot.Core.Tasks;
using Microsoft.Data.Sqlite;
using Ws = Autopilot.Core.Workspace.Workspace;

namespace Autopilot.Core.Tests;

/// <summary>Notes for the next attempt at a task that lives in a tracker, where the pool cannot edit the text.</summary>
public sealed class PoolTrackerNotesTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);
    private readonly Ws _workspace;
    private readonly PoolStateStore _state;

    public PoolTrackerNotesTests()
    {
        _workspace = new Ws(Path.Combine(_temp, "ws"));
        _state = new PoolStateStore(_workspace, "demo");
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
    public async Task A_tracker_task_keeps_the_notes_in_the_workspace_and_the_next_attempt_sees_them()
    {
        var tracker = new TrackerItems(new WorkItem("15279", "Fix the report", "The report is empty.", []));
        var tasks = new PipelinePoolTasks(null!, tracker, new TriageStore(Path.Combine(_temp, "project")), null, null!, new AutopilotConfig(), null, _state);

        await tasks.AnswerAsync("15279", "Notes for the next attempt (pool master): touch the exporter only.", default);

        var seen = await new PoolNotesWorkItemSource(tracker, _state).GetAsync("15279", default);
        Assert.StartsWith("The report is empty.", seen!.Body, StringComparison.Ordinal);
        Assert.Contains("touch the exporter only", seen.Body, StringComparison.Ordinal);
        Assert.Equal("The report is empty.", (await tracker.GetAsync("15279", default))!.Body); // the tracker text is untouched
    }

    [Fact]
    public async Task Notes_do_not_make_an_earlier_triage_look_out_of_date()
    {
        var item = new WorkItem("15279", "Fix the report", "The report is empty.", []);
        var triage = new TriageStore(Path.Combine(_temp, "project"));
        await triage.SaveAsync(item, new TriageResult(TriageLabel.AgentReady, "ok", ["a"], RiskLevel.Low), default);
        var tracker = new TrackerItems(item);
        var tasks = new PipelinePoolTasks(null!, new PoolNotesWorkItemSource(tracker, _state), triage, null, null!, new AutopilotConfig(), null, _state);

        await tasks.AnswerAsync("15279", "Try the exporter.", default);

        Assert.Equal(PoolTriageState.AgentReady, (await tasks.TriageStateAsync("15279", default)).State);
    }

    [Fact]
    public async Task A_tracker_id_that_looks_like_a_workspace_task_number_still_goes_to_the_notes()
    {
        var local = new LocalTaskService(_workspace, "demo");
        var own = local.Add("A local task", "Local text.", [], "test");
        var tracker = new TrackerItems(new WorkItem(own.Id, "Tracker item", "Tracker text.", []));
        var tasks = new PipelinePoolTasks(null!, tracker, new TriageStore(Path.Combine(_temp, "project")), local, null!, new AutopilotConfig(), null, _state);

        await tasks.AnswerAsync(own.Id, "A note.", default);

        Assert.Equal(["A note."], _state.Notes(own.Id));
        Assert.Equal("Local text.", local.Get(int.Parse(own.Id, CultureInfo.InvariantCulture))!.Body);
    }

    [Fact]
    public void Notes_are_kept_per_task_in_order_and_can_be_cleared()
    {
        var at = DateTimeOffset.UtcNow;
        _state.AddNote("1", "first", at);
        _state.AddNote("1", "second", at);
        _state.AddNote("2", "other", at);

        Assert.Equal(["first", "second"], _state.Notes("1"));
        _state.ClearNotes("1");
        Assert.Empty(_state.Notes("1"));
        Assert.Equal(["other"], _state.Notes("2"));
    }

    private sealed class TrackerItems(WorkItem item) : IWorkItemSource
    {
        public Task<IReadOnlyList<WorkItem>> ListOpenAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkItem>>([item]);

        public Task<WorkItem?> GetAsync(string id, CancellationToken ct) => Task.FromResult(id == item.Id ? item : null);

        public Task RecordTriageAsync(string id, TriageResult result, CancellationToken ct) => Task.CompletedTask;
    }
}
