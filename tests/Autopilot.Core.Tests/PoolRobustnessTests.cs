using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Local;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Pool;
using Autopilot.Core.Runs;
using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;
using Ws = Autopilot.Core.Workspace.Workspace;

namespace Autopilot.Core.Tests;

/// <summary>The four defects of the first unattended runs: topic check, shared paths, dead runs, budget stops.</summary>
public sealed class PoolRobustnessTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);
    private readonly Ws _workspace;
    private readonly PoolService _pool;
    private readonly PoolStateStore _state;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 28, 1, 0, 0, TimeSpan.Zero));
    private readonly FakeMaster _master = new();
    private readonly FakeTasks _tasks = new();
    private PoolConfig _config = new() { Unattended = true };
    private int _pid = 1;

    public PoolRobustnessTests()
    {
        _workspace = new Ws(Path.Combine(_temp, "ws"));
        _pool = new PoolService(_workspace, "demo");
        _state = new PoolStateStore(_workspace, "demo");
        _master.Answer = _ => new MasterAnswer(true, 1, "Option 1.", "It is the recommended one.", PoolCategories.Other, true);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private static ImplementResult Result(string id, ImplementOutcome outcome, string message = "ok") => new(id, outcome, message, "autopilot/" + id);

    private PoolRunner Runner(Func<string, Task<ImplementResult>> implement, Func<int, bool>? alive = null, TimeSpan? heartbeat = null, Func<double?>? usage = null)
    {
        var isAlive = alive ?? (_ => false);
        return new PoolRunner(_pool, new RunStore(_workspace, "demo"), new ProjectRunLock(_workspace, isAlive, () => _pid, _clock), "demo", (id, _) => implement(id),
            new PoolRunOptions
            {
                Config = () => _config,
                Master = _master,
                Tasks = _tasks,
                State = _state,
                TaskLock = new TaskRunLock(_workspace, isAlive, () => _pid, _clock),
                Clock = _clock,
                IsProcessAlive = isAlive,
                UsagePercent = usage,
                CurrentPid = () => _pid,
                PollInterval = TimeSpan.FromMilliseconds(20),
                HeartbeatInterval = heartbeat ?? TimeSpan.FromMilliseconds(20),
            });
    }

    private Task<PoolRunResult> RunAsync(Func<string, Task<ImplementResult>> implement, Func<int, bool>? alive = null) =>
        Runner(implement, alive).RunAsync(null, Path.Combine(_temp, "report"), CancellationToken.None);

    private void Add(params string[] ids)
    {
        foreach (var id in ids)
        {
            _pool.Add(id);
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "The condition did not become true in time.");
    }

    // ---------- 259: the UI's triage lock, and a pool that never crashes ----------

    [Fact]
    public async Task A_task_whose_triage_lock_a_live_process_holds_is_not_triaged_by_the_pool_and_is_parked_naming_the_holder()
    {
        Add("1", "2");
        _tasks.Untriaged.Add("1");
        var ui = new TaskRunLock(_workspace, _ => true, () => 99, _clock);
        using var uiLock = ui.AcquireTriage("demo", "1", "web UI triage");

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)), alive: _ => true);

        Assert.DoesNotContain("1", _tasks.Triaged);
        var parked = Assert.Single(result.Parked);
        Assert.Equal("1", parked.WorkItemId);
        Assert.Contains("web UI triage", parked.Reason, StringComparison.Ordinal);
        Assert.Contains("99", parked.Reason, StringComparison.Ordinal);
        Assert.Equal(["2"], result.Done.Select(d => d.WorkItemId));
    }

    [Fact]
    public async Task A_task_the_UI_is_triaging_that_already_has_a_stored_result_is_implemented_from_that_result()
    {
        Add("1");
        var ui = new TaskRunLock(_workspace, _ => true, () => 99, _clock);
        using var uiLock = ui.AcquireTriage("demo", "1", "web UI triage");

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)), alive: _ => true);

        Assert.Empty(_tasks.Triaged);
        Assert.Empty(result.Parked);
        Assert.Equal("1", Assert.Single(result.Done).WorkItemId);
    }

    [Fact]
    public async Task A_locked_worktree_error_parks_that_task_and_the_pool_goes_on_and_writes_its_report()
    {
        Add("1", "2", "3");
        _tasks.Faults["2"] = new IOException("The process cannot access the file 'C:\\wt\\@triage-2' because it is being used by another process.");

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Equal(["1", "3"], result.Done.Select(d => d.WorkItemId).Order());
        var parked = Assert.Single(result.Parked);
        Assert.Equal("2", parked.WorkItemId);
        Assert.Contains("another process", parked.Reason, StringComparison.Ordinal);
        Assert.True(File.Exists(result.ReportPath));
        Assert.Contains("2:", await File.ReadAllTextAsync(result.ReportPath!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unexpected_exception_in_the_pool_loop_still_writes_the_report()
    {
        Add("1");
        var runner = new PoolRunner(_pool, new RunStore(_workspace, "demo"), new ProjectRunLock(_workspace, _ => false, () => _pid, _clock), "demo",
            (id, _) => Task.FromResult(Result(id, ImplementOutcome.Published)),
            new PoolRunOptions
            {
                Config = () => throw new InvalidOperationException("config exploded"),
                Unattended = false,
                Clock = _clock,
                CurrentPid = () => _pid,
            });

        var result = await runner.RunAsync(null, Path.Combine(_temp, "report"), CancellationToken.None);

        Assert.Contains("config exploded", result.StoppedBecause, StringComparison.Ordinal);
        Assert.False(result.Succeeded);
        Assert.True(File.Exists(result.ReportPath));
    }

    // ---------- 229: the topic comes from triage and the master, a narrow safety net stays ----------

    [Fact]
    public async Task The_texts_of_tasks_229_232_and_185_reach_the_master_instead_of_being_parked_for_a_word()
    {
        Add("229", "232", "185");
        _tasks.Infos["229"] = new PoolTaskInfo(
            "Pool master parks a task for the topic 'settings' only because the text mentions a config option",
            "Task 185 (stale local base branch) was parked as 'The question is about settings, which is always left to a person' because the question text mentions a config option. The alwaysAsk check is a keyword match and over-triggers. Make the topic come from the master/triage as a structured field (and only keyword-match as a safety net on a narrow list), show the matched word in the park reason, and add a test with this exact text.",
            ["src/Autopilot.Core/Pool/**", "tests/Autopilot.Core.Tests/**"], [], Topic: PoolCategories.Other, PathSource: PathSource.Triage);
        _tasks.Questions["229"] = "Part of this already exists: the master already returns a structured `category`, and `ParkReason` already checks it. The over-trigger is the keyword pre-check, which scans the whole task text before the master is asked. Narrowing it also loosens the guard for secrets, paid and stored-data, so someone has to decide what the safety net keeps. Which scope do you want? I recommend 1: it fixes the settings false positive and keeps the guard for the high-risk topics.";
        _tasks.Infos["232"] = new PoolTaskInfo(
            "Pool never runs two tasks together: every task shares the project's writable paths",
            "In the first real pool run with concurrency 2 every task waited with 'shares files with task 185' because paths come from the project's writablePaths unless the task text has a Paths: line, so nothing ran in parallel. Triage should output the expected write paths per task (structured field), the pool should use them, and the Pool page should show the overlap in plain words. Until then parallelism is theoretical.",
            ["src/Autopilot.Core/Prompts/**"], [], Topic: null, PathSource: PathSource.Triage);
        _tasks.Questions["232"] = "Should this ship as one change or in steps, and what should the pool do when triage's guessed paths are wrong or missing? The item spans the triage output schema and prompt, the pool scheduler and the Pool web page. Wrong guesses mean two tasks may write the same files, so the fallback needs a decision. I recommend option 1: do the structured field and the pool use first.";
        _tasks.Infos["185"] = new PoolTaskInfo(
            "Triage and implement silently use a stale local base branch",
            "Steps: register a project whose local main is behind origin/main, triage a task about code that exists only on origin/main. Expected: fetch and base on origin/<base> (or at least doctor and the run header warn 'main is N commits behind origin/main'), and show the commit used for the run in the UI.",
            ["src/Autopilot.Core/Git/**"], [], Topic: PoolCategories.Other, PathSource: PathSource.Triage);
        _tasks.Questions["185"] = "Which fix do you want when the local base branch is behind origin: (A) fetch and base runs on origin/<base>, (B) only warn in doctor, the run header and the UI, or (C) warn by default and fetch only when a config option is set? The item allows both A and B, and A adds network access and changes which commit every run uses. I recommend C: it is safe for offline use, local-only branches and users with unpushed local commits, and it still fixes the silent stale base.";

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Equal(["185", "229", "232"], _master.Asked.Order());
        Assert.Equal(["185", "229", "232"], result.Done.Select(d => d.WorkItemId).Order());
        Assert.Empty(result.Parked);
        Assert.Empty(_state.Parked());
        Assert.Equal(3, _state.Decisions().Count);
    }

    [Fact]
    public async Task A_topic_triage_gave_parks_the_task_and_the_reason_names_it()
    {
        Add("1");
        _tasks.Infos["1"] = new PoolTaskInfo("Choose the retention", "Body", ["src/1/**"], [], Topic: PoolCategories.Paid, PathSource: PathSource.Triage);
        _tasks.Questions["1"] = "How long do we keep it?";

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Empty(_master.Asked);
        var parked = Assert.Single(_state.Parked());
        Assert.Equal(PoolCategories.Paid, parked.Category);
        Assert.Contains("Triage says the question is about paid", parked.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_masters_own_topic_parks_the_task_and_the_reason_says_who_said_it()
    {
        Add("1");
        _tasks.Questions["1"] = "Which retention?";
        _master.Answer = _ => new MasterAnswer(true, 1, "30 days.", "Fine.", PoolCategories.Settings, true);

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Equal(["1"], _master.Asked);
        Assert.Contains("The pool master says the question is about settings", Assert.Single(result.Parked).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Use this key: -----BEGIN RSA PRIVATE KEY----- MIIE...", "a private key block")]
    [InlineData("The token is ghp_abcdefghijklmnopqrstuvwxyz0123456789 for the bot.", "a GitHub token")]
    [InlineData("Set password = Zx81hunter2Qq in the compose file.", "\"password\"")]
    public async Task A_real_secret_value_in_the_text_is_parked_without_asking_the_master(string text, string named)
    {
        Add("1");
        _tasks.Infos["1"] = new PoolTaskInfo("Wire the client", text, ["src/1/**"], [], PathSource: PathSource.Triage);
        _tasks.Questions["1"] = "Which header name?";

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Empty(_master.Asked);
        var parked = Assert.Single(_state.Parked());
        Assert.Equal(PoolCategories.Secrets, parked.Category);
        Assert.Contains(named, parked.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", parked.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_to_a_sensitive_path_is_parked_but_the_projects_default_paths_are_not_a_signal()
    {
        Add("1", "2");
        _tasks.Infos["1"] = new PoolTaskInfo("Add a column", "Body", ["src/Db/Migrations/**"], [], PathSource: PathSource.Triage);
        _tasks.Infos["2"] = new PoolTaskInfo("Sort the list", "Body", ["**/.env", "src/**"], [], PathSource: PathSource.Project);
        _tasks.Questions["1"] = "Which type?";
        _tasks.Questions["2"] = "Sort by name or date?";

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Equal(["2"], _master.Asked);
        var parked = Assert.Single(_state.Parked());
        Assert.Equal("1", parked.WorkItemId);
        Assert.Equal(PoolCategories.StoredData, parked.Category);
        Assert.Contains("src/Db/Migrations/**", parked.Reason, StringComparison.Ordinal);
        Assert.Contains("database migrations", parked.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Words_alone_are_no_signal_and_only_categories_in_the_list_count()
    {
        string[] all = [.. PoolCategories.All];
        Assert.Null(MasterGuard.SafetyNet("Show the secrets page, change the password policy and the rules, settings, billing and the config option.", [], all));
        Assert.Null(MasterGuard.SafetyNet("The password is set from the environment.", ["src/**"], all));
        Assert.Null(MasterGuard.SafetyNet("password: required", [], all));
        Assert.NotNull(MasterGuard.SafetyNet("Use AKIAABCDEFGHIJKLMNOP as the key id.", [], all));
        Assert.Null(MasterGuard.SafetyNet("Use AKIAABCDEFGHIJKLMNOP as the key id.", [], [PoolCategories.Paid]));
        Assert.NotNull(MasterGuard.SafetyNet("x", ["config/.env.production"], all));
        Assert.NotNull(MasterGuard.SafetyNet("x", ["deploy/secrets/**"], all));
        Assert.Null(MasterGuard.SafetyNet("x", ["src/**", "docs/**"], all));
        Assert.Null(MasterGuard.SafetyNet("x", ["config/.env.production"], [PoolCategories.Paid]));
    }

    // ---------- 232: paths come from triage, tasks with disjoint paths run together ----------

    [Fact]
    public async Task Two_tasks_with_disjoint_triage_paths_run_together_with_concurrency_two()
    {
        Add("1", "2");
        _config = _config with { Concurrency = 2 };
        _tasks.Infos["1"] = new PoolTaskInfo("One", string.Empty, ["src/Pool/**", "tests/Pool/**"], [], PathSource: PathSource.Triage);
        _tasks.Infos["2"] = new PoolTaskInfo("Two", string.Empty, ["src/Prompts/**", "tests/Prompts/**"], [], PathSource: PathSource.Triage);
        var running = 0;
        var peak = 0;
        var together = new TaskCompletionSource();

        var result = await RunAsync(async id =>
        {
            var now = Interlocked.Increment(ref running);
            peak = Math.Max(peak, now);
            if (now == 2)
            {
                together.TrySetResult();
            }

            await Task.WhenAny(together.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Interlocked.Decrement(ref running);
            return Result(id, ImplementOutcome.Published);
        });

        Assert.Equal(2, peak);
        Assert.Equal(2, result.Done.Count);
    }

    [Fact]
    public async Task A_waiting_task_says_in_plain_words_which_paths_collide()
    {
        Add("1", "2");
        _config = _config with { Concurrency = 2 };
        _tasks.Infos["1"] = new PoolTaskInfo("One", string.Empty, ["src/Pool/**"], [], PathSource: PathSource.Triage);
        _tasks.Infos["2"] = new PoolTaskInfo("Two", string.Empty, ["src/Pool/**"], [], PathSource: PathSource.Triage);
        var release = new TaskCompletionSource();
        var run = RunAsync(async id => { await release.Task; return Result(id, ImplementOutcome.Published); });

        await WaitAsync(() => _state.Slots().Any(s => s.WorkItemId == "2" && s.State == "waiting"));
        Assert.Equal("shares files with task 1 (both change src/Pool/**)", _state.Slots().Single(s => s.WorkItemId == "2").Detail);
        release.SetResult();
        await run;
    }

    [Fact]
    public async Task A_task_without_known_paths_holds_others_back_until_its_triage_gives_them()
    {
        Add("1", "2");
        _config = _config with { Concurrency = 2 };
        _tasks.Infos["1"] = new PoolTaskInfo("One", string.Empty, ["src/**"], [], PathSource: PathSource.Project);
        _tasks.Infos["2"] = new PoolTaskInfo("Two", string.Empty, ["src/b/**"], [], PathSource: PathSource.Triage);
        var started = new List<string>();
        var release = new TaskCompletionSource();
        var run = RunAsync(async id =>
        {
            lock (started) { started.Add(id); }
            await release.Task;
            return Result(id, ImplementOutcome.Published);
        });

        await WaitAsync(() => _state.Slots().Any(s => s.WorkItemId == "2" && s.State == "waiting"));
        var detail = _state.Slots().Single(s => s.WorkItemId == "2").Detail;
        Assert.Contains("shares files with task 1", detail, StringComparison.Ordinal);
        Assert.Contains("expected paths of task 1 are not known yet", detail, StringComparison.Ordinal);

        // Triage of task 1 finishes and narrows its paths: task 2 may start beside it.
        _tasks.Infos["1"] = new PoolTaskInfo("One", string.Empty, ["src/a/**"], [], PathSource: PathSource.Triage);
        await WaitAsync(() => { lock (started) { return started.Count == 2; } });
        release.SetResult();
        await run;
    }

    [Fact]
    public void Paths_overlap_only_when_a_file_can_match_both_patterns()
    {
        Assert.False(PathOverlap.Overlaps(["src/a/x.cs"], ["src/a/*.md"]));
        Assert.True(PathOverlap.Overlaps(["src/a/x.cs"], ["src/a/*.cs"]));
        Assert.True(PathOverlap.Overlaps(["src/a/x.cs"], ["src/a/x.cs"]));
        Assert.False(PathOverlap.Overlaps(["src/a/x.cs"], ["src/a/y.cs"]));
        Assert.True(PathOverlap.Overlaps(["docs/x.md"], ["**"]));
        Assert.False(PathOverlap.Overlaps(["tests/Pool/**"], ["src/Pool/**"]));
        var pair = PathOverlap.Find(["docs/**", "src/a/**"], ["src/a/z.cs"]);
        Assert.Equal(("src/a/**", "src/a/z.cs"), pair!.Value);
        Assert.Equal("src/a/** and src/a/z.cs cover the same files", PathOverlap.Describe(pair!.Value));
        Assert.Equal("both change src/a/**", PathOverlap.Describe(("src/a/**", "src/a/**")));
    }

    [Fact]
    public async Task The_real_task_source_takes_paths_from_the_text_then_from_triage_then_from_the_project()
    {
        var root = Path.Combine(_temp, "project");
        var store = new TriageStore(root);
        var items = new Dictionary<string, WorkItem>
        {
            ["1"] = new("1", "Has a line", "Do it.\nPaths: src/Line/**", []),
            ["2"] = new("2", "Triaged", "Do it.", []),
            ["3"] = new("3", "Unknown", "Do it.", ["depends-on:2"]),
            ["4"] = new("4", "Edited after triage", "Do it again.", []),
        };
        await store.SaveAsync(items["1"], new TriageResult(TriageLabel.AgentReady, "ok", ["a"], RiskLevel.Low, Topic: "other", WritePaths: ["src/Ignored/**"]), default);
        await store.SaveAsync(items["2"], new TriageResult(TriageLabel.AgentReady, "ok", ["a"], RiskLevel.Low, Topic: "settings", WritePaths: ["src/Triaged/**", "tests/Triaged/**"]), default);
        await store.SaveAsync(items["4"] with { Body = "Old text." }, new TriageResult(TriageLabel.AgentReady, "ok", ["a"], RiskLevel.Low, WritePaths: ["src/Old/**"]), default);
        var tasks = new PipelinePoolTasks(null!, new MapItems(items), store, null, null!, new AutopilotConfig { WritablePaths = ["src/**", "tests/**"] }, null);

        var line = (await tasks.DescribeAsync("1", default))!;
        Assert.Equal(["src/Line/**"], line.Paths);
        Assert.Equal(PathSource.Declared, line.PathSource);

        var triaged = (await tasks.DescribeAsync("2", default))!;
        Assert.Equal(["src/Triaged/**", "tests/Triaged/**"], triaged.Paths);
        Assert.Equal(PathSource.Triage, triaged.PathSource);
        Assert.Equal("settings", triaged.Topic);

        var unknown = (await tasks.DescribeAsync("3", default))!;
        Assert.Equal(["src/**", "tests/**"], unknown.Paths);
        Assert.Equal(PathSource.Project, unknown.PathSource);
        Assert.Equal(["2"], unknown.DependsOn);

        var stale = (await tasks.DescribeAsync("4", default))!;
        Assert.Equal(PathSource.Project, stale.PathSource); // the text changed since triage, so its paths are not trusted
    }

    private sealed class MapItems(Dictionary<string, WorkItem> items) : IWorkItemSource
    {
        public Task<IReadOnlyList<WorkItem>> ListOpenAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<WorkItem>>([.. items.Values]);

        public Task<WorkItem?> GetAsync(string id, CancellationToken ct) => Task.FromResult(items.GetValueOrDefault(id));

        public Task RecordTriageAsync(string id, TriageResult result, CancellationToken ct) => Task.CompletedTask;
    }

    // ---------- 234: a run that died is noticed through its heartbeat ----------

    [Fact]
    public void A_slot_and_the_locks_of_a_run_that_stopped_beating_are_released_even_when_the_process_id_lives_on()
    {
        const int dead = 100;
        var run = new ProjectRunLock(_workspace, _ => true, () => dead, _clock);
        var taskLocks = new TaskRunLock(_workspace, _ => true, () => dead, _clock);
        using var project = run.Acquire("demo", "pool run");
        using var task = taskLocks.Acquire("demo", "7", "pool run");
        _state.SetSlot("7", "running", "slot 1", _clock.GetUtcNow());
        _state.SetSlot("8", "waiting", "shares files with task 7", _clock.GetUtcNow());
        _state.Beat(dead, _clock.GetUtcNow());

        // Two minutes of silence is still a live run.
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(_state.ReleaseDead(_clock.GetUtcNow(), PoolStateStore.DefaultStaleAfter, _ => true, 1));
        Assert.NotNull(run.Holder("demo"));

        // A few minutes of silence is a dead one: the process id may belong to something else by now.
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(run.Holder("demo"));
        Assert.Null(taskLocks.Holder("demo", "7"));
        var interrupted = _state.ReleaseDead(_clock.GetUtcNow(), PoolStateStore.DefaultStaleAfter, _ => true, 1);

        Assert.Equal(["7"], interrupted);
        var slot = Assert.Single(_state.Slots());
        Assert.Equal(("7", "interrupted"), (slot.WorkItemId, slot.State));
        Assert.Contains("run was interrupted", slot.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_state.Beats());
        using var again = new TaskRunLock(_workspace, _ => true, () => 1, _clock).Acquire("demo", "7", "next run");
        using var projectAgain = new ProjectRunLock(_workspace, _ => true, () => 1, _clock).Acquire("demo", "next run");
    }

    [Fact]
    public void A_live_run_that_beats_keeps_its_slots_and_a_job_without_a_beat_is_left_alone()
    {
        _state.SetSlot("7", "running", "slot 1", _clock.GetUtcNow());
        _state.Beat(100, _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(1));
        _state.Beat(100, _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(_state.ReleaseDead(_clock.GetUtcNow(), PoolStateStore.DefaultStaleAfter, _ => true, 1));
        Assert.Equal("running", Assert.Single(_state.Slots()).State);

        // The process is gone: no need to wait for the silence.
        Assert.Equal(["7"], _state.ReleaseDead(_clock.GetUtcNow(), PoolStateStore.DefaultStaleAfter, _ => false, 1));

        // A holder that never beats (an older build, a plain job) is not touched.
        using var job = new ProjectRunLock(_workspace, _ => true, () => 300, _clock).Acquire("demo", "job");
        _state.SetSlot("9", "running", null, _clock.GetUtcNow());
        Assert.Empty(_state.ReleaseDead(_clock.GetUtcNow(), PoolStateStore.DefaultStaleAfter, _ => true, 1));
    }

    [Fact]
    public async Task The_next_run_after_a_killed_one_takes_over_its_task_and_says_so()
    {
        Add("7");
        const int dead = 100;
        _pid = 1;
        // What the killed run left: the project lock, the task lock, a running slot and an old heartbeat.
        var killedProject = new ProjectRunLock(_workspace, _ => true, () => dead, _clock).Acquire("demo", "pool run");
        var killedTask = new TaskRunLock(_workspace, _ => true, () => dead, _clock).Acquire("demo", "7", "pool run");
        _state.SetSlot("7", "running", "slot 1", _clock.GetUtcNow());
        _state.Beat(dead, _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(10));
        _tasks.Leftovers["7"] = "Kept the branch autopilot/7 left over from an earlier run as autopilot/7-attempt-1 and started the task on a new one.";

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)), alive: pid => pid == dead);

        Assert.Equal(["7"], result.Recovered);
        Assert.Equal(["7"], result.Done.Select(d => d.WorkItemId));
        Assert.Equal(["7"], _tasks.LeftoversAsked);
        Assert.Contains("attempt-1", Assert.Single(result.CleanedUp), StringComparison.Ordinal);
        Assert.Empty(_state.Slots());
        Assert.Empty(_state.Beats());
        var report = File.ReadAllText(result.ReportPath!);
        Assert.Contains("Interrupted earlier", report, StringComparison.Ordinal);
        Assert.Contains("Cleaned up after a run that died", report, StringComparison.Ordinal);
        killedProject.Dispose();
        killedTask.Dispose();
    }

    [Fact]
    public async Task A_run_that_is_still_alive_is_not_taken_over()
    {
        Add("7");
        const int other = 100;
        _clock.Advance(TimeSpan.Zero);
        using var live = new ProjectRunLock(_workspace, _ => true, () => other, _clock).Acquire("demo", "pool run");
        _state.Beat(other, _clock.GetUtcNow());

        await Assert.ThrowsAsync<RunLockHeldException>(() => RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)), alive: _ => true));
        Assert.Empty(_tasks.LeftoversAsked);
    }

    [Fact]
    public async Task A_running_pool_beats_and_removes_its_beat_when_it_ends()
    {
        Add("7");
        var release = new TaskCompletionSource();
        var run = RunAsync(async id => { await release.Task; return Result(id, ImplementOutcome.Published); });

        await WaitAsync(() => _state.Beats().Count == 1);
        var first = _state.Beats()[0].At;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await WaitAsync(() => _state.Beats()[0].At > first);
        release.SetResult();
        await run;

        Assert.Empty(_state.Beats());
    }

    [Fact]
    public async Task A_task_whose_lock_another_run_holds_is_not_cleaned_up()
    {
        Add("7");
        using var held = new TaskRunLock(_workspace, _ => true, () => 777, _clock).Acquire("demo", "7", "another pool");

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)), alive: pid => pid == 777);

        Assert.Empty(_tasks.LeftoversAsked);
        Assert.Contains("already being worked on", Assert.Single(result.NotReached), StringComparison.Ordinal);
    }

    // ---------- 235: a budget stop is a pool state, not a task failure ----------

    [Fact]
    public async Task A_budget_that_runs_out_before_the_master_classifies_a_failure_keeps_the_task_queued_and_uses_no_retry()
    {
        Add("1", "2");
        _config = _config with { Budget = new PoolBudgetConfig { MaxAgentRuns = 1 } };
        var calls = 0;

        var result = await RunAsync(id => { calls++; return Task.FromResult(Result(id, ImplementOutcome.Failed, "Build failed: 3 tests red")); });

        Assert.Equal(1, calls);
        Assert.Empty(_state.Decisions()); // no 'failure / Task' decision
        Assert.Empty(_state.Parked());
        Assert.Empty(result.Parked);
        Assert.Empty(result.Rejected);
        Assert.Equal(["1", "2"], _pool.List().Select(e => e.WorkItemId)); // both stay queued
        Assert.Contains("budget of 1 agent runs", result.StoppedBecause, StringComparison.Ordinal);
        Assert.Equal(["1"], _tasks.Retried); // the failed attempt's branch is set aside, but the retry counter is untouched
        Assert.Empty(_tasks.Answers);
    }

    [Fact]
    public async Task A_budget_that_runs_out_before_the_master_answers_does_not_park_the_task()
    {
        Add("1");
        _tasks.Questions["1"] = "Which order?";
        var looks = 0;

        // The usage limit is reached between the pool starting the task and the task asking the master.
        var result = await Runner(id => Task.FromResult(Result(id, ImplementOutcome.Published)), usage: () => ++looks == 1 ? 10 : 90)
            .RunAsync(null, Path.Combine(_temp, "report"), CancellationToken.None);

        Assert.Empty(_master.Asked);
        Assert.Empty(_state.Decisions());
        Assert.Empty(_state.Parked());
        Assert.Empty(result.Parked);
        Assert.Equal(["1"], _pool.List().Select(e => e.WorkItemId));
        Assert.Contains("usage is 90%", result.StoppedBecause, StringComparison.Ordinal);
        Assert.StartsWith("Budget used up", _state.CurrentPause()!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_used_up_budget_shows_as_a_pool_state_with_the_time_it_resets()
    {
        Add("1", "2", "3");
        _config = _config with { Budget = new PoolBudgetConfig { MaxAgentRuns = 2 } };
        var start = _clock.GetUtcNow();

        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Equal(2, result.Done.Count);
        var pause = _state.CurrentPause();
        Assert.NotNull(pause);
        Assert.StartsWith("Budget used up, resets at 2026-11-29 01:00", pause.Reason, StringComparison.Ordinal);
        Assert.Equal(start.AddHours(24), pause.RetryAt);
    }

    [Fact]
    public async Task A_budget_pause_does_not_block_the_next_run_once_the_budget_has_room()
    {
        Add("1", "2");
        _config = _config with { Budget = new PoolBudgetConfig { MaxAgentRuns = 1 } };
        await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));
        Assert.NotNull(_state.CurrentPause());

        // A person raises the limit: the stored pause is only a message, so the pool runs at once.
        _config = _config with { Budget = new PoolBudgetConfig { MaxAgentRuns = 5 } };
        var result = await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Published)));

        Assert.Equal(["2"], result.Done.Select(d => d.WorkItemId));
        Assert.Null(result.StoppedBecause);
        Assert.Null(_state.CurrentPause());
    }

    [Fact]
    public async Task The_budget_resets_when_the_oldest_counted_run_leaves_the_window()
    {
        var at = _clock.GetUtcNow();
        _state.CountAgentRun("1", "implement", at.AddHours(-20));
        _state.CountAgentRun("2", "implement", at.AddHours(-3));
        _state.CountAgentRun("3", "implement", at.AddHours(-30)); // outside the window, does not count

        Assert.Equal(at.AddHours(4), _state.BudgetResetsAt(at, 2)); // the 20-hour-old run leaves in 4 hours
        Assert.Equal(at.AddHours(21), _state.BudgetResetsAt(at, 1)); // both must leave
        Assert.Null(_state.BudgetResetsAt(at, 3));
        await Task.CompletedTask;
    }
}
