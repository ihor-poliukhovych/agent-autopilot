using System.Text.RegularExpressions;
using Autopilot.Core.Process;
using Autopilot.Core.Workspace;

namespace Autopilot.Core.Scheduling;

/// <summary>
/// A job at a local time. <see cref="Command"/> is the full command line to run. <see cref="Repeat"/> is
/// "daily" (the default), "weekdays", "none" (one date) or a comma list of days such as "mon,wed,fri".
/// A job with <see cref="OnLogon"/> runs when the user signs in instead of at the time (the catch-up job).
/// </summary>
public sealed record ScheduledJob(string Name, string AtTime, string Command)
{
    public string Repeat { get; init; } = ScheduleRepeats.Daily;

    /// <summary>The date of a one-time job, <c>yyyy-MM-dd</c>.</summary>
    public string? Date { get; init; }

    public bool OnLogon { get; init; }
}

public static class ScheduleRepeats
{
    public const string None = "none";
    public const string Daily = "daily";
    public const string Weekdays = "weekdays";

    public static IReadOnlyList<string> Days { get; } = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    /// <summary>The days of the week a repeat value stands for, or null when the value is not valid.</summary>
    public static IReadOnlyList<DayOfWeek>? DaysOf(string repeat)
    {
        switch (repeat.Trim().ToLowerInvariant())
        {
            case Daily:
                return [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
            case Weekdays:
                return [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
            case None:
                return [];
        }

        var days = new List<DayOfWeek>();
        foreach (var part in repeat.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = Days.ToList().IndexOf(part.ToLowerInvariant()[..Math.Min(3, part.Length)]);
            if (index < 0)
            {
                return null;
            }

            days.Add(index == 6 ? DayOfWeek.Sunday : (DayOfWeek)(index + 1));
        }

        return days.Count == 0 ? null : days.Distinct().ToList();
    }

    public static string Normalize(string repeat)
    {
        var text = repeat.Trim().ToLowerInvariant();
        if (text is Daily or Weekdays or None)
        {
            return text;
        }

        var days = DaysOf(text) ?? throw new WorkspaceException($"\"{repeat}\" is not a repeat. Use none, daily, weekdays or days such as mon,wed,fri.");
        return string.Join(',', days.OrderBy(d => d == DayOfWeek.Sunday ? 7 : (int)d).Select(d => Days[d == DayOfWeek.Sunday ? 6 : (int)d - 1]));
    }
}

/// <summary>What a person asked for: a start, how it repeats, when it stops, and how many tasks.</summary>
public sealed record ScheduleRequest(string At, string? Repeat = null, string? Until = null, int? Limit = null, bool? Unattended = null);

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
    /// <summary>The date of a one-time run, <c>yyyy-MM-dd</c>. Null for a repeating schedule.</summary>
    public string? Date { get; init; }

    public string Repeat { get; init; } = ScheduleRepeats.Daily;

    /// <summary>The run starts no new task after this time of day.</summary>
    public string? Until { get; init; }

    public bool Unattended { get; init; }

    /// <summary>The plan was made with <c>--at "date time"</c> or the new options, so it also installs the catch-up job.</summary>
    public bool CatchUp { get; init; }

    public string When => Date is not null && Repeat == ScheduleRepeats.None
        ? $"on {Date} at {AtTime}"
        : Repeat switch
        {
            ScheduleRepeats.Daily => $"every day at {AtTime}",
            ScheduleRepeats.Weekdays => $"on weekdays at {AtTime}",
            var days => $"on {days} at {AtTime}" + (Date is not null ? $", starting {Date}" : string.Empty),
        };

    public override string ToString() =>
        $"Run the pool of project {ProjectKey} {When}"
        + (Limit is { } n ? $" (at most {n} items)" : string.Empty)
        + (Unattended ? ", without a person watching (the pool master answers questions)" : string.Empty)
        + (Until is not null ? $", stopping at {Until}" : string.Empty)
        + $" with {SchedulerDescription}, as the current user." + Environment.NewLine + $"  Command: {Command}"
        + (CatchUp ? Environment.NewLine + "  A run missed because the computer was off starts at the next sign-in, if its stop time has not passed." : string.Empty);
}

public sealed record ScheduleState(SchedulePlan Plan, bool InstalledInOs)
{
    public DateTimeOffset? LastStartedAt { get; init; }

