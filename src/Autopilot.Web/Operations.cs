using System.Text.Json;
using Autopilot.Core.Jobs;
using Autopilot.Core.Local;
using Autopilot.Core.Pool;
using Autopilot.Core.Runs;
using Autopilot.Core.Scheduling;
using Autopilot.Core.Tasks;
using Autopilot.Core.Workspace;

namespace Autopilot.Web;

public sealed record TaskListView(
    IReadOnlyList<WorkItemListEntry> Items,
    bool LocalTasks,
    bool LegacyTaskFiles,
    IReadOnlyDictionary<string, string> ActiveJobs,
    RunStatus Status);

/// <summary>Who holds the project's run lock right now, if anyone.</summary>
public sealed record RunStatus(bool Busy, int? Pid, string? Owner);

public sealed record NeedsHumanItem(string Id, string Title, string Question, bool Stale, bool CanAnswer);

/// <summary>Tasks: lists, local task changes, starting a run. Every method is one call into the core.</summary>
public sealed class TaskOperations(WebUiOptions options)
{
    public async Task<TaskListView> ListAsync(ProjectScope scope, CancellationToken ct)
    {
        var items = await scope.CreateList().ListAsync(ct).ConfigureAwait(false);
        var active = scope.Jobs.List().Where(j => j.IsActive).GroupBy(j => j.WorkItemId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        return new TaskListView(items, scope.LocalTasks is not null, scope.LegacyTaskFiles, active, Status(scope));
    }

    public static RunStatus Status(ProjectScope scope) =>
        scope.Lock.Holder(scope.Project.Key) is { } held ? new RunStatus(true, held.Pid, held.Owner) : new RunStatus(false, null, null);

    public LocalTaskDetail Detail(ProjectScope scope, int number) =>
        Local(scope).Show(number) ?? throw new NotFoundException($"There is no task {number}.");

    public LocalTask Add(ProjectScope scope, string title, string? body, IEnumerable<string>? labels) =>
        Local(scope).Add(title, body, labels, Author());

    public void Update(ProjectScope scope, int number, string? title, string? body, IEnumerable<string>? labels)
    {
        var tasks = Local(scope);
        tasks.Update(number, title, body, Author());
        if (labels is not null)
        {
            var current = (tasks.Get(number) ?? throw new NotFoundException($"There is no task {number}.")).Labels;
            var wanted = labels.SelectMany(l => l.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
            tasks.ChangeLabels(number, wanted, current.Where(c => !wanted.Contains(c, StringComparer.OrdinalIgnoreCase)));
        }
    }

    public TaskComment Comment(ProjectScope scope, int number, string text) => Local(scope).Comment(number, text, Author());

    public bool SetClosed(ProjectScope scope, int number, bool closed) =>
        closed ? Local(scope).Close(number, Author()) : Local(scope).Reopen(number, Author());

    /// <summary>Starts one implement job in the background. Refused while another run holds the project.</summary>
    public JobRecord Start(ProjectScope scope, string id)
    {
        if (!ProjectPaths.IsValidId(id))
        {
            throw new ArgumentException(ProjectPaths.InvalidIdMessage(id));
        }

        RequireIdle(scope);
        return scope.Jobs.Start(
            id,
            options.Launcher,
            options.Program.File,
            jobId => [.. options.Program.Arguments, "implement", id, "--job", jobId, "--project", scope.Project.Key, "--workspace", scope.Workspace.Root],
            scope.Project.RepositoryFolder);
    }

    /// <summary>Starts the triage of one item in the background (it writes the result next to the other triage files).</summary>
    public void StartTriage(ProjectScope scope, string id)
    {
        if (!ProjectPaths.IsValidId(id))
        {
            throw new ArgumentException(ProjectPaths.InvalidIdMessage(id));
        }

        options.Launcher.Launch(
            options.Program.File,
            [.. options.Program.Arguments, "triage", id, "--project", scope.Project.Key, "--workspace", scope.Workspace.Root],
            scope.Project.RepositoryFolder);
    }

    public async Task<IReadOnlyList<NeedsHumanItem>> NeedsHumanAsync(ProjectScope scope, CancellationToken ct)
    {
        var items = await scope.CreateList().ListAsync(ct).ConfigureAwait(false);
        return [.. items
            .Where(i => i.Triage is { Label: "needs-human" })
            .Select(i => new NeedsHumanItem(i.Id, i.Title, i.Triage!.Reason, i.Triage.Stale, scope.LocalTasks is not null))];
    }

    /// <summary>
    /// Stores a person's answer on the task: as a comment, and as a section of the text so that the next
    /// triage reads it (the changed text also marks the old triage stale).
    /// </summary>
    public void Answer(ProjectScope scope, int number, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new ArgumentException("Write the answer first.");
        }

        var tasks = Local(scope);
        var task = tasks.Get(number) ?? throw new NotFoundException($"There is no task {number}.");
        var text = answer.Trim();
        tasks.Comment(number, "Answer to the triage question:\n\n" + text, Author());
        tasks.Update(number, null, task.Body.TrimEnd() + "\n\n## Answer from a person\n\n" + text + "\n", Author());
    }

    internal static void RequireIdle(ProjectScope scope)
    {
        if (scope.Lock.Holder(scope.Project.Key) is { } held)
        {
            throw new ConflictException($"A run is already active in this project ({held.Owner}, process {held.Pid}). Wait for it to end.");
        }
    }

    private static LocalTaskService Local(ProjectScope scope) =>
        scope.LocalTasks ?? throw new ConflictException(scope.LegacyTaskFiles
            ? "This project still has task files. Run 'autopilot task import-files' to move them into the workspace."
            : "The tasks of this project live in its tracker; change them there.");

    private static string Author() => LocalTaskService.DefaultAuthor();
}

public sealed record PoolItemView(string Id, string Title, int Position, string? Triage, string? Risk, bool Stale);

public sealed record SchedulePlanView(string ProjectKey, string JobName, string AtTime, int? Limit, string Command, string Scheduler, string Summary, IReadOnlyList<PoolItemView> Items);

public sealed record ScheduleView(bool Set, bool InstalledInOs, SchedulePlanView? Plan);

/// <summary>The pool and its runs: order, remove, run now, run at night.</summary>
public sealed class PoolOperations(WebUiOptions options)
{
    public async Task<IReadOnlyList<PoolItemView>> ListAsync(ProjectScope scope, CancellationToken ct)
    {
        var entries = scope.Pool.List();
        if (entries.Count == 0)
        {
            return [];
        }

        var titles = (await scope.CreateList().ListAsync(ct).ConfigureAwait(false)).ToDictionary(e => e.Id, StringComparer.Ordinal);
        return [.. entries.Select(e => titles.TryGetValue(e.WorkItemId, out var item)
            ? new PoolItemView(e.WorkItemId, item.Title, e.Position, item.Triage?.Label, item.Triage?.Risk, item.Triage?.Stale ?? false)
            : new PoolItemView(e.WorkItemId, "(not open any more)", e.Position, null, null, false))];
    }

