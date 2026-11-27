using System.Text.Json;
using Autopilot.Core.Jobs;
using Autopilot.Core.Scheduling;
using Microsoft.Data.Sqlite;

namespace Autopilot.Cli.Tests;

/// <summary>The new commands against a temp workspace, with a fake scheduler, launcher and killer. Never claude, never the OS scheduler.</summary>
public sealed class BackendCommandTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeScheduler _scheduler = new();
    private readonly FakeLauncher _launcher = new();
    private readonly FakeKiller _killer = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string WorkspacePath => _temp.Combine("ws");

    private async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CliContext(TextReader.Null, output, error)
        {
            DoctorChecksTools = false,
            Scheduler = _scheduler,
            JobLauncher = _launcher,
            ProcessKiller = _killer,
            IsProcessAlive = _ => true,
            Program = ("autopilot", []),
        };
        var exit = await CliApp.RunAsync([.. args, "--workspace", WorkspacePath], context);
        return (exit, output.ToString(), error.ToString());
    }

    private async Task<TestRepo> AddProjectAsync(string key = "demo")
    {
        var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["src/a.cs"] = "class A {}" });
        Assert.Equal(0, (await RunAsync("project", "add", key, "--repo", repo.Root)).Exit);
        return repo;
    }

    [Fact]
    public async Task Pool_commands_keep_the_queue_in_the_database()
    {
        using var repo = await AddProjectAsync();

        Assert.Contains("Added 1.", (await RunAsync("pool", "add", "1", "2", "3", "--project", "demo")).Out);
        Assert.Contains("already in the pool", (await RunAsync("pool", "add", "2", "--project", "demo")).Out);
        Assert.Equal(0, (await RunAsync("pool", "move", "3", "1", "--project", "demo")).Exit);
        using var json = JsonDocument.Parse((await RunAsync("pool", "list", "--json", "--project", "demo")).Out);
        Assert.Equal(["3", "1", "2"], json.RootElement.EnumerateArray().Select(e => e.GetProperty("workItemId").GetString()));
        Assert.Equal(0, (await RunAsync("pool", "remove", "1", "--project", "demo")).Exit);
        Assert.Equal(1, (await RunAsync("pool", "remove", "1", "--project", "demo")).Exit);
        Assert.Contains("Removed 2", (await RunAsync("pool", "clear", "--project", "demo")).Out);
        Assert.Contains("empty", (await RunAsync("pool", "list", "--project", "demo")).Out);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".autopilot", "out", "pool.json")));
    }

    [Fact]
    public async Task Ado_commands_explain_when_the_project_does_not_use_azure_devops()
    {
        using var repo = await AddProjectAsync();

        var preview = await RunAsync("ado", "preview", "--project", "demo");
        var check = await RunAsync("ado", "check", "--project", "demo");

        Assert.Equal(1, preview.Exit);
        Assert.Contains("does not use Azure DevOps", preview.Err);
        Assert.Equal(1, check.Exit);
        Assert.Contains("ado", (await RunAsync("--help")).Out);
    }

    [Fact]
    public async Task Pool_needs_a_registered_project()
    {
        using var repo = await TestRepo.CreateAsync(new Dictionary<string, string> { ["a.cs"] = "x" });

        var result = await RunAsync("pool", "list", "--project", repo.Root);

        Assert.Equal(2, result.Exit);
        Assert.Contains("registered project", result.Err);
    }

    [Fact]
    public async Task Pool_run_with_a_bad_limit_is_a_usage_error()
    {
        using var repo = await AddProjectAsync();
        Assert.Equal(2, (await RunAsync("pool", "run", "--limit", "0", "--project", "demo")).Exit);
    }

    [Fact]
    public async Task List_shows_open_items_with_triage_state_and_pool_membership()
    {
        using var repo = await AddProjectAsync();
        Directory.CreateDirectory(_temp.Combine("ws", "projects", "demo", "tasks"));
        File.WriteAllText(_temp.Combine("ws", "projects", "demo", "tasks", "001-first.md"), "# First task\n\nDo it.\n");
        await RunAsync("pool", "add", "001", "--project", "demo");

        using var json = JsonDocument.Parse((await RunAsync("list", "--json", "--project", "demo")).Out);
        var item = json.RootElement[0];

        Assert.Equal("001", item.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("triage").ValueKind);
        Assert.True(item.GetProperty("inPool").GetBoolean());
        Assert.Contains("not triaged", (await RunAsync("list", "--project", "demo")).Out);
    }

    [Fact]
    public async Task Detached_implement_creates_a_job_and_cancel_stops_only_that_job()
    {
        using var repo = await AddProjectAsync();

        var first = await RunAsync("implement", "1", "--detach", "--project", "demo");
        await RunAsync("implement", "2", "--detach", "--project", "demo");
        Assert.Equal(0, first.Exit);
        Assert.Equal(2, _launcher.Launched.Count);
        Assert.Equal(["implement", "1", "--job"], _launcher.Launched[0].Args.Take(3));
        Assert.Contains("--workspace", _launcher.Launched[0].Args);

        using var list = JsonDocument.Parse((await RunAsync("job", "list", "--json", "--project", "demo")).Out);
        var ids = list.RootElement.EnumerateArray().ToDictionary(j => j.GetProperty("workItemId").GetString()!, j => j.GetProperty("id").GetString()!);
        Assert.Equal(2, ids.Count);
        Assert.Contains(ids["1"], first.Out);

        Assert.Equal(0, (await RunAsync("job", "cancel", ids["1"], "--project", "demo")).Exit);
        Assert.Equal([_launcher.Pids[0]], _killer.Killed);
        Assert.Contains("cancelled", (await RunAsync("job", "show", ids["1"], "--project", "demo")).Out);
        Assert.Contains("queued", (await RunAsync("job", "show", ids["2"], "--project", "demo")).Out);
        Assert.Equal(1, (await RunAsync("job", "cancel", ids["1"], "--project", "demo")).Exit);
        Assert.Equal(2, (await RunAsync("job", "show", "jmissing", "--project", "demo")).Exit);
    }

    [Fact]
    public async Task A_triage_job_that_cannot_run_ends_failed_with_the_reason()
    {
        using var repo = await AddProjectAsync();
        var jobs = new JobService(new Autopilot.Core.Workspace.Workspace(WorkspacePath), "demo", _ => true);
        var job = jobs.Create("77", JobKinds.Triage);

        var result = await RunAsync("triage", "77", "--job", job.Id, "--project", "demo");

        Assert.Equal(1, result.Exit);
        var after = jobs.Get(job.Id)!;
        Assert.Equal(JobStatus.Failed, after.Status);
        Assert.Equal(JobKinds.Triage, after.Kind);
        Assert.Contains("77", after.Message, StringComparison.Ordinal);
        Assert.Contains("not found", after.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Detached_implement_refuses_an_unsafe_id()
    {
        using var repo = await AddProjectAsync();
        Assert.Equal(2, (await RunAsync("implement", "../x", "--detach", "--project", "demo")).Exit);
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task Schedule_prints_the_plan_and_installs_only_with_yes()
    {
        using var repo = await AddProjectAsync();

        var plan = await RunAsync("schedule", "set", "--at", "02:30", "--limit", "4", "--project", "demo");
        Assert.Equal(0, plan.Exit);
        Assert.Contains("02:30", plan.Out);
        Assert.Contains("Nothing was installed", plan.Out);
        Assert.Empty(_scheduler.Jobs);

        var installed = await RunAsync("schedule", "set", "--at", "02:30", "--limit", "4", "--yes", "--project", "demo");
        Assert.Contains("Installed", installed.Out);
        var job = _scheduler.Jobs["autopilot-demo"];
        Assert.Contains("--project demo", job.Command);
        Assert.Contains("pool run --limit 4", job.Command);

        Assert.Contains("It is installed", (await RunAsync("schedule", "show", "--project", "demo")).Out);
        Assert.Contains("Removed", (await RunAsync("schedule", "remove", "--project", "demo")).Out);
        Assert.Empty(_scheduler.Jobs);
        Assert.Contains("No night schedule", (await RunAsync("schedule", "show", "--project", "demo")).Out);
    }

    [Fact]
    public async Task Schedule_refuses_a_bad_time()
    {
        using var repo = await AddProjectAsync();
        var result = await RunAsync("schedule", "set", "--at", "25:99", "--yes", "--project", "demo");
        Assert.NotEqual(0, result.Exit);
        Assert.Empty(_scheduler.Jobs);
    }

    [Fact]
    public async Task Stats_and_decide_count_agreement_without_a_run_when_empty()
    {
        using var repo = await AddProjectAsync();

        Assert.Contains("No person has decided", (await RunAsync("stats", "--project", "demo")).Out);
        Assert.Equal(1, (await RunAsync("decide", "5", "merged", "--project", "demo")).Exit);
        using var json = JsonDocument.Parse((await RunAsync("stats", "--json", "--project", "demo")).Out);
        Assert.Equal(0, json.RootElement.GetProperty("totalRuns").GetInt32());
        Assert.Equal(2, (await RunAsync("decide", "5", "maybe", "--project", "demo")).Exit);
    }

    private sealed class FakeScheduler : IScheduler
    {
        public Dictionary<string, ScheduledJob> Jobs { get; } = [];

        public string Description => "a fake scheduler";

        public Task InstallAsync(ScheduledJob job, CancellationToken ct)
        {
            Jobs[job.Name] = job;
            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string name, CancellationToken ct) => Task.FromResult(Jobs.Remove(name));

        public Task<bool> ExistsAsync(string name, CancellationToken ct) => Task.FromResult(Jobs.ContainsKey(name));
    }

    private sealed class FakeLauncher : IJobLauncher
    {
        public List<(string File, IReadOnlyList<string> Args)> Launched { get; } = [];

        public List<int> Pids { get; } = [];

        public int Launch(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Launched.Add((fileName, arguments));
            var pid = 9000 + Launched.Count;
            Pids.Add(pid);
            return pid;
        }
    }

    private sealed class FakeKiller : IProcessKiller
    {
        public List<int> Killed { get; } = [];

        public void Kill(int pid) => Killed.Add(pid);
    }
}
