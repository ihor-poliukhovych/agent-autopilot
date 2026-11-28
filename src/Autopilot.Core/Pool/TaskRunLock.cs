using Autopilot.Core.Workspace;

namespace Autopilot.Core.Pool;

/// <summary>Another run holds the lock of this task.</summary>
public sealed class TaskLockHeldException(string projectKey, string workItemId, int pid, string owner)
    : Exception($"Task {workItemId} of project \"{projectKey}\" is already being worked on ({owner}, process {pid}).")
{
    public int Pid { get; } = pid;
}

/// <summary>
/// One lock per task, kept in the workspace database with the process id of the holder. The pool takes it
/// before an agent starts on a task, so two agents never share a task. A row whose process is gone is stale
/// and is taken over. Several tasks of one project can be locked at the same time by one pool run.
/// </summary>
public sealed class TaskRunLock(Workspace.Workspace workspace, Func<int, bool>? isProcessAlive = null, Func<int>? currentPid = null, TimeProvider? clock = null, TimeSpan? staleAfter = null)
{
    private readonly Func<int, bool> _isAlive = isProcessAlive ?? ProjectRunLock.IsAlive;
    private readonly Func<int> _pid = currentPid ?? (() => Environment.ProcessId);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _staleAfter = staleAfter ?? PoolStateStore.DefaultStaleAfter;

    /// <summary>The lock key of a task's triage. '@' cannot appear in an id, so it never meets a run lock.</summary>
    public static string TriageKey(string workItemId) => "@triage-" + workItemId;

    /// <summary>
    /// Takes the triage lock of a task. Unlike a run lock it is also refused to the process that holds it, so
    /// two triages of one task in one process never share a worktree either.
    /// </summary>
    public IDisposable AcquireTriage(string projectKey, string workItemId, string owner) =>
        Acquire(projectKey, TriageKey(workItemId), owner, reentrant: false);

    /// <summary>The live holder of a task's triage lock, or null.</summary>
    public (int Pid, string Owner)? TriageHolder(string projectKey, string workItemId) => Holder(projectKey, TriageKey(workItemId));

    public IDisposable Acquire(string projectKey, string workItemId, string owner, bool reentrant = true)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            var held = Db.Query(connection, r => (Pid: r.GetInt32(0), Owner: r.GetString(1)),
                "SELECT pid, owner FROM task_locks WHERE project_key = $k AND work_item_id = $i", ("$k", projectKey), ("$i", workItemId));
            // A holder whose process lives on but whose pool stopped beating is dead too (the process id was reused).
            if (held.Count > 0 && (!reentrant || held[0].Pid != _pid()) && _isAlive(held[0].Pid)
                && !PoolStateStore.IsSilent(connection, projectKey, held[0].Pid, _clock.GetUtcNow(), _staleAfter))
            {
                throw new TaskLockHeldException(projectKey, workItemId, held[0].Pid, held[0].Owner);
            }

            Db.Execute(connection, "DELETE FROM task_locks WHERE project_key = $k AND work_item_id = $i", ("$k", projectKey), ("$i", workItemId));
            Db.Execute(connection, "INSERT INTO task_locks (project_key, work_item_id, pid, owner, acquired_at) VALUES ($k, $i, $p, $o, $t)",
                ("$k", projectKey), ("$i", workItemId), ("$p", _pid()), ("$o", owner), ("$t", Db.Stamp(_clock.GetUtcNow())));
            Db.Execute(connection, "COMMIT");
        }
        catch
        {
            Db.Execute(connection, "ROLLBACK");
            throw;
        }

        return new Handle(workspace, projectKey, workItemId, _pid());
    }

    /// <summary>The live holder of a task's lock, or null.</summary>
    public (int Pid, string Owner)? Holder(string projectKey, string workItemId)
    {
        using var connection = workspace.Open();
        var held = Db.Query(connection, r => (Pid: r.GetInt32(0), Owner: r.GetString(1)),
            "SELECT pid, owner FROM task_locks WHERE project_key = $k AND work_item_id = $i", ("$k", projectKey), ("$i", workItemId));
        return held.Count > 0 && _isAlive(held[0].Pid) && !PoolStateStore.IsSilent(connection, projectKey, held[0].Pid, _clock.GetUtcNow(), _staleAfter) ? held[0] : null;
    }

    private sealed class Handle(Workspace.Workspace workspace, string projectKey, string workItemId, int pid) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                using var connection = workspace.Open();
                Db.Execute(connection, "DELETE FROM task_locks WHERE project_key = $k AND work_item_id = $i AND pid = $p",
                    ("$k", projectKey), ("$i", workItemId), ("$p", pid));
            }
        }
    }
}