    /// <summary>The next start after now, or null for a one-time run that has passed.</summary>
    public DateTimeOffset? Next { get; init; }
}

/// <summary>
/// The night schedule of a project: <c>autopilot pool run</c> at a time of day, installed in the user's own
/// scheduler (no administrator rights). The plan is kept in the workspace database; the OS is asked whether
/// the job is still there.
/// </summary>
public sealed partial class ScheduleService(Workspace.Workspace workspace, string projectKey, IScheduler scheduler, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>How long after its start a run that was missed can still be caught up, when the schedule has no stop time.</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(12);

    public static string JobName(string projectKey) => "autopilot-" + projectKey;

    /// <summary>The job that starts a missed run when the user signs in.</summary>
    public static string CatchUpJobName(string projectKey) => JobName(projectKey) + "-catchup";

    /// <summary>The job name used before the product was renamed. Jobs installed under it still run, and are found and removed.</summary>
    public static string LegacyJobName(string projectKey) => "dev-autopilot-" + projectKey;

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

    /// <summary>What <see cref="SetAsync"/> would do for a daily run at a time. Nothing is changed.</summary>
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

    /// <summary>
    /// What <see cref="SetAsync"/> would do for a request: <c>At</c> is "HH:mm" or "yyyy-MM-dd HH:mm". With a date
    /// and no repeat the run happens once; with only a time it repeats daily. Nothing is changed.
    /// </summary>
    public SchedulePlan Plan(ScheduleRequest request, string programCommand, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var zone = timeZone ?? _clock.LocalTimeZone;
        var (date, time) = ParseAt(request.At);
        var repeat = request.Repeat is null ? (date is not null ? ScheduleRepeats.None : ScheduleRepeats.Daily) : ScheduleRepeats.Normalize(request.Repeat);
        if (repeat == ScheduleRepeats.None && date is null)
        {
            throw new WorkspaceException("A one-time run needs a date: --at \"yyyy-MM-dd HH:mm\".");
        }

        if (request.Limit is <= 0)
        {
            throw new WorkspaceException("The limit must be greater than 0.");
        }

        string? until = null;
        if (request.Until is not null)
        {
            until = NormalizeTime(request.Until) ?? throw new WorkspaceException($"\"{request.Until}\" is not a time of day. Use HH:mm, for example 06:00.");
        }

        if (repeat == ScheduleRepeats.None)
        {
            var start = At(date!.Value, time, zone);
            if (start <= _clock.GetUtcNow())
            {
                throw new WorkspaceException($"{date:yyyy-MM-dd} {time} is in the past. Pick a later time.");
            }
        }

        var command = programCommand + " pool run"
            + (request.Limit is { } n ? $" --limit {n}" : string.Empty)
            + (request.Unattended == true ? " --unattended" : string.Empty)
            + (until is not null ? $" --until {until}" : string.Empty)
            + (repeat == ScheduleRepeats.None ? $" --only-on {date:yyyy-MM-dd}" : string.Empty)
            + " --scheduled";
        return new SchedulePlan(projectKey, JobName(projectKey), time, request.Limit, command, scheduler.Description)
        {
            Date = date?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Repeat = repeat,
            Until = until,
            Unattended = request.Unattended == true,
            CatchUp = true,
        };
    }

    /// <summary>Splits "HH:mm" or "yyyy-MM-dd HH:mm" into an optional date and a time.</summary>
    public static (DateOnly? Date, string Time) ParseAt(string? text)
    {
        var parts = (text ?? string.Empty).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2)
        {
            if (!DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            {
                throw new WorkspaceException($"\"{parts[0]}\" is not a date. Use yyyy-MM-dd, for example 2026-11-28.");
            }

            return (date, NormalizeTime(parts[1]) ?? throw new WorkspaceException($"\"{parts[1]}\" is not a time of day. Use HH:mm, for example 02:30."));
        }

        return (null, NormalizeTime(text) ?? throw new WorkspaceException($"\"{text}\" is not a time. Use HH:mm, or \"yyyy-MM-dd HH:mm\" for a date."));
    }

    /// <summary>Installs the plan. Returns a note for the person when the catch-up job could not be installed, otherwise null.</summary>
    public async Task<string?> SetAsync(SchedulePlan plan, CancellationToken ct)
    {
        await scheduler.RemoveAsync(LegacyJobName(projectKey), ct).ConfigureAwait(false);
        await scheduler.RemoveAsync(CatchUpJobName(projectKey), ct).ConfigureAwait(false);
        await scheduler.InstallAsync(new ScheduledJob(plan.JobName, plan.AtTime, plan.Command) { Repeat = plan.Repeat, Date = plan.Date }, ct).ConfigureAwait(false);
        string? note = null;
        if (plan.CatchUp)
        {
            try
            {
                await scheduler.InstallAsync(new ScheduledJob(CatchUpJobName(projectKey), plan.AtTime, CatchUpCommand(plan)) { OnLogon = true }, ct).ConfigureAwait(false);
            }
            catch (WorkspaceException ex)
            {
                note = $"The run is installed, but the catch-up job (start a missed run at sign-in) was not: {ex.Message} You can run 'autopilot schedule catch-up' by hand.";
            }
        }

        using var connection = workspace.Open();
        Db.Execute(connection, "DELETE FROM schedules WHERE project_key = $k", ("$k", projectKey));
        Db.Execute(connection,
            "INSERT INTO schedules (project_key, at_time, run_limit, command, created_at, start_date, repeat, until_time, unattended) VALUES ($k, $t, $l, $c, $n, $d, $r, $u, $a)",
            ("$k", projectKey), ("$t", plan.AtTime), ("$l", plan.Limit), ("$c", plan.Command), ("$n", Db.Stamp(_clock.GetUtcNow())),
            ("$d", plan.Date), ("$r", plan.Repeat), ("$u", plan.Until), ("$a", plan.Unattended ? 1 : 0));
        return note;
    }

    /// <summary>The command of the catch-up job: it starts the stored schedule only when a run was missed.</summary>
    private static string CatchUpCommand(SchedulePlan plan)
    {
        var marker = " pool run";
        var at = plan.Command.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? plan.Command : plan.Command[..at] + " schedule catch-up";
    }

    public async Task<ScheduleState?> ShowAsync(CancellationToken ct)
    {
        using var connection = workspace.Open();
        var stored = Db.Query(connection,
            r => (Plan: new SchedulePlan(projectKey, JobName(projectKey), r.GetString(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetString(2), scheduler.Description)
            {
                Date = Db.StringOrNull(r, 3),
                Repeat = Db.StringOrNull(r, 4) ?? ScheduleRepeats.Daily,
                Until = Db.StringOrNull(r, 5),
                Unattended = !r.IsDBNull(6) && r.GetInt32(6) != 0,
                CatchUp = !r.IsDBNull(4),
            }, Last: Db.ParseStampOrNull(r, 7)),
            "SELECT at_time, run_limit, command, start_date, repeat, until_time, unattended, last_started_at FROM schedules WHERE project_key = $k", ("$k", projectKey)).FirstOrDefault();
        if (stored.Plan is null)
        {
            return null;
        }

        var inOs = await scheduler.ExistsAsync(stored.Plan.JobName, ct).ConfigureAwait(false)
            || await scheduler.ExistsAsync(LegacyJobName(projectKey), ct).ConfigureAwait(false);
        return new ScheduleState(stored.Plan, inOs) { LastStartedAt = stored.Last, Next = ScheduleRules.NextRun(stored.Plan, _clock.GetUtcNow(), _clock.LocalTimeZone) };
    }

    /// <summary>Notes that the scheduled run started now, so the catch-up job does not start it again.</summary>
    public void MarkStarted()
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "UPDATE schedules SET last_started_at = $t WHERE project_key = $k", ("$t", Db.Stamp(_clock.GetUtcNow())), ("$k", projectKey));
    }

