using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Pool;

public static class DecisionKinds
{
    public const string Answer = "answer";
    public const string Failure = "failure";
}

/// <summary>One thing the pool master decided: the task, the question, the answer and why.</summary>
public sealed record PoolDecision(
    long Id,
    string WorkItemId,
    string Kind,
    string Question,
    string Answer,
    string Reason,
    string? Category,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevertedAt);

/// <summary>A task the pool left for a person.</summary>
public sealed record ParkedTask(string WorkItemId, string? Category, string Reason, DateTimeOffset ParkedAt);

/// <summary>A task the running pool works on ("running") or holds back ("waiting", with the reason).</summary>
public sealed record PoolSlot(string WorkItemId, string State, string? Detail, DateTimeOffset Since);

public sealed record PoolPause(string Reason, DateTimeOffset? RetryAt, DateTimeOffset CreatedAt);

/// <summary>
/// What the unattended pool remembers in the workspace database: the master's decisions, parked tasks,
/// who is running or waiting, the pause, and the agent runs counted against the budget.
/// </summary>
public sealed class PoolStateStore(Workspace.Workspace workspace, string projectKey)
{
    // ---------- decisions ----------

    public PoolDecision AddDecision(string workItemId, string kind, string question, string answer, string reason, string? category, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        Db.Execute(connection,
            "INSERT INTO pool_decisions (project_key, work_item_id, kind, question, answer, reason, category, created_at) VALUES ($k, $i, $kind, $q, $a, $r, $c, $t)",
            ("$k", projectKey), ("$i", workItemId), ("$kind", kind), ("$q", question), ("$a", answer), ("$r", reason), ("$c", category), ("$t", Db.Stamp(at)));
        using var id = Db.Command(connection, "SELECT last_insert_rowid()");
        return new PoolDecision(Convert.ToInt64(id.ExecuteScalar()), workItemId, kind, question, answer, reason, category, at, null);
    }

    public IReadOnlyList<PoolDecision> Decisions(int? limit = null)
    {
        using var connection = workspace.Open();
        return Db.Query(connection, ReadDecision,
            "SELECT id, work_item_id, kind, question, answer, reason, category, created_at, reverted_at FROM pool_decisions WHERE project_key = $k ORDER BY id DESC" + (limit is { } n ? $" LIMIT {n}" : string.Empty),
            ("$k", projectKey));
    }

    public PoolDecision? Decision(long id)
    {
        using var connection = workspace.Open();
        return Db.Query(connection, ReadDecision,
            "SELECT id, work_item_id, kind, question, answer, reason, category, created_at, reverted_at FROM pool_decisions WHERE project_key = $k AND id = $id",
            ("$k", projectKey), ("$id", id)).FirstOrDefault();
    }

    /// <summary>Marks a decision as undone by a person. False when it does not exist or was already undone.</summary>
    public bool Revert(long id, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        return Db.Execute(connection, "UPDATE pool_decisions SET reverted_at = $t WHERE project_key = $k AND id = $id AND reverted_at IS NULL",
            ("$t", Db.Stamp(at)), ("$k", projectKey), ("$id", id)) > 0;
    }

    /// <summary>How many times a person undid a decision of the master. It feeds the trust levels.</summary>
    public int RevertedCount()
    {
        using var connection = workspace.Open();
        using var command = Db.Command(connection, "SELECT COUNT(*) FROM pool_decisions WHERE project_key = $k AND reverted_at IS NOT NULL", ("$k", projectKey));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static PoolDecision ReadDecision(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), Db.StringOrNull(r, 6), Db.ParseStamp(r.GetString(7)), Db.ParseStampOrNull(r, 8));

    // ---------- parked ----------

