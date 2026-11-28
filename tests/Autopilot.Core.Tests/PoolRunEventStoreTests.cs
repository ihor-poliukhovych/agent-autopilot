using Autopilot.Core.Pool;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Tests;

public sealed class PoolRunEventStoreTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);
    private readonly PoolRunEventStore _store;
    private static readonly DateTimeOffset T0 = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public PoolRunEventStoreTests() => _store = new PoolRunEventStore(new Workspace.Workspace(Path.Combine(_temp, "ws")));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    [Fact]
    public void Append_then_read_returns_the_event_with_all_fields()
    {
        _store.Append("run-1", PoolRunEvent.SlotGranted, T0, "42", "slot 2");

        var events = _store.Read("run-1");

        var e = Assert.Single(events);
        Assert.Equal("run-1", e.RunId);
        Assert.Equal(PoolRunEvent.SlotGranted, e.Event);
        Assert.Equal(T0, e.At);
        Assert.Equal("42", e.TaskId);
        Assert.Equal("slot 2", e.Detail);
    }

    [Fact]
    public void Events_come_back_in_the_order_they_were_appended()
    {
        // Times are deliberately not increasing: order is by append, not by time.
        _store.Append("run-1", PoolRunEvent.Start, T0.AddMinutes(5));
        _store.Append("run-1", PoolRunEvent.WorkFinished, T0);
        _store.Append("run-1", PoolRunEvent.VerifyPassed, T0.AddMinutes(1));

        Assert.Equal([PoolRunEvent.Start, PoolRunEvent.WorkFinished, PoolRunEvent.VerifyPassed], _store.Read("run-1").Select(e => e.Event));
    }

    [Fact]
    public void A_null_task_id_and_detail_stay_null()
    {
        _store.Append("run-1", PoolRunEvent.Enqueue, T0);

        var e = Assert.Single(_store.Read("run-1"));
        Assert.Null(e.TaskId);
        Assert.Null(e.Detail);
    }

    [Fact]
    public void A_run_without_events_reads_as_empty_and_other_runs_are_not_mixed_in()
    {
        _store.Append("run-1", PoolRunEvent.Start, T0);
        _store.Append("run-2", PoolRunEvent.Cancel, T0);

        Assert.Empty(_store.Read("run-3"));
        Assert.Equal([PoolRunEvent.Start], _store.Read("run-1").Select(e => e.Event));
        Assert.Equal([PoolRunEvent.Cancel], _store.Read("run-2").Select(e => e.Event));
    }
}
