using System.Text.Json.Nodes;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Gate;
using DevAutopilot.Core.Jobs;
using DevAutopilot.Core.Local;
using DevAutopilot.Core.Pipeline;
using DevAutopilot.Core.Pool;
using DevAutopilot.Core.Process;
using DevAutopilot.Core.Runs;
using DevAutopilot.Core.Scheduling;
using DevAutopilot.Core.Settings;
using DevAutopilot.Core.Tasks;
using DevAutopilot.Core.Workspace;
using Microsoft.Data.Sqlite;
using Ws = DevAutopilot.Core.Workspace.Workspace;

namespace DevAutopilot.Core.Tests;

public sealed class BackendServicesTests : IDisposable
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

    private Ws NewWorkspace() => new(Path.Combine(_temp, "ws"));

    private static ImplementResult Result(string id, ImplementOutcome outcome, string message = "ok", GateOutcome? gate = null) =>
        new(id, outcome, message, "autopilot/" + id, null, gate is null ? null : new GateDecision(gate.Value, []));

    // ---------- pool ----------

    [Fact]
    public void Pool_keeps_order_and_ignores_duplicates()
    {
        var pool = new PoolService(NewWorkspace(), "demo");
        Assert.True(pool.Add("1"));
        Assert.True(pool.Add("2"));
        Assert.True(pool.Add("3"));
        Assert.False(pool.Add("2"));

        Assert.Equal(["1", "2", "3"], pool.List().Select(e => e.WorkItemId));
        Assert.True(pool.Move("3", 1));
        Assert.Equal(["3", "1", "2"], pool.List().Select(e => e.WorkItemId));
        Assert.True(pool.Move("3", 99));
        Assert.Equal(["1", "2", "3"], pool.List().Select(e => e.WorkItemId));
        Assert.False(pool.Move("missing", 1));

        Assert.True(pool.Remove("1"));
        Assert.Equal([1, 2], pool.List().Select(e => e.Position));
        Assert.False(pool.Remove("1"));
        Assert.Equal(2, pool.Clear());
        Assert.Empty(pool.List());
    }

    [Fact]
    public void Pools_of_two_projects_do_not_mix_and_ids_are_checked()
    {
        var workspace = NewWorkspace();
        new PoolService(workspace, "a").Add("1");
        Assert.Empty(new PoolService(workspace, "b").List());
        Assert.Throws<WorkspaceException>(() => new PoolService(workspace, "a").Add("../x"));
    }

    // ---------- lock ----------

    [Fact]
    public void Lock_refuses_a_second_live_holder_and_takes_over_a_stale_one()
    {
        var workspace = NewWorkspace();
        var alive = true;
        var first = new ProjectRunLock(workspace, _ => alive, () => 100);
        var second = new ProjectRunLock(workspace, _ => alive, () => 200);

        var held = first.Acquire("demo", "pool run");
        var refused = Assert.Throws<RunLockHeldException>(() => second.Acquire("demo", "job"));
        Assert.Equal(100, refused.Pid);
        Assert.Equal(100, second.Holder("demo")?.Pid);

        alive = false;
        Assert.Null(second.Holder("demo"));
        using (second.Acquire("demo", "job"))
        {
            alive = true;
            Assert.Equal(200, first.Holder("demo")?.Pid);
        }

        held.Dispose();
        alive = true;
        using (first.Acquire("demo", "again"))
        {
            // The disposed first handle must not have removed the second lock row.
        }
    }

    [Fact]
    public void Lock_of_another_project_is_independent()
    {
        var workspace = NewWorkspace();
        var locker = new ProjectRunLock(workspace, _ => true, () => 1);
        using var a = locker.Acquire("a", "x");
        using var b = new ProjectRunLock(workspace, _ => true, () => 2).Acquire("b", "y");
        Assert.NotNull(locker.Holder("b"));
    }

    // ---------- pool run ----------

    private PoolRunner Runner(Ws workspace, Func<string, Task<ImplementResult>> implement, out PoolService pool, out RunStore runs)
    {
        pool = new PoolService(workspace, "demo");
        runs = new RunStore(workspace, "demo");
        return new PoolRunner(pool, runs, new ProjectRunLock(workspace, _ => false), "demo", (id, _) => implement(id));
    }

    [Fact]
    public async Task Pool_run_sorts_results_writes_the_report_and_empties_the_pool()
    {
        var workspace = NewWorkspace();
        var outcomes = new Dictionary<string, ImplementResult>
        {
            ["1"] = Result("1", ImplementOutcome.Published, "draft made", GateOutcome.Passed),
            ["2"] = Result("2", ImplementOutcome.Rejected, "gate said no", GateOutcome.Rejected),
            ["3"] = Result("3", ImplementOutcome.NotReady, "needs a person"),
        };
        var runner = Runner(workspace, id => Task.FromResult(outcomes[id]), out var pool, out var runs);
        foreach (var id in outcomes.Keys)
        {
            pool.Add(id);
        }

        var folder = Path.Combine(_temp, "report");
        var result = await runner.RunAsync(null, folder, CancellationToken.None);

        Assert.Single(result.Done);
        Assert.Single(result.Rejected);
        Assert.Single(result.NeedsHuman);
        Assert.Empty(result.NotReached);
        Assert.False(result.Succeeded);
        Assert.Empty(pool.List());
        var report = await File.ReadAllTextAsync(Path.Combine(folder, "POOL-REPORT.md"));
        Assert.Contains("## Done (drafts waiting for a person) (1)", report);
        Assert.Contains("## Rejected (1)", report);
        Assert.Contains("## Needs a person (1)", report);
        Assert.Contains("## Not reached (0)", report);

        var stored = runs.List();
        Assert.Equal(3, stored.Count);
        Assert.True(stored.Single(r => r.WorkItemId == "1").WouldMerge);
        Assert.Equal("rejected", stored.Single(r => r.WorkItemId == "2").GateVerdict);
        Assert.Null(stored.Single(r => r.WorkItemId == "3").GateVerdict);
    }

    [Fact]
    public async Task Pool_run_stops_on_an_infrastructure_error_and_keeps_the_rest_in_the_pool()
    {
        var workspace = NewWorkspace();
        var started = new List<string>();
        var runner = Runner(workspace, id =>
        {
            started.Add(id);
            return Task.FromResult(id == "2"
                ? Result(id, ImplementOutcome.Failed, "The implementer run failed: Claude usage limit reached")
                : Result(id, ImplementOutcome.Published, "ok", GateOutcome.Passed));
        }, out var pool, out var runs);
        foreach (var id in new[] { "1", "2", "3" })
        {
            pool.Add(id);
        }

        var result = await runner.RunAsync(null, Path.Combine(_temp, "r"), CancellationToken.None);

        Assert.Equal(["1", "2"], started);
        Assert.NotNull(result.StoppedBecause);
        Assert.Equal(2, result.NotReached.Count);
        Assert.Equal(["2", "3"], pool.List().Select(e => e.WorkItemId));
        Assert.Single(runs.List());
        Assert.Contains("Stopped at 2", await File.ReadAllTextAsync(result.ReportPath!));
    }

    [Fact]
    public async Task Pool_run_continues_after_an_ordinary_failure_and_honours_the_limit()
    {
        var workspace = NewWorkspace();
        var started = new List<string>();
        var runner = Runner(workspace, id =>
        {
            started.Add(id);
            return Task.FromResult(id == "1"
                ? Result(id, ImplementOutcome.Failed, "Branch autopilot/1 already exists")
                : Result(id, ImplementOutcome.Published, "ok", GateOutcome.Passed));
        }, out var pool, out _);
        foreach (var id in new[] { "1", "2", "3" })
        {
            pool.Add(id);
        }

        var result = await runner.RunAsync(2, Path.Combine(_temp, "r"), CancellationToken.None);

        Assert.Equal(["1", "2"], started);
        Assert.Single(result.Rejected);
        Assert.Single(result.Done);
        Assert.Single(result.NotReached);
        Assert.Equal(["3"], pool.List().Select(e => e.WorkItemId));
    }

    [Fact]
    public async Task Pool_run_refuses_a_second_run_of_the_same_project()
    {
        var workspace = NewWorkspace();
        var other = new ProjectRunLock(workspace, _ => true, () => 4242);
        using var held = other.Acquire("demo", "job j1");
        var runner = new PoolRunner(new PoolService(workspace, "demo"), new RunStore(workspace, "demo"), new ProjectRunLock(workspace, _ => true, () => 1), "demo",
            (_, _) => throw new InvalidOperationException("must not run"));

        await Assert.ThrowsAsync<RunLockHeldException>(() => runner.RunAsync(null, _temp, CancellationToken.None));
    }

    [Fact]
    public async Task Pool_run_turns_a_thrown_error_into_a_failed_item_and_releases_the_lock()
    {
        var workspace = NewWorkspace();
        var runner = Runner(workspace, _ => throw new InvalidOperationException("git broke"), out var pool, out _);
        pool.Add("1");

        var result = await runner.RunAsync(null, Path.Combine(_temp, "r"), CancellationToken.None);

        Assert.Single(result.Rejected);
        Assert.Null(new ProjectRunLock(workspace, _ => true).Holder("demo"));
    }

    // ---------- runs and stats ----------

    [Fact]
    public void Stats_count_agreement_between_the_gate_and_the_person()
    {
        var runs = new RunStore(NewWorkspace(), "demo");
        var now = DateTimeOffset.UtcNow;
        var passedMerged = runs.Record(Result("1", ImplementOutcome.Published, gate: GateOutcome.Passed), now);
        var passedRejected = runs.Record(Result("2", ImplementOutcome.Published, gate: GateOutcome.Passed), now);
        var rejectedMerged = runs.Record(Result("3", ImplementOutcome.Rejected, gate: GateOutcome.Rejected), now);
        var rejectedRejected = runs.Record(Result("4", ImplementOutcome.Rejected, gate: GateOutcome.Rejected), now);
        runs.Record(Result("5", ImplementOutcome.Published, gate: GateOutcome.Passed), now);

        Assert.Null(runs.Stats().AgreementRate);
        Assert.True(runs.RecordDecision(passedMerged.Id, HumanDecision.Merged));
        Assert.True(runs.RecordDecision(passedRejected.Id, HumanDecision.Rejected));
        Assert.True(runs.RecordDecision(rejectedMerged.Id, HumanDecision.Merged));
        Assert.True(runs.RecordDecision(rejectedRejected.Id, HumanDecision.Rejected));
        Assert.False(runs.RecordDecision(9999, HumanDecision.Merged));

        var stats = runs.Stats();
        Assert.Equal(5, stats.TotalRuns);
        Assert.Equal(3, stats.GatePassed);
        Assert.Equal(2, stats.GateRejected);
        Assert.Equal(4, stats.Decided);
        Assert.Equal(2, stats.Agreed);
        Assert.Equal(1, stats.GateTooLenient);
        Assert.Equal(1, stats.GateTooStrict);
        Assert.Equal(0.5, stats.AgreementRate);
    }

    [Fact]
    public void Runs_of_another_project_are_not_counted_and_last_run_is_the_newest()
    {
        var workspace = NewWorkspace();
        var runs = new RunStore(workspace, "demo");
        runs.Record(Result("1", ImplementOutcome.Rejected, gate: GateOutcome.Rejected), DateTimeOffset.UtcNow);
        var second = runs.Record(Result("1", ImplementOutcome.Published, gate: GateOutcome.Passed), DateTimeOffset.UtcNow);
        new RunStore(workspace, "other").Record(Result("1", ImplementOutcome.Failed), DateTimeOffset.UtcNow);

        Assert.Equal(2, runs.Stats().TotalRuns);
        Assert.Equal(second.Id, runs.LastRuns()["1"].Id);
        Assert.Null(runs.Get(999));
    }

    // ---------- jobs ----------

    private sealed class FakeLauncher : IJobLauncher
    {
        public List<(string File, IReadOnlyList<string> Args)> Launched { get; } = [];

        public int NextPid { get; set; } = 500;

        public int Launch(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Launched.Add((fileName, arguments));
            return NextPid++;
        }
    }

    private sealed class FakeKiller : IProcessKiller
    {
        public List<int> Killed { get; } = [];

        public void Kill(int pid) => Killed.Add(pid);
    }

    [Fact]
    public void Job_goes_through_queued_running_and_finished_with_a_log()
    {
        var workspace = NewWorkspace();
        var jobs = new JobService(workspace, "demo", _ => true);
        var launcher = new FakeLauncher();

        var job = jobs.Start("7", launcher, "autopilot", id => ["implement", "7", "--job", id], _temp);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(500, job.Pid);
        Assert.Equal(["implement", "7", "--job", job.Id], launcher.Launched.Single().Args);

        jobs.MarkRunning(job.Id, 777);
        using (var log = new JobLogWriter(job.LogPath, step => jobs.SetStep(job.Id, step)))
        {
            log.WriteLine("7: creating worktree on autopilot/7 from main");
            log.WriteLine("7: implementer");
            log.WriteLine("some other line");
        }

        var running = jobs.Get(job.Id)!;
        Assert.Equal(JobStatus.Running, running.Status);
        Assert.Equal("implementer", running.Step);
        Assert.NotNull(running.StartedAt);

        jobs.Finish(job.Id, JobStatus.Succeeded, "draft made");
        var done = jobs.Get(job.Id)!;
        Assert.Equal(JobStatus.Succeeded, done.Status);
        Assert.NotNull(done.FinishedAt);
        Assert.Contains("some other line", File.ReadAllText(job.LogPath));
    }

    [Fact]
    public async Task Cancel_stops_only_the_named_job_and_frees_its_lock()
    {
        var workspace = NewWorkspace();
        var jobs = new JobService(workspace, "demo", _ => true);
        var otherProject = new JobService(workspace, "other", _ => true);
        var launcher = new FakeLauncher();
        var first = jobs.Start("1", launcher, "autopilot", id => [id], _temp);
        var second = jobs.Start("2", launcher, "autopilot", id => [id], _temp);
        var foreign = otherProject.Start("3", launcher, "autopilot", id => [id], _temp);
        jobs.MarkRunning(first.Id, 501);
        jobs.MarkRunning(second.Id, 502);
        otherProject.MarkRunning(foreign.Id, 503);
        var locker = new ProjectRunLock(workspace, _ => true, () => 501);
        locker.Acquire("demo", "job " + first.Id);
        var killer = new FakeKiller();

        Assert.True(jobs.Cancel(first.Id, killer, locker));

        Assert.Equal([501], killer.Killed);
        Assert.Equal(JobStatus.Cancelled, jobs.Get(first.Id)!.Status);
        Assert.Equal(JobStatus.Running, jobs.Get(second.Id)!.Status);
        Assert.Equal(JobStatus.Running, otherProject.Get(foreign.Id)!.Status);
        Assert.Null(locker.Holder("demo"));
        Assert.False(jobs.Cancel(first.Id, killer), "a job that has ended cannot be cancelled again");
        Assert.False(jobs.Cancel("jnope", killer));
        Assert.Equal("", await jobs.ReadLogAsync("jnope", null, CancellationToken.None));
    }

    [Fact]
    public void A_running_job_whose_process_is_gone_is_reported_as_failed()
    {
        var workspace = NewWorkspace();
        var alive = true;
        var jobs = new JobService(workspace, "demo", _ => alive);
        var job = jobs.Create("1");
        jobs.MarkRunning(job.Id, 600);
        Assert.Equal(JobStatus.Running, jobs.Get(job.Id)!.Status);

        alive = false;

        var list = jobs.List();
        Assert.Equal(JobStatus.Failed, list.Single().Status);
        Assert.Equal(JobStatus.Failed, jobs.Get(job.Id)!.Status);
    }

    [Fact]
    public void A_job_that_cannot_be_launched_is_marked_failed()
    {
        var jobs = new JobService(NewWorkspace(), "demo", _ => true);

        Assert.Throws<InvalidOperationException>(() => jobs.Start("1", new ThrowingLauncher(), "x", id => [id], _temp));

        Assert.Equal(JobStatus.Failed, jobs.List().Single().Status);
    }

    private sealed class ThrowingLauncher : IJobLauncher
    {
        public int Launch(string fileName, IReadOnlyList<string> arguments, string workingDirectory) => throw new InvalidOperationException("no such program");
    }

    [Fact]
    public void Log_writer_takes_the_step_from_progress_lines_only()
    {
        Assert.Equal("reviewer", JobLogWriter.StepOf("12: reviewer"));
        Assert.Null(JobLogWriter.StepOf("Published, waiting for a person"));
        Assert.Null(JobLogWriter.StepOf("  - [rule] message"));
    }

    // ---------- list ----------

    private sealed class FakeSource(params WorkItem[] items) : IWorkItemSource
    {
        public Task<IReadOnlyList<WorkItem>> ListOpenAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkItem>>(items);

        public Task<WorkItem?> GetAsync(string id, CancellationToken ct) => Task.FromResult(items.FirstOrDefault(i => i.Id == id));

        public Task RecordTriageAsync(string id, TriageResult result, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task List_joins_triage_stale_flag_last_run_and_pool()
    {
        var workspace = NewWorkspace();
        var repo = Path.Combine(_temp, "repo");
        var data = Path.Combine(_temp, "data");
        var store = new TriageStore(repo, data);
        var original = new WorkItem("1", "Fix it", "body", []);
        var edited = original with { Body = "new body" };
        await store.SaveAsync(original, new TriageResult(TriageLabel.AgentReady, "small", ["works"], RiskLevel.Low), CancellationToken.None);
        await store.SaveAsync(new WorkItem("2", "Same", "text", []), new TriageResult(TriageLabel.NeedsHuman, "unclear", [], RiskLevel.Medium), CancellationToken.None);
        var pool = new PoolService(workspace, "demo");
        pool.Add("2");
        var runs = new RunStore(workspace, "demo");
        runs.Record(Result("2", ImplementOutcome.NotReady), DateTimeOffset.UtcNow);

        var source = new FakeSource(edited, new WorkItem("2", "Same", "text", [], "https://x/2", "Active", "Ann"), new WorkItem("3", "Fresh", "", []));
        var list = await new WorkItemListService(source, store, pool, runs).ListAsync(CancellationToken.None);

        Assert.True(list[0].Triage!.Stale);
        Assert.Equal("agent-ready", list[0].Triage!.Label);
        Assert.False(list[1].Triage!.Stale);
        Assert.Equal("needs-human", list[1].Triage!.Label);
        Assert.True(list[1].InPool);
        Assert.Equal(1, list[1].PoolPosition);
        Assert.Equal("notready", list[1].LastRun!.Outcome);
        Assert.Equal(("https://x/2", "Active", "Ann"), (list[1].Url, list[1].State, list[1].AssignedTo));
        Assert.Null(list[2].Triage);
        Assert.False(list[2].InPool);
    }

    [Fact]
    public async Task List_works_without_a_registered_project()
    {
        var store = new TriageStore(Path.Combine(_temp, "repo"), Path.Combine(_temp, "data"));
        var list = await new WorkItemListService(new FakeSource(new WorkItem("1", "A", "", [])), store).ListAsync(CancellationToken.None);
        Assert.False(list.Single().InPool);
        Assert.Null(list.Single().LastRun);
    }

    private static ProcessResult Proc(int exit, string stdout) => new(exit, stdout, string.Empty, stdout, false, TimeSpan.Zero);

    // ---------- schedule ----------

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

    [Theory]
    [InlineData("2:30", "02:30")]
    [InlineData("23:59", "23:59")]
    [InlineData(" 07:05 ", "07:05")]
    [InlineData("24:00", null)]
    [InlineData("12:60", null)]
    [InlineData("noon", null)]
    [InlineData(null, null)]
    public void Time_is_normalised_or_refused(string? input, string? expected) =>
        Assert.Equal(expected, ScheduleService.NormalizeTime(input));

    [Fact]
    public async Task Schedule_plans_without_installing_and_installs_when_asked()
    {
        var workspace = NewWorkspace();
        var fake = new FakeScheduler();
        var service = new ScheduleService(workspace, "demo", fake);

        var plan = service.Plan("2:30", 3, "\"autopilot\" --project demo");
        Assert.Equal("\"autopilot\" --project demo pool run --limit 3", plan.Command);
        Assert.Empty(fake.Jobs);
        Assert.Null(await service.ShowAsync(CancellationToken.None));

        await service.SetAsync(plan, CancellationToken.None);
        Assert.Equal("02:30", fake.Jobs["dev-autopilot-demo"].AtTime);
        var shown = await service.ShowAsync(CancellationToken.None);
        Assert.True(shown!.InstalledInOs);
        Assert.Equal(3, shown.Plan.Limit);

        fake.Jobs.Clear();
        Assert.False((await service.ShowAsync(CancellationToken.None))!.InstalledInOs);

        Assert.True(await service.RemoveAsync(CancellationToken.None));
        Assert.Null(await service.ShowAsync(CancellationToken.None));
        Assert.False(await service.RemoveAsync(CancellationToken.None));
    }

    [Fact]
    public void Schedule_plan_refuses_a_bad_time_or_limit()
    {
        var service = new ScheduleService(NewWorkspace(), "demo", new FakeScheduler());
        Assert.Throws<WorkspaceException>(() => service.Plan("25:00", null, "x"));
        Assert.Throws<WorkspaceException>(() => service.Plan("02:00", 0, "x"));
    }

    [Fact]
    public async Task Windows_scheduler_calls_schtasks_with_a_daily_trigger_for_the_current_user()
    {
        var calls = new List<string[]>();
        var scheduler = new WindowsTaskScheduler((args, _) =>
        {
            calls.Add([.. args]);
            return Task.FromResult(Proc(0, string.Empty));
        });

        await scheduler.InstallAsync(new ScheduledJob("dev-autopilot-demo", "02:30", "\"a.exe\" pool run"), CancellationToken.None);
        Assert.True(await scheduler.ExistsAsync("dev-autopilot-demo", CancellationToken.None));
        Assert.True(await scheduler.RemoveAsync("dev-autopilot-demo", CancellationToken.None));

        Assert.Equal(["/Create", "/TN", "dev-autopilot-demo", "/TR", "\"a.exe\" pool run", "/SC", "DAILY", "/ST", "02:30", "/F"], calls[0]);
        Assert.DoesNotContain("/RU", calls[0]);
        Assert.Equal(["/Delete", "/TN", "dev-autopilot-demo", "/F"], calls[2]);
    }

    [Fact]
    public async Task Cron_scheduler_replaces_only_its_own_marked_line()
    {
        var crontab = "0 1 * * * backup.sh\n";
        var scheduler = new CronScheduler((args, input, _) =>
        {
            if (args[0] == "-l")
            {
                return Task.FromResult(Proc(crontab.Length == 0 ? 1 : 0, crontab));
            }

            crontab = input!;
            return Task.FromResult(Proc(0, string.Empty));
        });

        await scheduler.InstallAsync(new ScheduledJob("dev-autopilot-demo", "02:30", "autopilot pool run"), CancellationToken.None);
        await scheduler.InstallAsync(new ScheduledJob("dev-autopilot-demo", "03:15", "autopilot pool run"), CancellationToken.None);

        Assert.Equal("0 1 * * * backup.sh\n15 3 * * * autopilot pool run # dev-autopilot-demo\n", crontab);
        Assert.True(await scheduler.ExistsAsync("dev-autopilot-demo", CancellationToken.None));
        Assert.True(await scheduler.RemoveAsync("dev-autopilot-demo", CancellationToken.None));
        Assert.Equal("0 1 * * * backup.sh\n", crontab);
        Assert.False(await scheduler.RemoveAsync("dev-autopilot-demo", CancellationToken.None));
    }

    // ---------- settings ----------

    private ProjectSettingsService Settings(string json = "{ \"futureKey\": { \"a\": 1 }, \"context\": [\"docs/rules.md\"] }")
    {
        var data = Path.Combine(_temp, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "autopilot.json"), json);
        var repo = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "docs"));
        File.WriteAllText(Path.Combine(repo, "docs", "rules.md"), "rules");
        return new ProjectSettingsService(repo, data);
    }

    [Fact]
    public void Settings_defaults_are_the_safe_ones()
    {
        var view = Settings().Read();
        Assert.Equal("human", view.Config.Merge.By);
        Assert.False(view.Config.AutoQueue.Enabled);
        Assert.Equal(RiskLevel.Low, view.Config.AutoQueue.MaxRisk);
        Assert.Equal("ask", view.Config.Trust);
        Assert.Empty(view.Problems);
    }

    [Fact]
    public void Save_merges_the_change_and_keeps_unknown_keys()
    {
        var service = Settings();

        var saved = service.Save(JsonNode.Parse("{ \"trust\": \"safe\", \"autoQueue\": { \"enabled\": true, \"maxRisk\": \"medium\" } }")!.AsObject());

        Assert.Equal("safe", saved.Config.Trust);
        Assert.True(saved.Config.AutoQueue.Enabled);
        Assert.Equal(RiskLevel.Medium, saved.Config.AutoQueue.MaxRisk);
        var text = File.ReadAllText(service.Path);
        Assert.Contains("\"futureKey\"", text);
        Assert.Contains("\"a\": 1", text);
        Assert.Contains("docs/rules.md", text);
    }

    [Theory]
    [InlineData("{ \"trust\": \"always\" }", "trust")]
    [InlineData("{ \"merge\": { \"by\": \"agent\" } }", "merge.by")]
    [InlineData("{ \"gate\": { \"maxFilesChanged\": 0 } }", "gate.maxFilesChanged")]
    [InlineData("{ \"autoQueue\": { \"maxRisk\": \"extreme\" } }", "autoQueue")]
    public void Save_refuses_a_bad_value_and_writes_nothing(string change, string mention)
    {
        var service = Settings();
        var before = File.ReadAllText(service.Path);

        var error = Assert.Throws<SettingsInvalidException>(() => service.Save(JsonNode.Parse(change)!.AsObject()));

        Assert.Contains(mention, error.Message);
        Assert.Equal(before, File.ReadAllText(service.Path));
    }

    [Fact]
    public void Read_reports_the_problems_of_an_existing_file()
    {
        var view = Settings("{ \"trust\": \"nope\" }").Read();
        Assert.Contains(view.Problems, p => p.Contains("trust", StringComparison.Ordinal));
        Assert.Throws<ConfigException>(() => Settings("[1]").Read());
    }

    [Fact]
    public void Context_files_can_be_read_and_written_only_inside_the_listed_entries()
    {
        var service = Settings("{ \"context\": [\"docs/rules.md\", \"docs/decisions/\"] }");

        Assert.Equal("rules", service.ReadContext("docs/rules.md"));
        service.WriteContext("docs/decisions/001.md", "decided");
        Assert.Equal("decided", service.ReadContext("docs/decisions/001.md"));
        Assert.Equal(["docs/decisions/001.md", "docs/rules.md"], service.ListContextFiles());
        Assert.Null(service.ReadContext("docs/decisions/missing.md"));

        Assert.Throws<SettingsInvalidException>(() => service.WriteContext("src/Program.cs", "x"));
        Assert.Throws<SettingsInvalidException>(() => service.WriteContext("docs/decisions/../../outside.md", "x"));
        Assert.Throws<SettingsInvalidException>(() => service.WriteContext("/etc/passwd", "x"));
        Assert.Throws<SettingsInvalidException>(() => service.WriteContext("docs/rules.md", new string('x', ProjectSettingsService.MaxContextFileBytes + 1)));
        Assert.Equal("rules", service.ReadContext("docs/rules.md"));
    }
}