    public void Park(string workItemId, string? category, string reason, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "DELETE FROM pool_parked WHERE project_key = $k AND work_item_id = $i", ("$k", projectKey), ("$i", workItemId));
        Db.Execute(connection, "INSERT INTO pool_parked (project_key, work_item_id, category, reason, parked_at) VALUES ($k, $i, $c, $r, $t)",
            ("$k", projectKey), ("$i", workItemId), ("$c", category), ("$r", reason), ("$t", Db.Stamp(at)));
    }

    public bool Unpark(string workItemId)
    {
        using var connection = workspace.Open();
        return Db.Execute(connection, "DELETE FROM pool_parked WHERE project_key = $k AND work_item_id = $i", ("$k", projectKey), ("$i", workItemId)) > 0;
    }

    public IReadOnlyList<ParkedTask> Parked()
    {
        using var connection = workspace.Open();
        return Db.Query(connection, r => new ParkedTask(r.GetString(0), Db.StringOrNull(r, 1), r.GetString(2), Db.ParseStamp(r.GetString(3))),
            "SELECT work_item_id, category, reason, parked_at FROM pool_parked WHERE project_key = $k ORDER BY parked_at", ("$k", projectKey));
    }

    // ---------- slots ----------

    public void SetSlot(string workItemId, string state, string? detail, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "INSERT OR REPLACE INTO pool_status (project_key, work_item_id, state, detail, since) VALUES ($k, $i, $s, $d, $t)",
            ("$k", projectKey), ("$i", workItemId), ("$s", state), ("$d", detail), ("$t", Db.Stamp(at)));
    }

    public void ClearSlot(string workItemId)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "DELETE FROM pool_status WHERE project_key = $k AND work_item_id = $i", ("$k", projectKey), ("$i", workItemId));
    }

    public void ClearSlots()
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "DELETE FROM pool_status WHERE project_key = $k", ("$k", projectKey));
    }

    public IReadOnlyList<PoolSlot> Slots()
    {
        using var connection = workspace.Open();
        return Db.Query(connection, r => new PoolSlot(r.GetString(0), r.GetString(1), Db.StringOrNull(r, 2), Db.ParseStamp(r.GetString(3))),
            "SELECT work_item_id, state, detail, since FROM pool_status WHERE project_key = $k ORDER BY since", ("$k", projectKey));
    }

    // ---------- pause ----------

    public void Pause(string reason, DateTimeOffset? retryAt, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "INSERT OR REPLACE INTO pool_pause (project_key, reason, retry_at, created_at) VALUES ($k, $r, $a, $t)",
            ("$k", projectKey), ("$r", reason), ("$a", retryAt is { } r ? Db.Stamp(r) : null), ("$t", Db.Stamp(at)));
    }

    public PoolPause? CurrentPause()
    {
        using var connection = workspace.Open();
        return Db.Query(connection, r => new PoolPause(r.GetString(0), Db.ParseStampOrNull(r, 1), Db.ParseStamp(r.GetString(2))),
            "SELECT reason, retry_at, created_at FROM pool_pause WHERE project_key = $k", ("$k", projectKey)).FirstOrDefault();
    }

    public bool Resume()
    {
        using var connection = workspace.Open();
        return Db.Execute(connection, "DELETE FROM pool_pause WHERE project_key = $k", ("$k", projectKey)) > 0;
    }

    // ---------- agent runs (budget) ----------

    public void CountAgentRun(string? workItemId, string kind, DateTimeOffset at)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "INSERT INTO pool_agent_runs (project_key, work_item_id, kind, started_at) VALUES ($k, $i, $kind, $t)",
            ("$k", projectKey), ("$i", workItemId), ("$kind", kind), ("$t", Db.Stamp(at)));
    }

    /// <summary>Agent runs started in the 24 hours before <paramref name="now"/>.</summary>
    public int AgentRunsInLastDay(DateTimeOffset now)
    {
        using var connection = workspace.Open();
        var rows = Db.Query(connection, r => r.GetString(0), "SELECT started_at FROM pool_agent_runs WHERE project_key = $k", ("$k", projectKey));
        return rows.Count(s => Db.ParseStamp(s) > now.AddHours(-24));
    }
}
