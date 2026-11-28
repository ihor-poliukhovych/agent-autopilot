using Autopilot.Core.Config;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Pool;
using Autopilot.Core.Runs;
using Microsoft.Data.Sqlite;
using E = Autopilot.Core.Pool.PoolRunEvent;
using Ws = Autopilot.Core.Workspace.Workspace;

namespace Autopilot.Core.Tests;

public sealed class PoolRunStatusFolderTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 28, 1, 0, 0, TimeSpan.Zero);
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static PoolRunEventRecord Ev(E e, string? task = null) => new("r", T0, e, task, null);

    private static PoolRunStatusSnapshot Fold(params PoolRunEventRecord[] events) => PoolRunStatusFolder.Fold(events);

    [Fact]
    public void An_empty_list_folds_to_Planned_with_zero_progress()
    {
        var snapshot = Fold();
        Assert.Equal(PoolRunState.Planned, snapshot.State);
        Assert.Equal(new PoolRunProgress(0, 0, 0), snapshot.Progress);
    }

    [Fact]
    public void A_refused_event_is_skipped_and_does_not_throw()
    {
        var snapshot = Fold(Ev(E.Enqueue), Ev(E.JudgePassed), Ev(E.PersonApproved), Ev(E.Resume));
        Assert.Equal(PoolRunState.Queued, snapshot.State);
    }

    [Fact]
    public void The_happy_path_ends_ready_for_a_person()
    {
        var snapshot = Fold(Ev(E.Enqueue), Ev(E.Start), Ev(E.SlotUnavailable), Ev(E.SlotGranted, "1"), Ev(E.TaskMerged, "1"),
            Ev(E.WorkFinished), Ev(E.VerifyPassed), Ev(E.AttackPassed), Ev(E.JudgePassed));
        Assert.Equal(PoolRunState.ReadyForPerson, snapshot.State);
    }

    [Fact]
    public void Pause_and_resume_for_infrastructure_and_budget_return_to_working()
    {
        var working = PoolRunState.Running(PoolRunSubStatus.Working);
        Assert.Equal(PoolRunState.Running(PoolRunSubStatus.PausedInfrastructure), Fold(Ev(E.Enqueue), Ev(E.Start), Ev(E.PauseInfrastructure)).State);
        Assert.Equal(working, Fold(Ev(E.Enqueue), Ev(E.Start), Ev(E.PauseInfrastructure), Ev(E.Resume)).State);
        Assert.Equal(PoolRunState.Running(PoolRunSubStatus.PausedBudget), Fold(Ev(E.Enqueue), Ev(E.Start), Ev(E.PauseBudget)).State);
        Assert.Equal(working, Fold(Ev(E.Enqueue), Ev(E.Start), Ev(E.PauseBudget), Ev(E.Resume)).State);
    }

    [Fact]
    public void Park_and_resolve_move_between_working_and_parked_waiting()
    {
        Assert.Equal(PoolRunState.Running(PoolRunSubStatus.ParkedTasksWaiting), Fold(Ev(E.Enqueue), Ev(E.Start), Ev(E.ParkTasks)).State);
        Assert.Equal(PoolRunState.Running(PoolRunSubStatus.Working), Fold(Ev(E.Enqueue), Ev(E.Start), Ev(E.ParkTasks), Ev(E.ParkedTasksResolved)).State);
    }

    [Fact]
    public void Progress_counts_merged_parked_and_distinct_tasks()
    {
        var snapshot = Fold(Ev(E.Enqueue), Ev(E.Start),
            Ev(E.SlotGranted, "1"), Ev(E.SlotGranted, "2"), Ev(E.SlotGranted, "3"), Ev(E.SlotGranted, "3"),
            Ev(E.TaskMerged, "1"), Ev(E.TaskMerged, "1"), Ev(E.TaskParked, "2"));
        Assert.Equal(new PoolRunProgress(Done: 1, Total: 3, Parked: 1), snapshot.Progress);
        Assert.Equal(PoolRunState.Running(PoolRunSubStatus.Working), snapshot.State);
    }

    [Fact]
    public void New_event_kinds_do_not_change_how_stored_events_read()
    {
        var store = new PoolRunEventStore(new Ws(Path.Combine(_temp, "ws")));
        store.Append("r", E.Retry, T0);
        store.Append("r", E.TaskMerged, T0, "9");
        Assert.Equal([E.Retry, E.TaskMerged], store.Read("r").Select(e => e.Event));
        Assert.False(PoolRunStateMachine.Transition(PoolRunState.Done, E.TaskMerged).Allowed);
    }

    [Fact]
    public async Task A_run_with_one_merged_and_one_parked_task_records_events_that_fold_to_1_1_2()
    {
        var workspace = new Ws(Path.Combine(_temp, "ws2"));
        var pool = new PoolService(workspace, "demo");
        pool.Add("1");
        pool.Add("2");
        var tasks = new FakeTasks { Integrate = r => r.WorkItemId == "2" ? new IntegrationResult(false, "conflict") : null };
        var events = new PoolRunEventStore(workspace);
        var runner = new PoolRunner(pool, new RunStore(workspace, "demo"), new ProjectRunLock(workspace, _ => false), "demo",
            (id, _) => Task.FromResult(new ImplementResult(id, ImplementOutcome.Published, "ok", "autopilot/" + id)),
            new PoolRunOptions { Config = () => new PoolConfig(), Tasks = tasks, Events = events, Clock = new FakeClock(T0), PollInterval = TimeSpan.FromMilliseconds(20) });

        await runner.RunAsync(null, Path.Combine(_temp, "report"), CancellationToken.None);

        var runIds = RunIds(workspace);
        var runId = Assert.Single(runIds);
        var stored = events.Read(runId);
        var snapshot = PoolRunStatusFolder.Fold(stored);
        Assert.Equal(new PoolRunProgress(Done: 1, Total: 2, Parked: 1), snapshot.Progress);
        Assert.Contains(stored, e => e.Event == E.Start);
        Assert.Contains(stored, e => e.Event == E.RunFinished);
    }

    private static List<string> RunIds(Ws workspace)
    {
        using var connection = workspace.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT run_id FROM pool_run_events";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }
}
