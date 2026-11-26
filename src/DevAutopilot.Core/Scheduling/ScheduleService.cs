using System.Text.RegularExpressions;
using DevAutopilot.Core.Process;
using DevAutopilot.Core.Workspace;

namespace DevAutopilot.Core.Scheduling;

/// <summary>A daily job at a local time. <see cref="Command"/> is the full command line to run.</summary>
public sealed record ScheduledJob(string Name, string AtTime, string Command);

/// <summary>The operating system's scheduler. Tests use a fake; the real ones are below.</summary>
public interface IScheduler
{
    /// <summary>"Windows Task Scheduler" or "crontab", for messages.</summary>
    string Description { get; }

    /// <summary>Creates or replaces the job.</summary>
    Task InstallAsync(ScheduledJob job, CancellationToken ct);

    Task<bool> RemoveAsync(string name, CancellationToken ct);

    Task<bool> ExistsAsync(string name, CancellationToken ct);
}

public sealed record SchedulePlan(string ProjectKey, string JobName, string AtTime, int? Limit, string Command, string SchedulerDescription)
{
    public override string ToString() =>
        $"Run the pool of project {ProjectKey} every day at {AtTime}" + (Limit is { } n ? $" (at most {n} items)" : string.Empty)
        + $" with {SchedulerDescription}, as the current user." + Environment.NewLine + $"  Command: {Command}";
}

public sealed record ScheduleState(SchedulePlan Plan, bool InstalledInOs);

/// <summary>
/// The night schedule of a project: <c>autopilot pool run</c> at a time of day, installed in the user's own
/// scheduler (no administrator rights). The plan is kept in the workspace database; the OS is asked whether
/// the job is still there.
/// </summary>
public sealed partial class ScheduleService(Workspace.Workspace workspace, string projectKey, IScheduler scheduler)
{
    public static string JobName(string projectKey) => "dev-autopilot-" + projectKey;

    /// <summary>"HH:mm" (24 hours) or null when the text is not a time of day.</summary>
    public static string? NormalizeTime(string? text)
    {
        var match = text is null ? Match.Empty : TimePattern().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var hour = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return hour is <= 23 && minute is <= 59 ? $"{hour:00}:{minute:00}" : null;
    }

    /// <summary>What <see cref="SetAsync"/> would do. Nothing is changed.</summary>
    public SchedulePlan Plan(string atTime, int? limit, string programCommand)
    {
        var time = NormalizeTime(atTime) ?? throw new WorkspaceException($"\"{atTime}\" is not a time of day. Use HH:mm, for example 02:30.");
        if (limit is <= 0)
        {
            throw new WorkspaceException("The limit must be greater than 0.");
        }

        var command = programCommand + " pool run" + (limit is { } n ? $" --limit {n}" : string.Empty);
        return new SchedulePlan(projectKey, JobName(projectKey), time, limit, command, scheduler.Description);
    }

    public async Task SetAsync(SchedulePlan plan, CancellationToken ct)
    {
        await scheduler.InstallAsync(new ScheduledJob(plan.JobName, plan.AtTime, plan.Command), ct).ConfigureAwait(false);
        using var connection = workspace.Open();
        Db.Execute(connection, "DELETE FROM schedules WHERE project_key = $k", ("$k", projectKey));
        Db.Execute(connection, "INSERT INTO schedules (project_key, at_time, run_limit, command, created_at) VALUES ($k, $t, $l, $c, $n)",
            ("$k", projectKey), ("$t", plan.AtTime), ("$l", plan.Limit), ("$c", plan.Command), ("$n", Db.Stamp(DateTimeOffset.UtcNow)));
    }

