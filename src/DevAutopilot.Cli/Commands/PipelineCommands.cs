using System.CommandLine;
using DevAutopilot.Core;
using DevAutopilot.Core.Jobs;
using DevAutopilot.Core.Local;
using DevAutopilot.Core.Pool;
using DevAutopilot.Core.Runs;
using DevAutopilot.Core.Pipeline;

namespace DevAutopilot.Cli.Commands;

/// <summary><c>triage [id]</c>, <c>implement &lt;id&gt;</c> and <c>run [--limit N]</c>.</summary>
public static class PipelineCommands
{
    public static Command CreateTriage(ProjectOptions projectOption, CliContext context)
    {
        var id = new Argument<string?>("id") { Description = "One work item id. Default: every open item.", Arity = ArgumentArity.ZeroOrOne };
        var source = SourceOption();
        var command = new Command("triage", "Sort work items into agent-ready and needs-human.") { id, source };
        command.SetAction((parse, ct) => WithPipelineAsync(projectOption.Resolve(parse), parse.GetValue(source), context, ct, async pipeline =>
        {
            var results = await pipeline.TriageAsync(parse.GetValue(id), ct).ConfigureAwait(false);
            await PrintTriageAsync(context.Out, results).ConfigureAwait(false);
            return results.Any(r => !r.Succeeded) ? ExitCodes.Failure : ExitCodes.Ok;
        }));
        return command;
    }

    public static Command CreateImplement(ProjectOptions projectOption, CliContext context)
    {
        var id = new Argument<string>("id") { Description = "Work item id; triage must have marked it agent-ready." };
        var source = SourceOption();
        var detach = new Option<bool>("--detach") { Description = "Start the run in the background and print the job id (see 'autopilot job')." };
        var job = new Option<string?>("--job") { Description = "Used by --detach: the job this process reports to.", Hidden = true };
        var command = new Command("implement", "Implement one agent-ready item: worktree, implementer, reviewer, merge gate, draft. Exit code 0 only when a draft was published.") { id, source, detach, job };
        command.SetAction(async (parse, ct) =>
        {
            var target = projectOption.Resolve(parse);
            var itemId = parse.GetRequiredValue(id);
            if (parse.GetValue(detach))
            {
                return await StartDetachedAsync(target, itemId, context).ConfigureAwait(false);
            }

            if (parse.GetValue(job) is { } jobId)
            {
                return await RunAsJobAsync(target, itemId, jobId, parse.GetValue(source), context, ct).ConfigureAwait(false);
            }

            return await WithPipelineAsync(target, parse.GetValue(source), context, ct, async pipeline =>
            {
                var result = await RecordedAsync(target, context, null, () => pipeline.ImplementAsync(itemId, ct)).ConfigureAwait(false);
                await PrintImplementAsync(context.Out, result).ConfigureAwait(false);
                return result.Outcome == ImplementOutcome.Published ? ExitCodes.Ok : ExitCodes.Failure;
            }).ConfigureAwait(false);
        });
        return command;
    }

    /// <summary>Runs one implement under the project's run lock and records the run, when the project is registered.</summary>
    private static async Task<ImplementResult> RecordedAsync(ProjectTarget target, CliContext context, string? jobId, Func<Task<ImplementResult>> implement)
    {
        if (target.Project is null || target.Workspace is null)
        {
            return await implement().ConfigureAwait(false);
        }

        using var held = new ProjectRunLock(target.Workspace, context.IsProcessAlive).Acquire(target.Project.Key, jobId is null ? "implement" : $"job {jobId}");
        var started = DateTimeOffset.UtcNow;
        var result = await implement().ConfigureAwait(false);
        new RunStore(target.Workspace, target.Project.Key).Record(result, started, jobId);
        return result;
    }

