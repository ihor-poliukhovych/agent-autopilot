using System.Globalization;
using Autopilot.Core.Config;
using Autopilot.Core.Git;
using Autopilot.Core.Local;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Tasks;

namespace Autopilot.Core.Pool;

/// <summary>
/// The real <see cref="IPoolTasks"/>: reads tasks from the project's work item source, triages through the
/// pipeline, writes answers and bug tasks into the workspace tasks, and merges into the integration branch.
/// Answers and bug tasks need workspace tasks; for a project whose tasks live in a tracker the pool master
/// cannot write them, so such tasks are parked instead.
/// </summary>
public sealed class PipelinePoolTasks(
    AutopilotPipeline pipeline,
    IWorkItemSource items,
    TriageStore triage,
    LocalTaskService? tasks,
    GitClient git,
    AutopilotConfig config,
    IntegrationService? integration) : IPoolTasks
{
    private readonly SemaphoreSlim _triageGate = new(1, 1);

    public async Task<PoolTaskInfo?> DescribeAsync(string workItemId, CancellationToken ct)
    {
        var item = await items.GetAsync(workItemId, ct).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var paths = PoolTaskHints.PathsFrom(item.Body);
        return new PoolTaskInfo(item.Title, item.Body, paths.Count > 0 ? paths : config.WritablePaths, PoolTaskHints.DependenciesFrom(item.Labels));
    }

    public async Task<PoolTriage> TriageStateAsync(string workItemId, CancellationToken ct)
    {
        var item = await items.GetAsync(workItemId, ct).ConfigureAwait(false);
        if (item is null)
        {
            return new PoolTriage(PoolTriageState.Missing, $"Work item {workItemId} was not found.");
        }

        var record = await triage.LoadRecordAsync(workItemId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return new PoolTriage(PoolTriageState.Missing, "The task has not been triaged yet.");
        }

        if (record.TextHash != TriageStore.TextHash(item))
        {
            return new PoolTriage(PoolTriageState.Missing, "The text of the task changed since it was triaged.");
        }

        return new PoolTriage(record.Result.Label == TriageLabel.AgentReady ? PoolTriageState.AgentReady : PoolTriageState.NeedsHuman, record.Result.Reason);
    }

    public Task AnswerAsync(string workItemId, string text, CancellationToken ct)
    {
        if (tasks is null || !LocalTaskService.TryParseNumber(workItemId, out var number) || tasks.Get(number) is not { } task)
        {
            throw new InvalidOperationException($"Task {workItemId} is not a workspace task, so the pool cannot write into it.");
        }

        var body = string.IsNullOrWhiteSpace(task.Body) ? text : task.Body.TrimEnd() + "\n\n" + text;
        tasks.Update(number, null, body, "pool master");
        return Task.CompletedTask;
    }

    public async Task<PoolTriage> RetriageAsync(string workItemId, CancellationToken ct)
    {
        // The pipeline triages in one shared worktree; two triages at once would remove each other's.
        await _triageGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var results = await pipeline.TriageAsync(workItemId, ct).ConfigureAwait(false);
            if (results.Count == 0 || results[0].Triage is null)
            {
                return new PoolTriage(PoolTriageState.Missing, (results.Count > 0 ? results[0].Error : null) ?? "The triage gave no result.");
            }
        }
        finally
        {
            _triageGate.Release();
        }

        // Read the stored result back: that is what implement will see.
        return await TriageStateAsync(workItemId, ct).ConfigureAwait(false);
    }

    public async Task PrepareRetryAsync(string workItemId, CancellationToken ct)
    {
        var branch = config.BranchPrefix + workItemId;
        if (!await git.BranchExistsAsync(branch, ct).ConfigureAwait(false))
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            var kept = $"{branch}-attempt-{attempt.ToString(CultureInfo.InvariantCulture)}";
            if (!await git.BranchExistsAsync(kept, ct).ConfigureAwait(false))
            {
                await git.RenameBranchAsync(branch, kept, ct).ConfigureAwait(false);
                return;
            }
        }
    }

    public Task<string> CreateBugAsync(string title, string body, CancellationToken ct)
    {
        if (tasks is null)
        {
            return Task.FromResult("(not created: this project's tasks live in a tracker)");
        }

        var bug = tasks.Add(title, body, ["bug", "from-pool"], "pool master");
        return Task.FromResult("#" + bug.Id);
    }

    public async Task<IntegrationResult?> IntegrateAsync(ImplementResult result, CancellationToken ct)
    {
        if (integration is null || !integration.Enabled || result.BranchName is null)
        {
            return null;
        }

        return await integration.MergeAsync(result.WorkItemId, result.BranchName, ct).ConfigureAwait(false);
    }
}
