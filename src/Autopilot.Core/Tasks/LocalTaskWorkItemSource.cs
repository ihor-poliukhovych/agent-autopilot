using System.Globalization;
using Autopilot.Core.Local;

namespace Autopilot.Core.Tasks;

/// <summary>
/// The workspace tasks of a project as a work item source, so triage, implement, the pool and
/// <c>list</c> read them like any other source. The id of an item is the task number.
/// The triage result is stored as before (<see cref="TriageStore"/>) and also appended to the task's history.
/// </summary>
public sealed class LocalTaskWorkItemSource(LocalTaskService tasks, TriageStore triage) : IWorkItemSource
{
    public Task<IReadOnlyList<WorkItem>> ListOpenAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WorkItem>>(tasks.List().Select(ToItem).ToList());

    public Task<WorkItem?> GetAsync(string id, CancellationToken ct) =>
        Task.FromResult(LocalTaskService.TryParseNumber(id, out var number) && tasks.Get(number) is { } task ? ToItem(task) : null);

    public async Task RecordTriageAsync(string id, TriageResult result, CancellationToken ct)
    {
        var item = await GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"There is no task {id}.");
        await triage.SaveAsync(item, result, ct).ConfigureAwait(false);
        tasks.AppendHistory(int.Parse(item.Id, CultureInfo.InvariantCulture), LocalTaskService.HistoryKind.Triage,
            $"Triage: {TriageStore.Label(result.Label)} ({result.Risk.ToString().ToLowerInvariant()} risk).", result.Reason);
    }

    public static WorkItem ToItem(LocalTask task) => new(task.Id, task.Title, task.Body, task.Labels, task.ExternalUrl, task.State);
}

/// <summary>
/// Appends the gate verdict and the draft location to the task's history after the inner publisher ran.
/// Only items whose id is a task number are recorded.
/// </summary>
public sealed class TaskHistoryPublisher(IPublisher inner, LocalTaskService tasks) : IPublisher
{
    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        var result = await inner.PublishAsync(request, ct).ConfigureAwait(false);
        if (LocalTaskService.TryParseNumber(request.WorkItem.Id, out var number) && tasks.Get(number) is not null)
        {
            var findings = request.Decision.Findings.Select(f => $"- {f.RuleId}: {f.Message}");
            tasks.AppendHistory(number, LocalTaskService.HistoryKind.Gate, $"Gate: {request.Decision.Outcome}.", string.Join('\n', findings));
            if (result.Succeeded)
            {
                tasks.AppendHistory(number, LocalTaskService.HistoryKind.Draft, $"Draft on branch {result.BranchName ?? request.BranchName}.", result.Location);
            }
        }

        return result;
    }
}
