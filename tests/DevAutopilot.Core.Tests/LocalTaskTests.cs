using DevAutopilot.Core.Gate;
using DevAutopilot.Core.Local;
using DevAutopilot.Core.Tasks;
using DevAutopilot.Core.Workspace;
using Microsoft.Data.Sqlite;
using Ws = DevAutopilot.Core.Workspace.Workspace;

namespace DevAutopilot.Core.Tests;

public sealed class LocalTaskTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private LocalTaskService Service(string project = "demo") => new(new Ws(Path.Combine(_temp, "ws")), project);

    [Fact]
    public void Numbers_are_per_project_and_start_at_one()
    {
        var a = Service("a");
        var b = Service("b");
        Assert.Equal(1, a.Add("first").Number);
        Assert.Equal(2, a.Add("second").Number);
        Assert.Equal(1, b.Add("other project").Number);
        Assert.Single(b.List());
    }

    [Fact]
    public void Update_changes_the_text_keeps_the_rest_and_records_it_in_the_history()
    {
        var tasks = Service();
        var task = tasks.Add("Old title", "Old body", ["a"]);
        Assert.False(tasks.Update(task.Number, null, null), "nothing to change");
        Assert.True(tasks.Update(task.Number, "New title", null));
        var changed = tasks.Get(task.Number)!;
        Assert.Equal("New title", changed.Title);
        Assert.Equal("Old body", changed.Body);
        Assert.Equal(["a"], changed.Labels);
        Assert.Contains(tasks.Show(task.Number)!.History, e => e.Kind == LocalTaskService.HistoryKind.Updated);
        Assert.Throws<WorkspaceException>(() => tasks.Update(task.Number, " ", null));
        Assert.Throws<WorkspaceException>(() => tasks.Update(99, "x", null));
    }

    [Fact]
    public void Add_requires_a_title_and_normalizes_labels()
    {
        var tasks = Service();
        Assert.Throws<WorkspaceException>(() => tasks.Add("  "));
        var task = tasks.Add(" Fix 404 ", "body", ["bug, ui", "BUG"], "me");
        Assert.Equal("Fix 404", task.Title);
        Assert.Equal(["bug", "ui"], task.Labels);
        Assert.Equal("me", task.Author);
        Assert.False(task.Closed);
    }

    [Fact]
    public void Close_reopen_comment_and_labels_are_recorded_in_history()
    {
        var tasks = Service();
        var n = tasks.Add("task").Number;
        Assert.True(tasks.Close(n));
        Assert.False(tasks.Close(n));
        Assert.Empty(tasks.List());
        Assert.Single(tasks.List(includeClosed: true));
        Assert.True(tasks.Reopen(n));
        tasks.Comment(n, "looks good", "ann");
        Assert.Equal(["a", "b"], tasks.ChangeLabels(n, ["a", "b"], null));
        Assert.Equal(["b"], tasks.ChangeLabels(n, null, ["A"]));
        Assert.Throws<WorkspaceException>(() => tasks.Comment(n, " "));
        Assert.Throws<WorkspaceException>(() => tasks.Close(99));

        var detail = tasks.Show(n)!;
        Assert.Equal("looks good", Assert.Single(detail.Comments).Body);
        Assert.Equal(["created", "closed", "reopened", "labels", "labels"], detail.History.Select(h => h.Kind));
        Assert.Single(tasks.List(label: "b"));
        Assert.Empty(tasks.List(label: "a"));
    }

    [Fact]
    public void Import_is_idempotent_by_external_id()
    {
        var tasks = Service();
        var item = new TaskImport("ado:7", "Title", "Body", ["x"], false, "https://example.test/7");
        Assert.Equal(ImportOutcome.Created, tasks.Import(item).Outcome);
        Assert.Equal(ImportOutcome.Unchanged, tasks.Import(item).Outcome);
        var (task, outcome) = tasks.Import(item with { Title = "New title", Closed = true });
        Assert.Equal(ImportOutcome.Updated, outcome);
        Assert.Equal("New title", task.Title);
        Assert.True(task.Closed);
        Assert.Equal("https://example.test/7", task.ExternalUrl);
        Assert.Single(tasks.List(includeClosed: true));
    }

    [Fact]
    public async Task Work_item_source_reads_open_tasks_and_records_triage_in_history()
    {
        var root = Path.Combine(_temp, "repo");
        var data = Path.Combine(_temp, "data");
        var tasks = Service();
        tasks.Add("Open one", "do it", ["bug"]);
        tasks.Close(tasks.Add("Closed one").Number);
        var source = new LocalTaskWorkItemSource(tasks, new TriageStore(root, data));

        var open = Assert.Single(await source.ListOpenAsync(default));
        Assert.Equal(("1", "Open one", "do it"), (open.Id, open.Title, open.Body));
        Assert.Equal("open", open.State);
        Assert.Null(await source.GetAsync("nope", default));
        Assert.Equal("1", (await source.GetAsync("#1", default))!.Id);

        await source.RecordTriageAsync("1", new TriageResult(TriageLabel.AgentReady, "clear enough", [], RiskLevel.Low), default);
        Assert.NotNull(await new TriageStore(root, data).LoadAsync("1", default));
        var entry = tasks.Show(1)!.History.Last();
        Assert.Equal("triage", entry.Kind);
        Assert.Equal("clear enough", entry.Detail);
    }

    [Fact]
    public async Task Publisher_decorator_appends_gate_and_draft_to_history()
    {
        var tasks = Service();
        tasks.Add("task");
        var publisher = new TaskHistoryPublisher(new StubPublisher(), tasks);
        var request = new PublishRequest(new WorkItem("1", "task", "", []), "autopilot/1", "diff",
            new GateDecision(GateOutcome.Passed, [new GateFinding("r1", "all fine")]),
            new Evidence("1", null, null, null));
        await publisher.PublishAsync(request, default);

        var kinds = tasks.Show(1)!.History.Select(h => h.Kind).ToList();
        Assert.Equal(["created", "gate", "draft"], kinds);
    }

    [Fact]
    public async Task Sync_imports_files_then_exports_only_what_was_not_sent()
    {
        var repo = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".autopilot", "tasks"));
        await File.WriteAllTextAsync(Path.Combine(repo, ".autopilot", "tasks", "001-fix.md"), "# Fix it\nLabels: bug\nSome text");
        await File.WriteAllTextAsync(Path.Combine(repo, ".autopilot", "tasks", "002-old.md"), "# Old\nStatus: done");
        var tasks = Service();

        var files = new TaskSyncService(tasks, new FileTaskTracker(new LocalWorkItemSource(repo)));
        Assert.Equal(new SyncImportReport(2, 0, 0), await files.ImportAsync(default));
        Assert.Equal(new SyncImportReport(0, 0, 2), await files.ImportAsync(default));
        Assert.Equal(["Fix it"], tasks.List().Select(t => t.Title));
        Assert.Equal(["bug"], tasks.Get(1)!.Labels);

        // Export: a fake tracker sees comments and results once.
        var tracker = new RecordingTracker();
        var sync = new TaskSyncService(tasks, tracker);
        Assert.True(await sync.ExportAsync(1, default));
        Assert.Equal("file:001", tracker.Writes.Single().Id);
        tracker.Writes.Clear();

        tasks.Comment(1, "hello", "ann");
        tasks.AppendHistory(1, LocalTaskService.HistoryKind.Draft, "Draft ready.", "https://example.test/pr/1");
        Assert.True(await sync.ExportAsync(1, default));
        var write = tracker.Writes.Single().Write;
        Assert.Equal(["ann", "autopilot"], write.Comments!.Select(c => c.Author));
        Assert.Equal("https://example.test/pr/1", write.DraftLink);
        Assert.False(write.Closed);

        tracker.Writes.Clear();
        await sync.ExportAsync(1, default);
        Assert.Empty(tracker.Writes.Single().Write.Comments!);
        Assert.False(await sync.ExportAsync(99, default));
    }

    [Fact]
    public void Migration_creates_the_task_tables()
    {
        using var connection = new Ws(Path.Combine(_temp, "ws")).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Contains("tasks", tables);
        Assert.Contains("task_comments", tables);
        Assert.Contains("task_history", tables);
    }

    private sealed class StubPublisher : IPublisher
    {
        public Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct) => Task.FromResult(new PublishResult(true, "/out/1", null));
    }

    private sealed class RecordingTracker : IWorkItemTracker
    {
        public List<(string Id, TrackerWrite Write)> Writes { get; } = [];

        public string Name => "fake";

        public Task<IReadOnlyList<TrackerItem>> ReadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TrackerItem>>([]);

        public Task WriteAsync(string externalId, TrackerWrite write, CancellationToken ct)
        {
            Writes.Add((externalId, write));
            return Task.CompletedTask;
        }
    }
}
