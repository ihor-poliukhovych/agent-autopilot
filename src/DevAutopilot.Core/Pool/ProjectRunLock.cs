using DevAutopilot.Core.Workspace;

namespace DevAutopilot.Core.Pool;

/// <summary>Another run of the same project holds the lock; the message names the process.</summary>
public sealed class RunLockHeldException(string projectKey, int pid, string owner)
    : Exception($"Another run is active for project \"{projectKey}\" ({owner}, process {pid}). Wait for it to finish, or cancel it.")
{
    public int Pid { get; } = pid;
}

/// <summary>
/// One run per project at a time. The lock is a row in the workspace database with the process id of the
/// holder. A row whose process is gone is stale and is taken over, so a crash never blocks the project.
/// </summary>
public sealed class ProjectRunLock(Workspace.Workspace workspace, Func<int, bool>? isProcessAlive = null, Func<int>? currentPid = null)
{
    private readonly Func<int, bool> _isAlive = isProcessAlive ?? IsAlive;
    private readonly Func<int> _pid = currentPid ?? (() => Environment.ProcessId);

    /// <summary>Takes the lock or throws <see cref="RunLockHeldException"/>. Dispose the result to release it.</summary>
    public IDisposable Acquire(string projectKey, string owner)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "BEGIN IMMEDIATE");
        try
        {
            var held = Db.Query(connection, r => (Pid: r.GetInt32(0), Owner: r.GetString(1)),
                "SELECT pid, owner FROM project_locks WHERE project_key = $k", ("$k", projectKey));
            if (held.Count > 0 && held[0].Pid != _pid() && _isAlive(held[0].Pid))
            {
                throw new RunLockHeldException(projectKey, held[0].Pid, held[0].Owner);
            }

            Db.Execute(connection, "DELETE FROM project_locks WHERE project_key = $k", ("$k", projectKey));
            Db.Execute(connection, "INSERT INTO project_locks (project_key, pid, owner, acquired_at) VALUES ($k, $p, $o, $t)",
                ("$k", projectKey), ("$p", _pid()), ("$o", owner), ("$t", Db.Stamp(DateTimeOffset.UtcNow)));
            Db.Execute(connection, "COMMIT");
        }
        catch
        {
            Db.Execute(connection, "ROLLBACK");
            throw;
        }

        return new Handle(this, projectKey, _pid());
    }

    /// <summary>The live holder of the project's lock, or null when there is none or it is stale.</summary>
    public (int Pid, string Owner)? Holder(string projectKey)
    {
        using var connection = workspace.Open();
        var held = Db.Query(connection, r => (Pid: r.GetInt32(0), Owner: r.GetString(1)),
            "SELECT pid, owner FROM project_locks WHERE project_key = $k", ("$k", projectKey));
        return held.Count > 0 && _isAlive(held[0].Pid) ? held[0] : null;
    }

    /// <summary>Removes the project's lock when the given process holds it (used when that process is cancelled).</summary>
    public void ReleaseIfHeldBy(string projectKey, int pid)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "DELETE FROM project_locks WHERE project_key = $k AND pid = $p", ("$k", projectKey), ("$p", pid));
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class Handle(ProjectRunLock owner, string key, int pid) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.ReleaseIfHeldBy(key, pid);
            }
        }
    }
}
