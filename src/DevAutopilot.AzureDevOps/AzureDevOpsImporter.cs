using DevAutopilot.Core;
using DevAutopilot.Core.Local;
using DevAutopilot.Core.Tasks;

namespace DevAutopilot.AzureDevOps;

/// <summary>Counts of one import. <see cref="DryRun"/> says nothing was written.</summary>
public sealed record AzureDevOpsImportReport(int Created, int Updated, int Unchanged, bool DryRun);

/// <summary>
/// Reads the configured query (read only) and turns the work items into local tasks, matched by the
/// work item number. A second run creates nothing new and only updates what changed.
/// </summary>
public sealed class AzureDevOpsImporter(LocalTaskService tasks)
{
    /// <summary>Tracker states that count as finished; any other state keeps the task open.</summary>
    private static readonly string[] ClosedStates = ["Closed", "Done", "Removed", "Resolved", "Completed"];

    public async Task<AzureDevOpsImportReport> ImportAsync(IWorkItemSource source, int? limit, bool dryRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var items = await source.ListOpenAsync(ct).ConfigureAwait(false);
        int created = 0, updated = 0, unchanged = 0;
        foreach (var item in items.Take(limit is > 0 ? limit.Value : int.MaxValue))
        {
            var import = ToImport(item);
            var outcome = dryRun ? Predict(import) : tasks.Import(import).Outcome;
            switch (outcome)
            {
                case ImportOutcome.Created: created++; break;
                case ImportOutcome.Updated: updated++; break;
                default: unchanged++; break;
            }
        }

        return new AzureDevOpsImportReport(created, updated, unchanged, dryRun);
    }

    public static TaskImport ToImport(WorkItem item) =>
        new(item.Id, item.Title, item.Body, item.Labels, IsClosed(item.State), item.Url);

    public static bool IsClosed(string? state) =>
        state is not null && ClosedStates.Contains(state.Trim(), StringComparer.OrdinalIgnoreCase);

    private ImportOutcome Predict(TaskImport item)
    {
        var existing = tasks.FindByExternalId(item.ExternalId);
        if (existing is null)
        {
            return ImportOutcome.Created;
        }

        var labels = item.Labels.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        var same = existing.Title == item.Title.Trim() && existing.Body == item.Body && existing.Closed == item.Closed && existing.ExternalUrl == item.Url
            && existing.Labels.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(labels.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        return same ? ImportOutcome.Unchanged : ImportOutcome.Updated;
    }
}
