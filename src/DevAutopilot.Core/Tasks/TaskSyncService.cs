using DevAutopilot.Core.Local;

namespace DevAutopilot.Core.Tasks;

public sealed record SyncImportReport(int Created, int Updated, int Unchanged);

/// <summary>Moves items between a tracker and the local tasks of a project.</summary>
public sealed class TaskSyncService(LocalTaskService tasks, IWorkItemTracker tracker)
{
    /// <summary>Creates or updates local tasks from the tracker's items, matched by external id.</summary>
    public async Task<SyncImportReport> ImportAsync(CancellationToken ct)
    {
        int created = 0, updated = 0, unchanged = 0;
        foreach (var item in await tracker.ReadAsync(ct).ConfigureAwait(false))
        {
            switch (tasks.Import(new TaskImport(item.ExternalId, item.Title, item.Body, item.Labels, item.Closed, item.Url)).Outcome)
            {
                case ImportOutcome.Created: created++; break;
                case ImportOutcome.Updated: updated++; break;
                default: unchanged++; break;
            }
        }

        return new SyncImportReport(created, updated, unchanged);
    }

    /// <summary>
    /// Writes the task's labels and state, its comments and agent results not sent before, and the newest
    /// draft link to the tracker. Marks them sent only after the write succeeded. Returns false when the task has no external id.
    /// </summary>
    public async Task<bool> ExportAsync(int number, CancellationToken ct)
    {
        var task = tasks.Get(number);
        if (task?.ExternalId is null)
        {
            return false;
        }

        var (comments, history) = tasks.Unsynced(number);
        var results = history.Where(h => h.Kind is LocalTaskService.HistoryKind.Triage or LocalTaskService.HistoryKind.Gate or LocalTaskService.HistoryKind.Draft).ToList();
        var texts = comments.Select(c => new TrackerComment(c.Author, c.Body))
            .Concat(results.Select(h => new TrackerComment("autopilot", h.Detail is null ? h.Summary : h.Summary + "\n\n" + h.Detail)))
            .ToList();
        var draft = results.LastOrDefault(h => h.Kind == LocalTaskService.HistoryKind.Draft)?.Detail;
        await tracker.WriteAsync(task.ExternalId, new TrackerWrite(task.Labels, texts, task.Closed, draft), ct).ConfigureAwait(false);
        tasks.MarkSynced(comments.Select(c => c.Id), history.Select(h => h.Id));
        return true;
    }
}

/// <summary>
/// The tracker for repository-local task files (<c>.autopilot/tasks/*.md</c>): it reads them as items
/// and writes nothing back, because the files are the person's input. Backs <c>task import-files</c>.
/// </summary>
public sealed class FileTaskTracker(LocalWorkItemSource files) : IWorkItemTracker
{
    public string Name => "files";

    public async Task<IReadOnlyList<TrackerItem>> ReadAsync(CancellationToken ct)
    {
        var items = new List<TrackerItem>();
        foreach (var path in Directory.Exists(files.TasksFolder) ? Directory.EnumerateFiles(files.TasksFolder, "*.md").Order(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>())
        {
            var (item, closed) = LocalWorkItemSource.Parse(Path.GetFileName(path), await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            items.Add(new TrackerItem("file:" + item.Id, item.Title, item.Body, item.Labels, closed));
        }

        return items;
    }

    public Task WriteAsync(string externalId, TrackerWrite write, CancellationToken ct) => Task.CompletedTask;
}