    private static async Task<int> StartDetachedAsync(ProjectTarget target, string itemId, CliContext context)
    {
        var (project, workspace) = target.RequireRegistered();
        if (!ProjectPaths.IsValidId(itemId))
        {
            throw new UsageException(ProjectPaths.InvalidIdMessage(itemId));
        }

        var (file, leading) = ProgramLocation.Current(context);
        var job = new JobService(workspace, project.Key, context.IsProcessAlive).Start(
            itemId,
            context.JobLauncher ?? new DetachedProcessLauncher(),
            file,
            jobId => [.. leading, "implement", itemId, "--job", jobId, "--project", project.Key, "--workspace", workspace.Root],
            Directory.GetCurrentDirectory());
        await context.Out.WriteLineAsync($"Started job {job.Id} for {itemId}. Follow it with 'autopilot job show {job.Id} --log --project {project.Key}'.").ConfigureAwait(false);
        return ExitCodes.Ok;
    }

    private static async Task<int> RunAsJobAsync(ProjectTarget target, string itemId, string jobId, string? source, CliContext context, CancellationToken ct)
    {
        var (project, workspace) = target.RequireRegistered();
        var jobs = new JobService(workspace, project.Key, context.IsProcessAlive);
        var job = jobs.Get(jobId) ?? throw new UsageException($"There is no job \"{jobId}\" in project {project.Key}.");
        using var log = new JobLogWriter(job.LogPath, step => jobs.SetStep(jobId, step));
        var jobContext = context with { Out = log, Error = log };
        jobs.MarkRunning(jobId, Environment.ProcessId);
        try
        {
            var exit = await WithPipelineAsync(target, source, jobContext, ct, async pipeline =>
            {
                var result = await RecordedAsync(target, context, jobId, () => pipeline.ImplementAsync(itemId, ct)).ConfigureAwait(false);
                await PrintImplementAsync(log, result).ConfigureAwait(false);
                jobs.Finish(jobId, result.Outcome == ImplementOutcome.Published ? JobStatus.Succeeded : JobStatus.Failed, result.Message);
                return result.Outcome == ImplementOutcome.Published ? ExitCodes.Ok : ExitCodes.Failure;
            }).ConfigureAwait(false);

            // Finish does nothing for a job that already ended, so this only marks a run that never got that far.
            jobs.Finish(jobId, JobStatus.Failed, "The job could not start; see the log.");
            return exit;
        }
        catch (OperationCanceledException)
        {
            jobs.Finish(jobId, JobStatus.Cancelled, "Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            await log.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
            jobs.Finish(jobId, JobStatus.Failed, ex.Message);
            return ExitCodes.Failure;
        }
    }

    public static Command CreateRun(ProjectOptions projectOption, CliContext context)
    {
        var limit = new Option<int?>("--limit") { Description = "Implement at most this many items." };
        var source = SourceOption();
        var command = new Command("run", "Triage every open item, then implement the agent-ready ones. Exit code 1 when something broke.") { limit, source };
        command.SetAction((parse, ct) =>
        {
            var max = parse.GetValue(limit);
            if (max is <= 0)
            {
                throw new UsageException("--limit must be greater than 0.");
            }

            var target = projectOption.Resolve(parse);
            return WithPipelineAsync(target, parse.GetValue(source), context, ct, async pipeline =>
            {
                var started = DateTimeOffset.UtcNow;
                var summary = await pipeline.RunAsync(max, ct).ConfigureAwait(false);
                if (target.Project is not null && target.Workspace is not null)
                {
                    var store = new RunStore(target.Workspace, target.Project.Key);
                    foreach (var implemented in summary.Implemented)
                    {
                        store.Record(implemented, started);
                    }
                }

                await PrintTriageAsync(context.Out, summary.Triage).ConfigureAwait(false);
                foreach (var result in summary.Implemented)
                {
                    await context.Out.WriteLineAsync().ConfigureAwait(false);
                    await PrintImplementAsync(context.Out, result).ConfigureAwait(false);
                }

                foreach (var skipped in summary.Skipped)
                {
                    await context.Out.WriteLineAsync($"Skipped {skipped}").ConfigureAwait(false);
                }

                var broke = summary.Triage.Any(t => !t.Succeeded) || summary.Implemented.Any(i => i.Outcome == ImplementOutcome.Failed);
                return broke ? ExitCodes.Failure : ExitCodes.Ok;
            });
        });
        return command;
    }

    /// <summary><c>--source local</c>: read <c>.autopilot/tasks/</c> for this run, whatever modules.workItems says.</summary>
    private static Option<string?> SourceOption()
    {
        var option = new Option<string?>("--source")
        {
            Description = "Where work items come from for this run. 'local' reads .autopilot/tasks/ (and keeps drafts local) even when modules.workItems is remote.",
        };
        option.AcceptOnlyFromAmong(ProjectServices.LocalSource);
        return option;
    }

    internal static async Task<int> WithPipelineAsync(ProjectTarget target, string? source, CliContext context, CancellationToken ct, Func<AutopilotPipeline, Task<int>> body)
    {
        AutopilotPipeline pipeline;
        try
        {
            pipeline = await ProjectServices.CreatePipelineAsync(target.RepoRoot, context, ct, source, target.DataFolder, target.LocalTasks).ConfigureAwait(false);
        }
        catch (ProjectSetupException ex)
        {
            await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        return await body(pipeline).ConfigureAwait(false);
    }

    private static async Task PrintTriageAsync(TextWriter output, IReadOnlyList<TriageItemResult> results)
    {
        if (results.Count == 0)
        {
            await output.WriteLineAsync("No open work items. Add task files to .autopilot/tasks/.").ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync("Triage").ConfigureAwait(false);
        foreach (var result in results)
        {
            if (result.Triage is null)
            {
                await output.WriteLineAsync($"  {result.WorkItemId}  ERROR        {result.Title}").ConfigureAwait(false);
                await output.WriteLineAsync($"        {result.Error}").ConfigureAwait(false);
                continue;
            }

            var label = TriageStore.Label(result.Triage.Label);
            var risk = result.Triage.Risk.ToString().ToLowerInvariant();
            await output.WriteLineAsync($"  {result.WorkItemId}  {label,-11}  {result.Title} (risk {risk})").ConfigureAwait(false);
            await output.WriteLineAsync($"        {result.Triage.Reason}").ConfigureAwait(false);
        }

        var ready = results.Count(r => r.Triage?.Label == TriageLabel.AgentReady);
        await output.WriteLineAsync($"{ready} agent-ready, {results.Count(r => r.Triage?.Label == TriageLabel.NeedsHuman)} needs-human, {results.Count(r => r.Triage is null)} error(s). Details in .autopilot/out/triage/.").ConfigureAwait(false);
    }

    internal static async Task PrintImplementAsync(TextWriter output, ImplementResult result)
    {
        var outcome = result.Outcome switch
        {
            ImplementOutcome.Published => "PUBLISHED, waiting for a person",
            ImplementOutcome.Rejected => "REJECTED",
            ImplementOutcome.NotReady => "NOT READY",
            ImplementOutcome.NotFound => "NOT FOUND",
            _ => "FAILED",
        };
        await output.WriteLineAsync($"{result.WorkItemId}: {outcome}").ConfigureAwait(false);
        await output.WriteLineAsync($"  {result.Message}").ConfigureAwait(false);
        if (result.Decision is { Findings.Count: > 0 } decision)
        {
            foreach (var finding in decision.Findings)
            {
                await output.WriteLineAsync($"  - [{finding.RuleId}] {finding.Message}{(finding.HighRisk ? " (high risk)" : string.Empty)}").ConfigureAwait(false);
            }
        }

        if (result.BranchName is not null)
        {
            await output.WriteLineAsync($"  Branch: {result.BranchName}").ConfigureAwait(false);
        }

        if (result.Location is not null)
        {
            await output.WriteLineAsync($"  Files:  {result.Location}").ConfigureAwait(false);
        }
    }
}
