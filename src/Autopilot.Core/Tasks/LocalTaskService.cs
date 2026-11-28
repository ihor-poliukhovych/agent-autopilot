using System.Globalization;
using System.Text.Json;
using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Tasks;

public sealed record LocalTask(
    int Number,
    string Title,
    string Body,
    bool Closed,
    IReadOnlyList<string> Labels,
    string? ExternalId,
    string? ExternalUrl,
    string Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    int? ParentNumber = null)
{
    public string Id => Number.ToString(CultureInfo.InvariantCulture);

    public string State => Closed ? LocalTaskService.ClosedState : LocalTaskService.OpenState;
}

/// <summary>The kinds of a task group, stored as a label "kind:&lt;name&gt;".</summary>
public static class TaskKinds
{
    public const string Prefix = "kind:";

    public static readonly IReadOnlyList<string> All = ["epic", "feature", "sprint", "release"];

    /// <summary>The kind in a label list, or null when there is no valid kind label.</summary>
    public static string? FromLabels(IEnumerable<string> labels) =>
        labels.Where(l => l.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            .Select(l => l[Prefix.Length..].Trim().ToLowerInvariant())
            .FirstOrDefault(All.Contains);
}

/// <summary>A task with its children. A task with at least one child is a group.</summary>
public sealed record TaskNode(LocalTask Task, IReadOnlyList<TaskNode> Children)
{
    public bool IsGroup => Children.Count > 0;

    public string? Kind => TaskKinds.FromLabels(Task.Labels);
}

public sealed record TaskComment(long Id, int TaskNumber, string Author, string Body, DateTimeOffset CreatedAt, bool Synced);

/// <summary>One entry in a task's history: a change by a person or a result of the agent.</summary>
public sealed record TaskHistoryEntry(long Id, int TaskNumber, string Kind, string Summary, string? Detail, DateTimeOffset CreatedAt, bool Synced);

public sealed record LocalTaskDetail(LocalTask Task, IReadOnlyList<TaskComment> Comments, IReadOnlyList<TaskHistoryEntry> History);

/// <summary>An item to create or update from outside (a tracker or a task file).</summary>
public sealed record TaskImport(string ExternalId, string Title, string Body, IReadOnlyList<string> Labels, bool Closed, string? Url = null);

public enum ImportOutcome
{
    Created,
    Updated,
    Unchanged,
}

/// <summary>
/// Tasks of one project, kept in the workspace database. This is the hub model: a project without a tracker
/// uses it directly, and a tracker adapter (<see cref="IWorkItemTracker"/>) syncs to it. Numbers are per project,
/// start at 1 and are never reused. The logic lives here so the CLI and the web UI share it.
/// </summary>
public sealed class LocalTaskService(Workspace.Workspace workspace, string projectKey)
{
    public const string OpenState = "open";
    public const string ClosedState = "closed";
    public const int MaxTitleLength = 300;

    /// <summary>History kinds written by the service and the agent.</summary>
    public static class HistoryKind
    {
        public const string Created = "created";
        public const string Imported = "imported";
        public const string Updated = "updated";
        public const string Closed = "closed";
        public const string Reopened = "reopened";
        public const string Labels = "labels";
        public const string Triage = "triage";
        public const string Gate = "gate";
        public const string Draft = "draft";
        public const string Split = "split";
    }

    public LocalTask Add(string title, string? body = null, IEnumerable<string>? labels = null, string? author = null) =>
        Create(RequireTitle(title), body ?? string.Empty, NormalizeLabels(labels), author ?? DefaultAuthor(), null, null, false, HistoryKind.Created, "Task created.");

    /// <summary>
    /// Changes the title and body of a task. A null part is left as it is. Returns false when nothing changed.
    /// A changed text makes stored triage stale (it is matched by a hash of the text).
    /// </summary>
    public bool Update(int number, string? title, string? body, string? author = null)
    {
        using var connection = workspace.Open();
        using var transaction = connection.BeginTransaction();
        var task = RequireTask(connection, number);
        var newTitle = title is null ? task.Title : RequireTitle(title);
        var newBody = body ?? task.Body;
        if (newTitle == task.Title && newBody == task.Body)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        Db.Execute(connection, "UPDATE tasks SET title = $title, body = $body, updated_at = $t WHERE project_key = $k AND number = $n",
            ("$title", newTitle), ("$body", newBody), ("$t", Db.Stamp(now)), ("$k", projectKey), ("$n", number));
        AddHistory(connection, number, HistoryKind.Updated, $"Text changed by {author ?? DefaultAuthor()}.", null, now);
        transaction.Commit();
        return true;
    }