    public async Task<ScheduleState?> ShowAsync(CancellationToken ct)
    {
        using var connection = workspace.Open();
        var stored = Db.Query(connection, r => new SchedulePlan(projectKey, JobName(projectKey), r.GetString(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetString(2), scheduler.Description),
            "SELECT at_time, run_limit, command FROM schedules WHERE project_key = $k", ("$k", projectKey)).FirstOrDefault();
        return stored is null ? null : new ScheduleState(stored, await scheduler.ExistsAsync(stored.JobName, ct).ConfigureAwait(false));
    }

    /// <summary>Removes the job from the scheduler and the plan from the workspace. Returns false when there was neither.</summary>
    public async Task<bool> RemoveAsync(CancellationToken ct)
    {
        var inOs = await scheduler.RemoveAsync(JobName(projectKey), ct).ConfigureAwait(false);
        using var connection = workspace.Open();
        var stored = Db.Execute(connection, "DELETE FROM schedules WHERE project_key = $k", ("$k", projectKey)) > 0;
        return inOs || stored;
    }

    [GeneratedRegex(@"^(\d{1,2}):(\d{2})$")]
    private static partial Regex TimePattern();
}

/// <summary>Windows Task Scheduler through <c>schtasks.exe</c>, for the current user (no administrator rights).</summary>
public sealed class WindowsTaskScheduler(Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>? run = null) : IScheduler
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> _run = run ??
        ((arguments, ct) => ProcessRunner.RunAsync("schtasks.exe", arguments, Environment.CurrentDirectory, TimeSpan.FromSeconds(30), null, ct));

    public string Description => "Windows Task Scheduler";

    public async Task InstallAsync(ScheduledJob job, CancellationToken ct)
    {
        var result = await _run(["/Create", "/TN", job.Name, "/TR", job.Command, "/SC", "DAILY", "/ST", job.AtTime, "/F"], ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new WorkspaceException($"Task Scheduler refused the job (exit code {result.ExitCode}): {result.StandardError.Trim()}");
        }
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken ct) =>
        (await _run(["/Delete", "/TN", name, "/F"], ct).ConfigureAwait(false)).Succeeded;

    public async Task<bool> ExistsAsync(string name, CancellationToken ct) =>
        (await _run(["/Query", "/TN", name], ct).ConfigureAwait(false)).Succeeded;
}

/// <summary>One line in the user's crontab, marked with a comment so it can be found and replaced.</summary>
public sealed class CronScheduler(Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>>? run = null) : IScheduler
{
    private readonly Func<IReadOnlyList<string>, string?, CancellationToken, Task<ProcessResult>> _run = run ?? RunCrontabAsync;

    public string Description => "the user's crontab";

    public async Task InstallAsync(ScheduledJob job, CancellationToken ct)
    {
        var lines = (await ReadAsync(ct).ConfigureAwait(false)).Where(l => !IsOurs(l, job.Name)).ToList();
        var parts = job.AtTime.Split(':');
        lines.Add($"{int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)} {int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)} * * * {job.Command} {Marker(job.Name)}");
        await WriteAsync(lines, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken ct)
    {
        var lines = await ReadAsync(ct).ConfigureAwait(false);
        var kept = lines.Where(l => !IsOurs(l, name)).ToList();
        if (kept.Count == lines.Count)
        {
            return false;
        }

        await WriteAsync(kept, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken ct) =>
        (await ReadAsync(ct).ConfigureAwait(false)).Any(l => IsOurs(l, name));

    private static string Marker(string name) => "# " + name;

    private static bool IsOurs(string line, string name) => line.TrimEnd().EndsWith(Marker(name), StringComparison.Ordinal);

    private async Task<List<string>> ReadAsync(CancellationToken ct)
    {
        var result = await _run(["-l"], null, ct).ConfigureAwait(false);

        // "no crontab for user" exits with 1 and an empty list; that is not an error.
        return result.ExitCode == 0
            ? result.StandardOutput.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];
    }

    private async Task WriteAsync(List<string> lines, CancellationToken ct)
    {
        var text = string.Join('\n', lines) + (lines.Count > 0 ? "\n" : string.Empty);
        var result = await _run(["-"], text, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new WorkspaceException($"crontab refused the change (exit code {result.ExitCode}): {result.StandardError.Trim()}");
        }
    }

    private static async Task<ProcessResult> RunCrontabAsync(IReadOnlyList<string> arguments, string? input, CancellationToken ct)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("crontab") { WorkingDirectory = Environment.CurrentDirectory };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // ProcessRunner closes stdin at once, so a crontab text goes through a temporary file instead.
        if (input is null)
        {
            return await ProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(30), null, ct).ConfigureAwait(false);
        }

        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, input, ct).ConfigureAwait(false);
            startInfo.ArgumentList.Clear();
            startInfo.ArgumentList.Add(file);
            return await ProcessRunner.RunAsync(startInfo, TimeSpan.FromSeconds(30), null, ct).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
