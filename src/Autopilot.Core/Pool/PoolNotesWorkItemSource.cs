namespace Autopilot.Core.Pool;

/// <summary>
/// A work item source that adds the pool master's notes (kept in the workspace) to the end of the item text, so the
/// next attempt sees them. For items that live in a tracker, where the pool cannot edit the text.
/// <see cref="Local.TriageStore.TextHash"/> ignores everything from <see cref="Marker"/> on, so notes do not
/// make an earlier triage look out of date.
/// </summary>
public sealed class PoolNotesWorkItemSource(IWorkItemSource inner, PoolStateStore state) : IWorkItemSource
{
    public const string Marker = "\n\n--- Notes for the next attempt (pool master) ---\n";

    public async Task<IReadOnlyList<WorkItem>> ListOpenAsync(CancellationToken ct) =>
        await inner.ListOpenAsync(ct).ConfigureAwait(false);

    public async Task<WorkItem?> GetAsync(string id, CancellationToken ct)
    {
        var item = await inner.GetAsync(id, ct).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var notes = state.Notes(item.Id);
        return notes.Count == 0 ? item : item with { Body = item.Body + Marker + string.Join("\n\n", notes) };
    }

    public Task RecordTriageAsync(string id, TriageResult result, CancellationToken ct) =>
        inner.RecordTriageAsync(id, result, ct);
}
