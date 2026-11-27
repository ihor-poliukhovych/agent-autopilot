using System.Security.Cryptography;
using Autopilot.Core.Pool;
using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record JobRecord(
    string Id,
    string ProjectKey,
    string WorkItemId,
    JobStatus Status,
    string? Step,
    int? Pid,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string LogPath,
    string? Message)
{
    public bool IsActive => Status is JobStatus.Queued or JobStatus.Running;
}

/// <summary>Starts a program that outlives the caller and returns its process id. Tests use a fake.</summary>
public interface IJobLauncher
{
    int Launch(string fileName, IReadOnlyList<string> arguments, string workingDirectory);
}

/// <summary>Stops one process and its children. Tests use a fake.</summary>
public interface IProcessKiller
{
    void Kill(int pid);
}

/// <summary>
/// Background implement jobs of one project: a row in the workspace database (status, current step, times)
/// and a <c>log.txt</c> next to the project's run output. A job is one process; cancelling it kills that
/// process tree only, so other jobs, in this project or another, keep running.
/// </summary>
public sealed class JobService(Workspace.Workspace workspace, string projectKey, Func<int, bool>? isProcessAlive = null)
{
    private const string Columns = "id, project_key, work_item_id, status, step, pid, created_at, started_at, finished_at, log_path, message";

    private readonly Func<int, bool> _isAlive = isProcessAlive ?? ProjectRunLock.IsAlive;

    public string JobFolder(string jobId) => Path.Combine(workspace.ProjectFolder(projectKey), "out", "jobs", jobId);

    public JobRecord Create(string workItemId)
    {
        var id = "j" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var logPath = Path.Combine(JobFolder(id), "log.txt");
        Directory.CreateDirectory(JobFolder(id));
        using var connection = workspace.Open();
        Db.Execute(connection,
            "INSERT INTO jobs (id, project_key, work_item_id, status, created_at, log_path) VALUES ($i, $k, $w, 'queued', $t, $l)",
            ("$i", id), ("$k", projectKey), ("$w", workItemId), ("$t", Db.Stamp(DateTimeOffset.UtcNow)), ("$l", logPath));
        return Get(id)!;
    }

    /// <summary>Creates the job and starts <paramref name="arguments"/> detached; the process id is stored on the job.</summary>
    public JobRecord Start(string workItemId, IJobLauncher launcher, string fileName, Func<string, IReadOnlyList<string>> arguments, string workingDirectory)
    {
        var job = Create(workItemId);
        try
        {
            var pid = launcher.Launch(fileName, arguments(job.Id), workingDirectory);
            using var connection = workspace.Open();
            Db.Execute(connection, "UPDATE jobs SET pid = $p WHERE id = $i AND status = 'queued'", ("$p", pid), ("$i", job.Id));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Finish(job.Id, JobStatus.Failed, $"The job could not be started: {ex.Message}");
            throw;
        }

        return Get(job.Id)!;
    }

    public JobRecord? Get(string id)
    {
        using var connection = workspace.Open();
        var job = Db.Query(connection, Read, $"SELECT {Columns} FROM jobs WHERE project_key = $k AND id = $i", ("$k", projectKey), ("$i", id)).FirstOrDefault();
        return job is null ? null : Reconcile(connection, job);
    }

    /// <summary>Newest first. A running job whose process is gone is marked failed first.</summary>
    public IReadOnlyList<JobRecord> List()
    {
        using var connection = workspace.Open();
        var jobs = Db.Query(connection, Read, $"SELECT {Columns} FROM jobs WHERE project_key = $k ORDER BY created_at DESC, id", ("$k", projectKey));
        return jobs.Select(j => Reconcile(connection, j)).ToList();
    }

    /// <summary>The job's process calls this when it starts working.</summary>
    public void MarkRunning(string id, int pid)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "UPDATE jobs SET status = 'running', pid = $p, started_at = $t WHERE id = $i AND status IN ('queued', 'running')",
            ("$p", pid), ("$t", Db.Stamp(DateTimeOffset.UtcNow)), ("$i", id));
    }

    public void SetStep(string id, string step)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "UPDATE jobs SET step = $s WHERE id = $i AND status = 'running'", ("$s", step), ("$i", id));
    }

    /// <summary>Ends the job. A job that was cancelled stays cancelled.</summary>
    public void Finish(string id, JobStatus status, string? message)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "UPDATE jobs SET status = $s, message = $m, finished_at = $t WHERE id = $i AND status IN ('queued', 'running')",
            ("$s", Name(status)), ("$m", message), ("$t", Db.Stamp(DateTimeOffset.UtcNow)), ("$i", id));
    }

    /// <summary>
    /// Stops this one job: kills its process tree, frees the project's run lock if that process held it, and
    /// marks it cancelled. Returns false when the job is unknown or already ended.
    /// </summary>
    public bool Cancel(string id, IProcessKiller killer, ProjectRunLock? runLock = null)
    {
        var job = Get(id);
        if (job is null || !job.IsActive)
        {
            return false;
        }

        if (job.Pid is { } pid)
        {
            try
            {
                killer.Kill(pid);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            {
                // The process is already gone; the job is cancelled all the same.
            }

            runLock?.ReleaseIfHeldBy(projectKey, pid);
        }

        Finish(id, JobStatus.Cancelled, "Cancelled by a person.");
        return true;
    }

    public async Task<string> ReadLogAsync(string id, int? lastLines, CancellationToken ct)
    {
        var job = Get(id);
        if (job is null || !File.Exists(job.LogPath))
        {
            return string.Empty;
        }

        var text = await File.ReadAllTextAsync(job.LogPath, ct).ConfigureAwait(false);
        if (lastLines is not { } count)
        {
            return text;
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - count - 1)));
    }

    private JobRecord Reconcile(SqliteConnection connection, JobRecord job)
    {
        if (job.Status != JobStatus.Running || job.Pid is not { } pid || _isAlive(pid))
        {
            return job;
        }

        const string message = "The process ended without finishing the job.";
        Db.Execute(connection, "UPDATE jobs SET status = 'failed', message = $m, finished_at = $t WHERE id = $i AND status = 'running'",
            ("$m", message), ("$t", Db.Stamp(DateTimeOffset.UtcNow)), ("$i", job.Id));
        return job with { Status = JobStatus.Failed, Message = message, FinishedAt = DateTimeOffset.UtcNow };
    }

    private static string Name(JobStatus status) => status.ToString().ToLowerInvariant();

    private static JobRecord Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), Enum.Parse<JobStatus>(r.GetString(3), ignoreCase: true), Db.StringOrNull(r, 4),
        r.IsDBNull(5) ? null : r.GetInt32(5), Db.ParseStamp(r.GetString(6)), Db.ParseStampOrNull(r, 7), Db.ParseStampOrNull(r, 8),
        r.GetString(9), Db.StringOrNull(r, 10));
}