    public IReadOnlyList<LocalTask> List(bool includeClosed = false, string? label = null)
    {
        using var connection = workspace.Open();
        var tasks = Db.Query(connection, ReadTask,
            $"{Select} WHERE project_key = $k {(includeClosed ? string.Empty : "AND state = 'open'")} ORDER BY number", ("$k", projectKey));
        return label is null
            ? tasks
            : tasks.Where(t => t.Labels.Contains(label, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The tasks created as children of a task (see <see cref="AddChild"/>), in number order.</summary>
    public IReadOnlyList<LocalTask> Children(int parentNumber)
    {
        using var connection = workspace.Open();
        return ChildrenCore(connection, parentNumber);
    }

    /// <summary>Creates a task linked to a parent task. The parent must exist.</summary>
    public LocalTask AddChild(int parentNumber, string title, string? body = null, IEnumerable<string>? labels = null, string? author = null)
    {
        using (var connection = workspace.Open())
        {
            RequireTask(connection, parentNumber);
        }

        return Create(RequireTitle(title), body ?? string.Empty, NormalizeLabels(labels), author ?? DefaultAuthor(), null, null, false,
            HistoryKind.Created, $"Task created as part of #{parentNumber}.", parentNumber);
    }

    /// <summary>
    /// Sets or clears (null) the parent of a task and returns the updated task. An unknown task or parent, the task
    /// itself, or a descendant of the task (a cycle) is rejected and nothing changes.
    /// </summary>
    public LocalTask SetParent(int number, int? parentNumber)
    {
        using var connection = workspace.Open();
        using var transaction = connection.BeginTransaction();
        var task = RequireTask(connection, number);
        if (parentNumber is { } parent)
        {
            RequireTask(connection, parent);
            if (parent == number)
            {
                throw new WorkspaceException($"Task {number} cannot be its own parent.");
            }

            // Walk up from the new parent; reaching the task means the parent is a descendant of it.
            var seen = new HashSet<int>();
            for (int? at = parent; at is { } current && seen.Add(current); at = GetCore(connection, current)?.ParentNumber)
            {
                if (current == number)
                {
                    throw new WorkspaceException($"Task {parent} is a descendant of task {number}; it cannot be its parent.");
                }
            }
        }

        if (task.ParentNumber != parentNumber)
        {
            var now = DateTimeOffset.UtcNow;
            Db.Execute(connection, "UPDATE tasks SET parent_number = $p, updated_at = $t WHERE project_key = $k AND number = $n",
                ("$p", parentNumber), ("$t", Db.Stamp(now)), ("$k", projectKey), ("$n", number));
            AddHistory(connection, number, HistoryKind.Updated, parentNumber is null ? "Parent cleared." : $"Parent set to #{parentNumber}.", null, now);
        }

        transaction.Commit();
        return GetCore(connection, number)!;
    }

    /// <summary>All tasks of the project as a tree: tasks without a parent are roots, children are ordered by number.</summary>
    public IReadOnlyList<TaskNode> Tree()
    {
        using var connection = workspace.Open();
        var all = Db.Query(connection, ReadTask, $"{Select} WHERE project_key = $k ORDER BY number", ("$k", projectKey));
        var numbers = all.Select(t => t.Number).ToHashSet();
        var byParent = all.Where(t => t.ParentNumber is { } p && numbers.Contains(p)).ToLookup(t => t.ParentNumber!.Value);
        TaskNode Build(LocalTask task) => new(task, byParent[task.Number].Select(Build).ToList());
        return all.Where(t => t.ParentNumber is not { } p || !numbers.Contains(p)).Select(Build).ToList();
    }

    /// <summary>Sets the kind label ("kind:epic" and so on) and removes any other "kind:" label.</summary>
    public IReadOnlyList<string> SetKind(int number, string kind)
    {
        var name = kind?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!TaskKinds.All.Contains(name))
        {
            throw new WorkspaceException($"A task kind must be one of: {string.Join(", ", TaskKinds.All)}.");
        }

        var task = Get(number) ?? throw new WorkspaceException($"There is no task {number} in project {projectKey}.");
        var old = task.Labels.Where(l => l.StartsWith(TaskKinds.Prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        return ChangeLabels(number, [TaskKinds.Prefix + name], old.Where(l => !string.Equals(l, TaskKinds.Prefix + name, StringComparison.OrdinalIgnoreCase)));
    }

    public LocalTask? Get(int number)
    {
        using var connection = workspace.Open();
        return GetCore(connection, number);
    }

    /// <summary>Accepts "12" or "#12".</summary>
    public static bool TryParseNumber(string? text, out int number) =>
        int.TryParse(text?.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    public LocalTask? FindByExternalId(string externalId)
    {
        using var connection = workspace.Open();
        return Db.Query(connection, ReadTask, $"{Select} WHERE project_key = $k AND external_id = $e", ("$k", projectKey), ("$e", externalId)).FirstOrDefault();
    }

    public LocalTaskDetail? Show(int number)
    {
        using var connection = workspace.Open();
        var task = GetCore(connection, number);
        if (task is null)
        {
            return null;
        }

        var comments = Db.Query(connection,
            r => new TaskComment(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), Db.ParseStamp(r.GetString(4)), !r.IsDBNull(5)),
            "SELECT id, task_number, author, body, created_at, synced_at FROM task_comments WHERE project_key = $k AND task_number = $n ORDER BY id",
            ("$k", projectKey), ("$n", number));
        var history = Db.Query(connection,
            r => new TaskHistoryEntry(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), Db.StringOrNull(r, 4), Db.ParseStamp(r.GetString(5)), !r.IsDBNull(6)),
            "SELECT id, task_number, kind, summary, detail, created_at, synced_at FROM task_history WHERE project_key = $k AND task_number = $n ORDER BY id",
            ("$k", projectKey), ("$n", number));
        return new LocalTaskDetail(task, comments, history);
    }

    public TaskComment Comment(int number, string text, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new WorkspaceException("A comment cannot be empty.");
        }

        using var connection = workspace.Open();
        RequireTask(connection, number);
        var now = DateTimeOffset.UtcNow;
        var who = author ?? DefaultAuthor();
        using var command = Db.Command(connection,
            "INSERT INTO task_comments (project_key, task_number, author, body, created_at) VALUES ($k, $n, $a, $b, $t); SELECT last_insert_rowid()",
            ("$k", projectKey), ("$n", number), ("$a", who), ("$b", text.Trim()), ("$t", Db.Stamp(now)));
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        Db.Execute(connection, "UPDATE tasks SET updated_at = $t WHERE project_key = $k AND number = $n", ("$t", Db.Stamp(now)), ("$k", projectKey), ("$n", number));
        return new TaskComment(id, number, who, text.Trim(), now, false);
    }

    /// <summary>Closes a task. Returns false when it was already closed.</summary>
    public bool Close(int number, string? author = null) => SetState(number, true, author);

    /// <summary>Reopens a task. Returns false when it was already open.</summary>
    public bool Reopen(int number, string? author = null) => SetState(number, false, author);

    /// <summary>Adds and removes labels (case-insensitive). Returns the labels after the change.</summary>
    public IReadOnlyList<string> ChangeLabels(int number, IEnumerable<string>? add, IEnumerable<string>? remove)
    {
        using var connection = workspace.Open();
        using var transaction = connection.BeginTransaction();
        var task = RequireTask(connection, number);
        var labels = task.Labels.ToList();
        var removed = NormalizeLabels(remove);
        labels.RemoveAll(l => removed.Contains(l, StringComparer.OrdinalIgnoreCase));
        foreach (var label in NormalizeLabels(add))
        {
            if (!labels.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                labels.Add(label);
            }
        }

        if (!labels.SequenceEqual(task.Labels, StringComparer.Ordinal))
        {
            var now = DateTimeOffset.UtcNow;
            Db.Execute(connection, "UPDATE tasks SET labels_json = $l, updated_at = $t WHERE project_key = $k AND number = $n",
                ("$l", JsonSerializer.Serialize(labels)), ("$t", Db.Stamp(now)), ("$k", projectKey), ("$n", number));
            AddHistory(connection, number, HistoryKind.Labels, "Labels: " + (labels.Count == 0 ? "(none)" : string.Join(", ", labels)), null, now);
        }

        transaction.Commit();
        return labels;
    }

    /// <summary>Appends an agent result or other event to a task's history.</summary>
    public void AppendHistory(int number, string kind, string summary, string? detail = null)
    {
        using var connection = workspace.Open();
        RequireTask(connection, number);
        AddHistory(connection, number, kind, summary, detail, DateTimeOffset.UtcNow);
    }

    /// <summary>Creates the task for an external id, or updates it when title, body, labels, link or state differ.</summary>
    public (LocalTask Task, ImportOutcome Outcome) Import(TaskImport item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var title = RequireTitle(item.Title);
        var labels = NormalizeLabels(item.Labels);
        var existing = FindByExternalId(item.ExternalId);
        if (existing is null)
        {
            var created = Create(title, item.Body, labels, "import", item.ExternalId, item.Url, item.Closed, HistoryKind.Imported, $"Imported from {item.ExternalId}.");
            return (created, ImportOutcome.Created);
        }

        var same = existing.Title == title && existing.Body == item.Body && existing.Closed == item.Closed && existing.ExternalUrl == item.Url
            && existing.Labels.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(labels.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        if (same)
        {
            return (existing, ImportOutcome.Unchanged);
        }

        using var connection = workspace.Open();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        Db.Execute(connection,
            "UPDATE tasks SET title = $t, body = $b, labels_json = $l, state = $s, external_url = $u, updated_at = $now, closed_at = $c WHERE project_key = $k AND number = $n",
            ("$t", title), ("$b", item.Body), ("$l", JsonSerializer.Serialize(labels)), ("$s", item.Closed ? ClosedState : OpenState), ("$u", item.Url),
            ("$now", Db.Stamp(now)), ("$c", item.Closed ? Db.Stamp(existing.ClosedAt ?? now) : null), ("$k", projectKey), ("$n", existing.Number));
        AddHistory(connection, existing.Number, HistoryKind.Updated, $"Updated from {item.ExternalId}.", null, now, synced: true);
        transaction.Commit();
        return (Get(existing.Number)!, ImportOutcome.Updated);
    }

    /// <summary>Comments and history entries not yet sent to the tracker.</summary>
    public (IReadOnlyList<TaskComment> Comments, IReadOnlyList<TaskHistoryEntry> History) Unsynced(int number)
    {
        var detail = Show(number) ?? throw new WorkspaceException($"There is no task {number} in project {projectKey}.");
        return (detail.Comments.Where(c => !c.Synced).ToList(), detail.History.Where(h => !h.Synced).ToList());
    }

    public void MarkSynced(IEnumerable<long> commentIds, IEnumerable<long> historyIds)
    {
        using var connection = workspace.Open();
        using var transaction = connection.BeginTransaction();
        var now = Db.Stamp(DateTimeOffset.UtcNow);
        foreach (var id in commentIds)
        {
            Db.Execute(connection, "UPDATE task_comments SET synced_at = $t WHERE id = $i AND project_key = $k", ("$t", now), ("$i", id), ("$k", projectKey));
        }

        foreach (var id in historyIds)
        {
            Db.Execute(connection, "UPDATE task_history SET synced_at = $t WHERE id = $i AND project_key = $k", ("$t", now), ("$i", id), ("$k", projectKey));
        }

        transaction.Commit();
    }

    public static string DefaultAuthor() =>
        string.IsNullOrWhiteSpace(Environment.UserName) ? "unknown" : Environment.UserName;

    // ---------- internals ----------

    private const string Select =
        "SELECT number, title, body, state, labels_json, external_id, external_url, author, created_at, updated_at, closed_at, parent_number FROM tasks";

    private static LocalTask ReadTask(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3) == ClosedState,
        JsonSerializer.Deserialize<List<string>>(r.GetString(4)) ?? [],
        Db.StringOrNull(r, 5), Db.StringOrNull(r, 6), r.GetString(7),
        Db.ParseStamp(r.GetString(8)), Db.ParseStamp(r.GetString(9)), Db.ParseStampOrNull(r, 10), r.IsDBNull(11) ? null : r.GetInt32(11));

    private LocalTask? GetCore(SqliteConnection connection, int number) =>
        Db.Query(connection, ReadTask, $"{Select} WHERE project_key = $k AND number = $n", ("$k", projectKey), ("$n", number)).FirstOrDefault();

    private LocalTask RequireTask(SqliteConnection connection, int number) =>
        GetCore(connection, number) ?? throw new WorkspaceException($"There is no task {number} in project {projectKey}.");

    private LocalTask Create(string title, string body, List<string> labels, string author, string? externalId, string? url, bool closed, string kind, string summary, int? parentNumber = null)
    {
        using var connection = workspace.Open();
        using var transaction = connection.BeginTransaction();
        using var max = Db.Command(connection, "SELECT COALESCE(MAX(number), 0) + 1 FROM tasks WHERE project_key = $k", ("$k", projectKey));
        var number = Convert.ToInt32(max.ExecuteScalar(), CultureInfo.InvariantCulture);
        var now = DateTimeOffset.UtcNow;
        Db.Execute(connection,
            "INSERT INTO tasks (project_key, number, title, body, state, labels_json, external_id, external_url, author, created_at, updated_at, closed_at, parent_number) "
            + "VALUES ($k, $n, $t, $b, $s, $l, $e, $u, $a, $now, $now, $c, $p)",
            ("$k", projectKey), ("$n", number), ("$t", title), ("$b", body), ("$s", closed ? ClosedState : OpenState), ("$l", JsonSerializer.Serialize(labels)),
            ("$e", externalId), ("$u", url), ("$a", author), ("$now", Db.Stamp(now)), ("$c", closed ? Db.Stamp(now) : null), ("$p", parentNumber));
        AddHistory(connection, number, kind, summary, null, now, synced: externalId is not null);
        transaction.Commit();
        return GetCore(connection, number)!;
    }

    private List<LocalTask> ChildrenCore(SqliteConnection connection, int parentNumber) =>
        Db.Query(connection, ReadTask, $"{Select} WHERE project_key = $k AND parent_number = $p ORDER BY number", ("$k", projectKey), ("$p", parentNumber));

    /// <summary>Closes the parent when the child that was just closed was the last open one.</summary>
    private void CloseParentWhenDone(SqliteConnection connection, int parentNumber, int closedChild, DateTimeOffset now)
    {
        var parent = GetCore(connection, parentNumber);
        if (parent is null || parent.Closed || ChildrenCore(connection, parentNumber).Any(c => !c.Closed && c.Number != closedChild))
        {
            return;
        }

        Db.Execute(connection, "UPDATE tasks SET state = $s, closed_at = $t, updated_at = $t WHERE project_key = $k AND number = $n",
            ("$s", ClosedState), ("$t", Db.Stamp(now)), ("$k", projectKey), ("$n", parentNumber));
        AddHistory(connection, parentNumber, HistoryKind.Closed, "Closed automatically: all child tasks are closed.", null, now);
    }

    private bool SetState(int number, bool close, string? author)
    {
        using var connection = workspace.Open();
        using var transaction = connection.BeginTransaction();
        var task = RequireTask(connection, number);
        if (task.Closed == close)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        Db.Execute(connection, "UPDATE tasks SET state = $s, closed_at = $c, updated_at = $t WHERE project_key = $k AND number = $n",
            ("$s", close ? ClosedState : OpenState), ("$c", close ? Db.Stamp(now) : null), ("$t", Db.Stamp(now)), ("$k", projectKey), ("$n", number));
        AddHistory(connection, number, close ? HistoryKind.Closed : HistoryKind.Reopened, (close ? "Closed" : "Reopened") + $" by {author ?? DefaultAuthor()}.", null, now);
        if (close && task.ParentNumber is { } parentNumber)
        {
            CloseParentWhenDone(connection, parentNumber, number, now);
        }

        transaction.Commit();
        return true;
    }

    private void AddHistory(SqliteConnection connection, int number, string kind, string summary, string? detail, DateTimeOffset now, bool synced = false) =>
        Db.Execute(connection,
            "INSERT INTO task_history (project_key, task_number, kind, summary, detail, created_at, synced_at) VALUES ($k, $n, $kind, $s, $d, $t, $sync)",
            ("$k", projectKey), ("$n", number), ("$kind", kind), ("$s", summary), ("$d", detail), ("$t", Db.Stamp(now)), ("$sync", synced ? Db.Stamp(now) : null));

    private static string RequireTitle(string? title)
    {
        var text = title?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new WorkspaceException("A task needs a title.");
        }

        return text.Length > MaxTitleLength ? throw new WorkspaceException($"A title can have at most {MaxTitleLength} characters.") : text;
    }

    private static List<string> NormalizeLabels(IEnumerable<string>? labels)
    {
        var result = new List<string>();
        foreach (var label in (labels ?? []).SelectMany(l => l.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!result.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(label);
            }
        }

        return result;
    }
}