    /// <summary>The stored plan when a run was missed and can still start (its stop time has not passed), otherwise null.</summary>
    public async Task<SchedulePlan?> MissedAsync(CancellationToken ct)
    {
        var state = await ShowAsync(ct).ConfigureAwait(false);
        return state is not null && ScheduleRules.IsMissed(state.Plan, state.LastStartedAt, _clock.GetUtcNow(), _clock.LocalTimeZone) ? state.Plan : null;
    }

    /// <summary>Removes the job from the scheduler and the plan from the workspace. Returns false when there was neither.</summary>
    public async Task<bool> RemoveAsync(CancellationToken ct)
    {
        var inOs = await scheduler.RemoveAsync(JobName(projectKey), ct).ConfigureAwait(false);
        inOs |= await scheduler.RemoveAsync(LegacyJobName(projectKey), ct).ConfigureAwait(false);
        await scheduler.RemoveAsync(CatchUpJobName(projectKey), ct).ConfigureAwait(false);
        using var connection = workspace.Open();
        var stored = Db.Execute(connection, "DELETE FROM schedules WHERE project_key = $k", ("$k", projectKey)) > 0;
        return inOs || stored;
    }

    internal static DateTimeOffset At(DateOnly date, string time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.ParseExact(time, "HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    [GeneratedRegex(@"^(\d{1,2}):(\d{2})$")]
    private static partial Regex TimePattern();
}

/// <summary>When a schedule starts, and whether a start was missed. Pure functions of the plan and the clock.</summary>
public static class ScheduleRules
{
    /// <summary>The latest start at or before <paramref name="now"/>, or null when there is none yet.</summary>
    public static DateTimeOffset? LastStart(SchedulePlan plan, DateTimeOffset now, TimeZoneInfo zone)
    {
        foreach (var start in Starts(plan, now, zone, backwards: true))
        {
            if (start <= now)
            {
                return start;
            }
        }

        return null;
    }

    /// <summary>The first start after <paramref name="now"/>, or null for a one-time run that has passed.</summary>
    public static DateTimeOffset? NextRun(SchedulePlan plan, DateTimeOffset now, TimeZoneInfo zone) =>
        Starts(plan, now, zone, backwards: false).Cast<DateTimeOffset?>().FirstOrDefault(s => s > now);

    /// <summary>True when the latest start was not followed by a run and its stop time has not passed.</summary>
    public static bool IsMissed(SchedulePlan plan, DateTimeOffset? lastStarted, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (LastStart(plan, now, zone) is not { } start || lastStarted >= start)
        {
            return false;
        }

        var stop = start + ScheduleService.CatchUpWindow;
        if (plan.Until is { } until)
        {
            var localStart = TimeZoneInfo.ConvertTime(start, zone);
            var day = localStart.Date + TimeOnly.ParseExact(until, "HH:mm", System.Globalization.CultureInfo.InvariantCulture).ToTimeSpan();
            if (day <= localStart.DateTime)
            {
                day = day.AddDays(1);
            }

            stop = new DateTimeOffset(day, zone.GetUtcOffset(day));
        }

        return now < stop;
    }

    private static IEnumerable<DateTimeOffset> Starts(SchedulePlan plan, DateTimeOffset now, TimeZoneInfo zone, bool backwards)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var first = plan.Date is { } text && DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
        if (plan.Repeat == ScheduleRepeats.None)
        {
            if (first is { } only)
            {
                yield return ScheduleService.At(only, plan.AtTime, zone);
            }

            yield break;
        }

        var days = ScheduleRepeats.DaysOf(plan.Repeat) ?? [];
        for (var offset = 0; offset <= 8; offset++)
        {
            var date = today.AddDays(backwards ? -offset : offset);
            if (days.Contains(date.DayOfWeek) && (first is null || date >= first))
            {
                yield return ScheduleService.At(date, plan.AtTime, zone);
            }
        }
    }
}

/// <summary>Windows Task Scheduler through <c>schtasks.exe</c>, for the current user (no administrator rights).</summary>
public sealed class WindowsTaskScheduler(Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>>? run = null) : IScheduler
{
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessResult>> _run = run ??
        ((arguments, ct) => ProcessRunner.RunAsync("schtasks.exe", arguments, Environment.CurrentDirectory, TimeSpan.FromSeconds(30), null, ct));

