using DevAutopilot.Core.Local;
using DevAutopilot.Core.Pool;
using DevAutopilot.Core.Runs;

namespace DevAutopilot.Core.Tasks;

/// <summary>The stored triage of one item. <see cref="Stale"/> is true when the item's text changed after triage.</summary>
public sealed record TriageSummary(string Label, string Risk, string Reason, DateTimeOffset TriagedAt, bool Stale);

/// <summary>One open work item with everything a person needs to pick it: triage, last run, pool position.</summary>
public sealed record WorkItemListEntry(
    string Id,
    string Title,
    string? Url,
    string? State,
    string? AssignedTo,
    TriageSummary? Triage,
    RunRecord? LastRun,
    bool InPool,
    int? PoolPosition);

/// <summary>Lists the open work items of a project (the CLI's <c>list</c> and the web UI both call this).</summary>
public sealed class WorkItemListService(IWorkItemSource source, TriageStore triage, PoolService? pool = null, RunStore? runs = null)
{
    public async Task<IReadOnlyList<WorkItemListEntry>> ListAsync(CancellationToken ct)
    {
        var items = await source.ListOpenAsync(ct).ConfigureAwait(false);
        var inPool = (pool?.List() ?? []).ToDictionary(e => e.WorkItemId, e => e.Position, StringComparer.Ordinal);
        var lastRuns = runs?.LastRuns() ?? new Dictionary<string, RunRecord>();
        var entries = new List<WorkItemListEntry>();
        foreach (var item in items)
        {
            TriageSummary? summary = null;
            if (ProjectPaths.IsValidId(item.Id) && await triage.LoadRecordAsync(item.Id, ct).ConfigureAwait(false) is { } record)
            {
                // Same rule as the pipeline: triage counts only for the text it was made from.
                summary = new TriageSummary(
                    TriageStore.Label(record.Result.Label),
                    record.Result.Risk.ToString().ToLowerInvariant(),
                    record.Result.Reason,
                    record.TriagedAt,
                    !string.Equals(record.TextHash, TriageStore.TextHash(item), StringComparison.Ordinal));
            }

            entries.Add(new WorkItemListEntry(
                item.Id, item.Title, item.Url, item.State, item.AssignedTo, summary,
                lastRuns.GetValueOrDefault(item.Id),
                inPool.ContainsKey(item.Id), inPool.TryGetValue(item.Id, out var position) ? position : null));
        }

        return entries;
    }
}
