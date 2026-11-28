using Autopilot.Core.Workspace;

namespace Autopilot.Core.Pool;

/// <summary>One thing that happened to a pool run.</summary>
public sealed record PoolRunEventRecord(string RunId, DateTimeOffset At, PoolRunEvent Event, string? TaskId, string? Detail);

/// <summary>Appends and reads the events of pool runs (table <c>pool_run_events</c>).</summary>
public sealed class PoolRunEventStore(Workspace.Workspace workspace)
{
    public void Append(string runId, PoolRunEvent runEvent, DateTimeOffset at, string? taskId = null, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var connection = workspace.Open();
        Db.Execute(connection,
            "INSERT INTO pool_run_events (run_id, at, event, task_id, detail) VALUES ($r, $t, $e, $i, $d)",
            ("$r", runId), ("$t", Db.Stamp(at)), ("$e", runEvent.ToString()), ("$i", taskId), ("$d", detail));
    }

    /// <summary>The id of the run whose newest event was appended last, or null when no run has events.</summary>
    public string? LatestRunId()
    {
        using var connection = workspace.Open();
        return Db.Query(connection, r => r.GetString(0), "SELECT run_id FROM pool_run_events ORDER BY id DESC LIMIT 1").FirstOrDefault();
    }

    /// <summary>The events of one run in the order they were appended. Empty when there are none.</summary>
    public IReadOnlyList<PoolRunEventRecord> Read(string runId)
    {
        using var connection = workspace.Open();
        return Db.Query(connection,
            r => new PoolRunEventRecord(r.GetString(0), Db.ParseStamp(r.GetString(1)), Enum.Parse<PoolRunEvent>(r.GetString(2)), Db.StringOrNull(r, 3), Db.StringOrNull(r, 4)),
            "SELECT run_id, at, event, task_id, detail FROM pool_run_events WHERE run_id = $r ORDER BY id",
            ("$r", runId));
    }
}