    public string Description => "Windows Task Scheduler";

    public async Task InstallAsync(ScheduledJob job, CancellationToken ct)
    {
        // A one-time job is a daily one that the command's --only-on guard lets run on one date only
        // (the date format of /SD depends on the computer's region settings).
        IReadOnlyList<string> arguments = job.OnLogon
            ? ["/Create", "/TN", job.Name, "/TR", job.Command, "/SC", "ONLOGON", "/F"]
            : job.Repeat is ScheduleRepeats.Daily or ScheduleRepeats.None
                ? ["/Create", "/TN", job.Name, "/TR", job.Command, "/SC", "DAILY", "/ST", job.AtTime, "/F"]
                : ["/Create", "/TN", job.Name, "/TR", job.Command, "/SC", "WEEKLY", "/D", string.Join(',', (ScheduleRepeats.DaysOf(job.Repeat) ?? []).Select(WindowsDay)), "/ST", job.AtTime, "/F"];
        var result = await _run(arguments, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new WorkspaceException($"Task Scheduler refused the job (exit code {result.ExitCode}): {result.StandardError.Trim()}");
        }
    }

    private static string WindowsDay(DayOfWeek day) => day.ToString()[..3].ToUpperInvariant();

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
        var days = job.Repeat is ScheduleRepeats.Daily or ScheduleRepeats.None
            ? "*"
            : string.Join(',', (ScheduleRepeats.DaysOf(job.Repeat) ?? []).Select(d => (int)d));
        lines.Add(job.OnLogon
            ? $"@reboot {job.Command} {Marker(job.Name)}"
            : $"{int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)} {int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)} * * {days} {job.Command} {Marker(job.Name)}");
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
