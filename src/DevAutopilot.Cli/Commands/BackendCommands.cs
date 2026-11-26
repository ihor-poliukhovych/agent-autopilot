using System.CommandLine;
using System.Text.Json;
using DevAutopilot.Core;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Jobs;
using DevAutopilot.Core.Local;
using DevAutopilot.Core.Pool;
using DevAutopilot.Core.Runs;
using DevAutopilot.Core.Scheduling;
using DevAutopilot.Core.Tasks;
using DevAutopilot.Core.Workspace;

namespace DevAutopilot.Cli.Commands;

/// <summary>
/// <c>list</c>, <c>pool</c>, <c>job</c>, <c>schedule</c>, <c>stats</c>, <c>runs</c> and <c>decide</c>. Each one only
/// parses its arguments and prints; the work is done by the services in DevAutopilot.Core.
/// </summary>
public static class BackendCommands
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IEnumerable<Command> Create(ProjectOptions options, CliContext context) =>
    [
        CreateList(options, context),
        CreatePool(options, context),
        CreateJob(options, context),
        CreateSchedule(options, context),
        CreateRuns(options, context),
        CreateStats(options, context),
        CreateDecide(options, context),
    ];

    private static Option<bool> JsonOption() => new("--json") { Description = "Print JSON." };

    private static ProjectRunLock LockFor(CliContext context, Workspace workspace) => new(workspace, context.IsProcessAlive);

    // ---------- list ----------

    private static Command CreateList(ProjectOptions options, CliContext context)
    {
        var json = JsonOption();
        var source = new Option<string?>("--source") { Description = "'local' reads the work items from the workspace task files even when the project uses a tracker." };
        source.AcceptOnlyFromAmong(ProjectServices.LocalSource);
        var command = new Command("list", "List the open work items with stored triage (stale when the text changed), last run and pool membership.") { json, source };
        command.SetAction(async (parse, ct) =>
        {
            var target = options.Resolve(parse);
            AutopilotConfig config;
            try
            {
                config = ConfigLoader.Load(target.RepoRoot, target.DataFolder);
            }
            catch (ConfigException ex)
            {
                await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            IWorkItemSource items;
            try
            {
                items = string.Equals(parse.GetValue(source), ProjectServices.LocalSource, StringComparison.Ordinal)
                    ? ProjectServices.CreateLocalModules(target.RepoRoot, target.DataFolder, target.LocalTasks).WorkItems
                    : ProjectServices.CreateModules(target.RepoRoot, config, target.DataFolder, target.LocalTasks).WorkItems;
            }
            catch (ProjectSetupException ex)
            {
                await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            PoolService? pool = null;
            RunStore? runs = null;
            if (target.Project is not null && target.Workspace is not null)
            {
                pool = new PoolService(target.Workspace, target.Project.Key);
                runs = new RunStore(target.Workspace, target.Project.Key);
            }

            var entries = await new WorkItemListService(items, new TriageStore(target.RepoRoot, target.DataFolder), pool, runs).ListAsync(ct).ConfigureAwait(false);
            if (parse.GetValue(json))
            {
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(entries, Json)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            if (entries.Count == 0)
            {
                await context.Out.WriteLineAsync("No open work items.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            foreach (var e in entries)
            {
                var triage = e.Triage is null ? "not triaged" : e.Triage.Label + (e.Triage.Stale ? " (stale)" : string.Empty);
                var run = e.LastRun is null ? "no run" : $"run {e.LastRun.Id} {e.LastRun.Outcome}";
                var pooled = e.InPool ? $"pool #{e.PoolPosition}" : "-";
                await context.Out.WriteLineAsync($"{e.Id,-8} {triage,-22} {pooled,-9} {run,-22} {e.Title}").ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    // ---------- pool ----------

    private static Command CreatePool(ProjectOptions options, CliContext context)
    {
        var command = new Command("pool", "The queue of work items for the agent. 'pool run' works through it one item at a time.");

        var ids = new Argument<string[]>("id") { Description = "Work item ids.", Arity = ArgumentArity.OneOrMore };
        var add = new Command("add", "Add work items to the end of the pool.") { ids };
        add.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var pool = new PoolService(workspace, project.Key);
            foreach (var id in parse.GetRequiredValue(ids))
            {
                await context.Out.WriteLineAsync(pool.Add(id) ? $"Added {id}." : $"{id} is already in the pool.").ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });
        command.Subcommands.Add(add);

        var removeId = new Argument<string>("id") { Description = "Work item id." };
        var remove = new Command("remove", "Take a work item out of the pool.") { removeId };
        remove.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var id = parse.GetRequiredValue(removeId);
            var removed = new PoolService(workspace, project.Key).Remove(id);
            await context.Out.WriteLineAsync(removed ? $"Removed {id}." : $"{id} is not in the pool.").ConfigureAwait(false);
            return removed ? ExitCodes.Ok : ExitCodes.Failure;
        });
        command.Subcommands.Add(remove);

        var json = JsonOption();
        var list = new Command("list", "Show the pool in run order.") { json };
        list.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var entries = new PoolService(workspace, project.Key).List();
            if (parse.GetValue(json))
            {
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(entries, Json)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            if (entries.Count == 0)
            {
                await context.Out.WriteLineAsync("The pool is empty. Add items with 'autopilot pool add <id>'.").ConfigureAwait(false);
            }

            foreach (var entry in entries)
            {
                await context.Out.WriteLineAsync($"{entry.Position,3}. {entry.WorkItemId}").ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });
        command.Subcommands.Add(list);

        var moveId = new Argument<string>("id") { Description = "Work item id." };
        var position = new Argument<int>("position") { Description = "New 1-based position." };
        var move = new Command("move", "Move a work item to a position in the pool.") { moveId, position };
        move.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var id = parse.GetRequiredValue(moveId);
            var moved = new PoolService(workspace, project.Key).Move(id, parse.GetRequiredValue(position));
            await context.Out.WriteLineAsync(moved ? $"Moved {id}." : $"{id} is not in the pool.").ConfigureAwait(false);
            return moved ? ExitCodes.Ok : ExitCodes.Failure;
        });
        command.Subcommands.Add(move);

        var clear = new Command("clear", "Empty the pool.");
        clear.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            await context.Out.WriteLineAsync($"Removed {new PoolService(workspace, project.Key).Clear()} item(s).").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        command.Subcommands.Add(clear);

        var limit = new Option<int?>("--limit") { Description = "Try at most this many items; the rest stay in the pool." };
        var run = new Command("run", $"Implement the pool one item at a time. Stops on infrastructure errors. Writes {PoolRunner.ReportFileName} to the project's workspace folder. Exit code 0 when nothing was rejected or stopped.") { limit };
        run.SetAction(async (parse, ct) =>
        {
            var max = parse.GetValue(limit);
            if (max is <= 0)
            {
                throw new UsageException("--limit must be greater than 0.");
            }

            var target = options.Resolve(parse);
            var (project, workspace) = target.RequireRegistered();
            var pipeline = await TryCreatePipelineAsync(target, context, ct).ConfigureAwait(false);
            if (pipeline is null)
            {
                return ExitCodes.Failure;
            }

            var runner = new PoolRunner(new PoolService(workspace, project.Key), new RunStore(workspace, project.Key), LockFor(context, workspace), project.Key, pipeline.ImplementAsync);
            var result = await runner.RunAsync(max, workspace.ProjectFolder(project.Key), ct).ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Done {result.Done.Count}, rejected {result.Rejected.Count}, needs a person {result.NeedsHuman.Count}, not reached {result.NotReached.Count}.").ConfigureAwait(false);
            if (result.StoppedBecause is not null)
            {
                await context.Error.WriteLineAsync(result.StoppedBecause).ConfigureAwait(false);
            }

            await context.Out.WriteLineAsync($"Report: {result.ReportPath}").ConfigureAwait(false);
            return result.Succeeded ? ExitCodes.Ok : ExitCodes.Failure;
        });
        command.Subcommands.Add(run);
        return command;
    }

    private static async Task<DevAutopilot.Core.Pipeline.AutopilotPipeline?> TryCreatePipelineAsync(ProjectTarget target, CliContext context, CancellationToken ct)
    {
        try
        {
            return await ProjectServices.CreatePipelineAsync(target.RepoRoot, context, ct, null, target.DataFolder, target.LocalTasks).ConfigureAwait(false);
        }
        catch (ProjectSetupException ex)
        {
            await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return null;
        }
    }

    // ---------- job ----------

    private static Command CreateJob(ProjectOptions options, CliContext context)
    {
        var command = new Command("job", "Background implement jobs started with 'autopilot implement <id> --detach'.");

        var json = JsonOption();
        var list = new Command("list", "List the jobs, newest first.") { json };
        list.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var jobs = new JobService(workspace, project.Key, context.IsProcessAlive).List();
            if (parse.GetValue(json))
            {
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(jobs, Json)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            if (jobs.Count == 0)
            {
                await context.Out.WriteLineAsync("No jobs.").ConfigureAwait(false);
            }

            foreach (var job in jobs)
            {
                await context.Out.WriteLineAsync($"{job.Id}  {job.Status.ToString().ToLowerInvariant(),-9} {job.WorkItemId,-8} {job.Step ?? "-"}").ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });
        command.Subcommands.Add(list);

        var showId = new Argument<string>("id") { Description = "Job id." };
        var showJson = JsonOption();
        var lines = new Option<int?>("--log") { Description = "Also print the last N lines of the job's log (default 20 with --log)." };
        var show = new Command("show", "Show one job.") { showId, showJson, lines };
        show.SetAction(async (parse, ct) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var jobs = new JobService(workspace, project.Key, context.IsProcessAlive);
            var id = parse.GetRequiredValue(showId);
            var job = jobs.Get(id) ?? throw new UsageException($"There is no job \"{id}\" in project {project.Key}.");
            if (parse.GetValue(showJson))
            {
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(job, Json)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            await context.Out.WriteLineAsync($"Job:       {job.Id}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Work item: {job.WorkItemId}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Status:    {job.Status.ToString().ToLowerInvariant()}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Step:      {job.Step ?? "-"}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Started:   {job.StartedAt?.ToString("u") ?? "-"}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Finished:  {job.FinishedAt?.ToString("u") ?? "-"}").ConfigureAwait(false);
            await context.Out.WriteLineAsync($"Log:       {job.LogPath}").ConfigureAwait(false);
            if (job.Message is not null)
            {
                await context.Out.WriteLineAsync($"Message:   {job.Message}").ConfigureAwait(false);
            }

            if (parse.GetResult(lines) is not null)
            {
                await context.Out.WriteLineAsync(await jobs.ReadLogAsync(id, parse.GetValue(lines) ?? 20, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });
        command.Subcommands.Add(show);

        var cancelId = new Argument<string>("id") { Description = "Job id." };
        var cancel = new Command("cancel", "Stop this one job. Other jobs keep running.") { cancelId };
        cancel.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var id = parse.GetRequiredValue(cancelId);
            var jobs = new JobService(workspace, project.Key, context.IsProcessAlive);
            var cancelled = jobs.Cancel(id, context.ProcessKiller ?? new ProcessTreeKiller(), LockFor(context, workspace));
            await context.Out.WriteLineAsync(cancelled ? $"Cancelled job {id}." : $"Job {id} is not running (or does not exist).").ConfigureAwait(false);
            return cancelled ? ExitCodes.Ok : ExitCodes.Failure;
        });
        command.Subcommands.Add(cancel);
        return command;
    }

    // ---------- schedule ----------

    private static Command CreateSchedule(ProjectOptions options, CliContext context)
    {
        var command = new Command("schedule", "Run the pool every night. Windows Task Scheduler for the current user, a crontab line elsewhere.");

        var at = new Option<string>("--at") { Description = "Time of day, HH:mm (24 hours, local time).", Required = true };
        var limit = new Option<int?>("--limit") { Description = "Try at most this many items per night." };
        var yes = new Option<bool>("--yes") { Description = "Install the schedule. Without it only the plan is printed." };
        var set = new Command("set", "Plan or install the night run of the pool.") { at, limit, yes };
        set.SetAction(async (parse, ct) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var service = new ScheduleService(workspace, project.Key, SchedulerFor(context));
            var plan = service.Plan(parse.GetRequiredValue(at), parse.GetValue(limit), ProgramCommandLine(context, project.Key, workspace));
            await context.Out.WriteLineAsync(plan.ToString()).ConfigureAwait(false);
            if (!parse.GetValue(yes))
            {
                await context.Out.WriteLineAsync("Nothing was installed. Add --yes to install it.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            await service.SetAsync(plan, ct).ConfigureAwait(false);
            await context.Out.WriteLineAsync("Installed.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        command.Subcommands.Add(set);

        var show = new Command("show", "Show the night schedule.");
        show.SetAction(async (parse, ct) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var state = await new ScheduleService(workspace, project.Key, SchedulerFor(context)).ShowAsync(ct).ConfigureAwait(false);
            if (state is null)
            {
                await context.Out.WriteLineAsync("No night schedule.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            await context.Out.WriteLineAsync(state.Plan.ToString()).ConfigureAwait(false);
            await context.Out.WriteLineAsync(state.InstalledInOs
                ? "It is installed."
                : $"It is NOT installed in {state.Plan.SchedulerDescription} any more. Run 'schedule set' again.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        command.Subcommands.Add(show);

        var remove = new Command("remove", "Remove the night schedule.");
        remove.SetAction(async (parse, ct) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var removed = await new ScheduleService(workspace, project.Key, SchedulerFor(context)).RemoveAsync(ct).ConfigureAwait(false);
            await context.Out.WriteLineAsync(removed ? "Removed the night schedule." : "There was no night schedule.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        command.Subcommands.Add(remove);
        return command;
    }

    private static IScheduler SchedulerFor(CliContext context) =>
        context.Scheduler ?? (OperatingSystem.IsWindows() ? new WindowsTaskScheduler() : new CronScheduler());

    /// <summary>The program with the global options a scheduled or detached run needs, quoted for a command line.</summary>
    private static string ProgramCommandLine(CliContext context, string projectKey, Workspace workspace)
    {
        var (file, arguments) = ProgramLocation.Current(context);
        return string.Join(' ', new[] { file }.Concat(arguments).Concat(["--project", projectKey, "--workspace", workspace.Root]).Select(ProgramLocation.Quote));
    }

    // ---------- runs, stats, decide ----------

    private static Command CreateRuns(ProjectOptions options, CliContext context)
    {
        var json = JsonOption();
        var limit = new Option<int?>("--limit") { Description = "Show only the newest N runs." };
        var command = new Command("runs", "List the implement runs: the gate's verdict and what a person decided.") { json, limit };
        command.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var runs = new RunStore(workspace, project.Key).List(null, parse.GetValue(limit));
            if (parse.GetValue(json))
            {
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(runs, Json)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            if (runs.Count == 0)
            {
                await context.Out.WriteLineAsync("No runs yet.").ConfigureAwait(false);
            }

            foreach (var r in runs)
            {
                await context.Out.WriteLineAsync($"{r.Id,4}  {r.WorkItemId,-8} {r.Outcome,-10} gate {r.GateVerdict ?? "-",-9} person {r.HumanDecision?.ToString().ToLowerInvariant() ?? "-"}").ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateStats(ProjectOptions options, CliContext context)
    {
        var json = JsonOption();
        var command = new Command("stats", "How often the gate and the person agreed (the base for trust levels).") { json };
        command.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var stats = new RunStore(workspace, project.Key).Stats();
            if (parse.GetValue(json))
            {
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(new { stats.TotalRuns, stats.GatePassed, stats.GateRejected, stats.Decided, stats.Agreed, stats.GateTooLenient, stats.GateTooStrict, stats.AgreementRate }, Json)).ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            await context.Out.WriteLineAsync($"Runs: {stats.TotalRuns} (gate passed {stats.GatePassed}, rejected {stats.GateRejected})").ConfigureAwait(false);
            await context.Out.WriteLineAsync(stats.Decided == 0
                ? "No person has decided on a run yet. Record it with 'autopilot decide <run-id> merged|rejected'."
                : $"Decided by a person: {stats.Decided}. Agreed with the gate: {stats.Agreed} ({stats.AgreementRate:P0}). " +
                  $"Gate too lenient: {stats.GateTooLenient}. Gate too strict: {stats.GateTooStrict}.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateDecide(ProjectOptions options, CliContext context)
    {
        var id = new Argument<long>("run-id") { Description = "Run id from 'autopilot runs'." };
        var decision = new Argument<string>("decision") { Description = "What the person did with the draft." };
        decision.AcceptOnlyFromAmong("merged", "rejected");
        var command = new Command("decide", "Record whether a person merged or rejected a run's draft, so agreement with the gate can be counted.") { id, decision };
        command.SetAction(async (parse, _) =>
        {
            var (project, workspace) = options.Resolve(parse).RequireRegistered();
            var runId = parse.GetRequiredValue(id);
            var recorded = new RunStore(workspace, project.Key).RecordDecision(runId, Enum.Parse<HumanDecision>(parse.GetRequiredValue(decision), ignoreCase: true));
            await context.Out.WriteLineAsync(recorded ? $"Recorded for run {runId}." : $"There is no run {runId} in project {project.Key}.").ConfigureAwait(false);
            return recorded ? ExitCodes.Ok : ExitCodes.Failure;
        });
        return command;
    }
}

/// <summary>Finds the program that detached and scheduled runs start.</summary>
public static class ProgramLocation
{
    public static (string File, IReadOnlyList<string> Arguments) Current(CliContext context)
    {
        if (context.Program is { } given)
        {
            return given;
        }

        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot tell which program is running.");
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entry))
        {
            var appHost = HookCommandLine.AppHostPath(entry, OperatingSystem.IsWindows());
            return appHost is not null && File.Exists(appHost) ? (appHost, []) : (processPath, [entry]);
        }

        return (processPath, []);
    }

    public static string Quote(string part) => part.Length > 0 && part.All(c => !char.IsWhiteSpace(c) && c != '"') ? part : "\"" + part + "\"";
}