    public bool Add(ProjectScope scope, string id) =>
        ProjectPaths.IsValidId(id) ? scope.Pool.Add(id) : throw new ArgumentException(ProjectPaths.InvalidIdMessage(id));

    public bool Remove(ProjectScope scope, string id) => scope.Pool.Remove(id);

    public bool Move(ProjectScope scope, string id, int position) => scope.Pool.Move(id, position);

    /// <summary>Starts <c>pool run</c> in the background. The run takes the project lock itself; this only checks it is free.</summary>
    public int RunNow(ProjectScope scope, int? limit)
    {
        if (limit is <= 0)
        {
            throw new ArgumentException("The limit must be greater than 0.");
        }

        if (scope.Pool.List().Count == 0)
        {
            throw new ConflictException("The pool is empty. Add tasks to it first.");
        }

        TaskOperations.RequireIdle(scope);
        List<string> args = [.. options.Program.Arguments, "pool", "run", "--project", scope.Project.Key, "--workspace", scope.Workspace.Root];
        if (limit is { } n)
        {
            args.AddRange(["--limit", n.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        return options.Launcher.Launch(options.Program.File, args, scope.Project.RepositoryFolder);
    }

    public async Task<SchedulePlanView> PlanAsync(ProjectScope scope, string at, int? limit, CancellationToken ct)
    {
        var plan = PlanOf(scope, at, limit);
        return new SchedulePlanView(plan.ProjectKey, plan.JobName, plan.AtTime, plan.Limit, plan.Command, plan.SchedulerDescription, plan.ToString(), await ListAsync(scope, ct).ConfigureAwait(false));
    }

    /// <summary>Installs the night run. The caller must say it confirmed the plan it was shown.</summary>
    public async Task SetAsync(ProjectScope scope, string at, int? limit, bool confirmed, CancellationToken ct)
    {
        if (!confirmed)
        {
            throw new ArgumentException("Confirm the plan first.");
        }

        await scope.Schedule.Create().SetAsync(PlanOf(scope, at, limit), ct).ConfigureAwait(false);
    }

    public async Task<ScheduleView> ShowAsync(ProjectScope scope, CancellationToken ct)
    {
        var state = await scope.Schedule.Create().ShowAsync(ct).ConfigureAwait(false);
        if (state is null)
        {
            return new ScheduleView(false, false, null);
        }

        var p = state.Plan;
        return new ScheduleView(true, state.InstalledInOs, new SchedulePlanView(p.ProjectKey, p.JobName, p.AtTime, p.Limit, p.Command, p.SchedulerDescription, p.ToString(), []));
    }

    public Task<bool> RemoveScheduleAsync(ProjectScope scope, CancellationToken ct) => scope.Schedule.Create().RemoveAsync(ct);

    private static SchedulePlan PlanOf(ProjectScope scope, string at, int? limit) =>
        scope.Schedule.Create().Plan(at, limit, scope.ProgramCommandLine());
}

public sealed record RunOverview(
    IReadOnlyList<RunRecord> Runs,
    RunStatsView Stats,
    IReadOnlyList<JobView> Jobs,
    RunStatus Status);

public sealed record RunStatsView(int TotalRuns, int GatePassed, int GateRejected, int Decided, int Agreed, int GateTooLenient, int GateTooStrict, double? AgreementRate);

public sealed record JobView(JobRecord Job, string? LogTail);

public sealed record RunDetail(RunRecord Run, JsonElement? Gate, string? Report, string? Diff, bool DiffTruncated, string? GateLog);

/// <summary>Run history, one run's gate report and diff, jobs and the Stop button.</summary>
public sealed class RunOperations(WebUiOptions options)
{
    public const int MaxDiffChars = 400_000;
    public const int LogLines = 60;

    public async Task<RunOverview> OverviewAsync(ProjectScope scope, CancellationToken ct)
    {
        var s = scope.Runs.Stats();
        var jobs = new List<JobView>();
        foreach (var job in scope.Jobs.List().Take(15))
        {
            jobs.Add(new JobView(job, job.IsActive ? await TailAsync(scope, job.Id, ct).ConfigureAwait(false) : null));
        }

        return new RunOverview(
            scope.Runs.List(null, 100),
            new RunStatsView(s.TotalRuns, s.GatePassed, s.GateRejected, s.Decided, s.Agreed, s.GateTooLenient, s.GateTooStrict, s.AgreementRate),
            jobs,
            TaskOperations.Status(scope));
    }

    public async Task<JobView> JobAsync(ProjectScope scope, string id, CancellationToken ct)
    {
        var job = scope.Jobs.Get(id) ?? throw new NotFoundException($"There is no job \"{id}\".");
        return new JobView(job, await TailAsync(scope, id, ct).ConfigureAwait(false));
    }

    /// <summary>Stops this one job and the processes it started. Other jobs and the pool run are not touched.</summary>
    public bool Stop(ProjectScope scope, string id)
    {
        _ = scope.Jobs.Get(id) ?? throw new NotFoundException($"There is no job \"{id}\".");
        return scope.Jobs.Cancel(id, options.Killer, scope.Lock);
    }

    public async Task<RunDetail> DetailAsync(ProjectScope scope, long id, CancellationToken ct)
    {
        var run = scope.Runs.Get(id) ?? throw new NotFoundException($"There is no run {id}.");
        if (!ProjectPaths.IsValidId(run.WorkItemId))
        {
            return new RunDetail(run, null, null, null, false, null);
        }

        var folder = ProjectPaths.RunFolder(scope.Project.RepositoryFolder, run.WorkItemId, scope.DataFolder);
        JsonElement? gate = null;
        if (await ReadAsync(Path.Combine(folder, GateReport.FileName), ct).ConfigureAwait(false) is { } json)
        {
            try
            {
                gate = JsonDocument.Parse(json).RootElement.Clone();
            }
            catch (JsonException)
            {
                gate = null;
            }
        }

        var report = await ReadAsync(Path.Combine(folder, "PULL_REQUEST.md"), ct).ConfigureAwait(false)
            ?? await ReadAsync(Path.Combine(folder, "REJECTED.md"), ct).ConfigureAwait(false);
        var diff = await ReadAsync(Path.Combine(folder, "change.patch"), ct).ConfigureAwait(false);
        var truncated = diff is not null && diff.Length > MaxDiffChars;
        var gateLog = await ReadAsync(Path.Combine(folder, "gate-log.txt"), ct).ConfigureAwait(false);
        return new RunDetail(run, gate, report, truncated ? diff![..MaxDiffChars] : diff, truncated, gateLog is { Length: > 20_000 } ? gateLog[^20_000..] : gateLog);
    }

    public bool Decide(ProjectScope scope, long id, string decision) =>
        Enum.TryParse<HumanDecision>(decision, ignoreCase: true, out var parsed)
            ? scope.Runs.RecordDecision(id, parsed)
            : throw new ArgumentException("The decision must be \"merged\" or \"rejected\".");

    private static async Task<string?> TailAsync(ProjectScope scope, string id, CancellationToken ct)
    {
        try
        {
            return await scope.Jobs.ReadLogAsync(id, LogLines, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadAsync(string path, CancellationToken ct) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;
}
