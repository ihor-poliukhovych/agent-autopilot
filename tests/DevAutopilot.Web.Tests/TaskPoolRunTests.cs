using System.Net;
using System.Text.Json;
using DevAutopilot.Core;
using DevAutopilot.Core.Jobs;
using DevAutopilot.Core.Local;
using DevAutopilot.Core.Pipeline;
using DevAutopilot.Core.Gate;
using DevAutopilot.Core.Runs;
using DevAutopilot.Core.Tasks;
using DevAutopilot.Core.Pool;

namespace DevAutopilot.Web.Tests;

public sealed class TaskPoolRunTests
{
    private static string Tasks(string key = "demo") => $"/api/projects/{key}/tasks";

    private static async Task<UiFixture> WithProjectAsync()
    {
        var ui = new UiFixture();
        await ui.CreateProjectAsync();
        return ui;
    }

    [Fact]
    public async Task A_local_task_is_added_edited_commented_closed_and_reopened()
    {
        await using var ui = await WithProjectAsync();
        var (status, task) = await ui.PostAsync(Tasks(), new { title = "Add retry", body = "Retry three times.", labels = new[] { "net" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, task.GetProperty("number").GetInt32());

        var (_, edited) = await ui.SendAsync(HttpMethod.Put, Tasks() + "/1", new { title = "Add retry to the client", body = "Retry five times.", labels = new[] { "net", "http" } });
        Assert.Equal("Add retry to the client", edited.GetProperty("task").GetProperty("title").GetString());
        Assert.Equal(2, edited.GetProperty("task").GetProperty("labels").GetArrayLength());

        await ui.PostAsync(Tasks() + "/1/comments", new { text = "Please keep the old timeout." });
        var (_, detail) = await ui.GetAsync(Tasks() + "/1");
        Assert.Equal("Please keep the old timeout.", detail.GetProperty("comments")[0].GetProperty("body").GetString());

        var (_, closed) = await ui.PostAsync(Tasks() + "/1/close");
        Assert.True(closed.GetProperty("changed").GetBoolean());
        var (_, list) = await ui.GetAsync(Tasks());
        Assert.Empty(list.GetProperty("items").EnumerateArray());
        await ui.PostAsync(Tasks() + "/1/reopen");
        var (_, again) = await ui.GetAsync(Tasks());
        Assert.Single(again.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task An_empty_title_is_refused()
    {
        await using var ui = await WithProjectAsync();
        var (status, body) = await ui.PostAsync(Tasks(), new { title = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("title", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_list_shows_triage_and_marks_it_stale_after_the_text_changes()
    {
        await using var ui = await WithProjectAsync();
        await ui.PostAsync(Tasks(), new { title = "Rename a field", body = "Rename Name to FullName." });
        await Triage(ui, 1, TriageLabel.AgentReady, "Small and clear.");

        var (_, list) = await ui.GetAsync(Tasks());
        var triage = list.GetProperty("items")[0].GetProperty("triage");
        Assert.Equal("agent-ready", triage.GetProperty("label").GetString());
        Assert.False(triage.GetProperty("stale").GetBoolean());

        await ui.SendAsync(HttpMethod.Put, Tasks() + "/1", new { body = "Rename Name to DisplayName." });
        var (_, after) = await ui.GetAsync(Tasks());
        Assert.True(after.GetProperty("items")[0].GetProperty("triage").GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task A_needs_human_question_is_answered_and_the_answer_is_stored_on_the_task()
    {
        await using var ui = await WithProjectAsync();
        await ui.PostAsync(Tasks(), new { title = "Speed up search", body = "Make it faster." });
        await Triage(ui, 1, TriageLabel.NeedsHuman, "Which query is slow?");

        var (_, items) = await ui.GetAsync("/api/projects/demo/needs-human");
        Assert.Equal("Which query is slow?", items[0].GetProperty("question").GetString());

        var (status, detail) = await ui.PostAsync(Tasks() + "/1/answer", new { text = "The product search." });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("The product search.", detail.GetProperty("task").GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Contains("The product search.", detail.GetProperty("comments")[0].GetProperty("body").GetString(), StringComparison.Ordinal);

        var (_, after) = await ui.GetAsync("/api/projects/demo/needs-human");
        Assert.True(after[0].GetProperty("stale").GetBoolean(), "the changed text marks the old triage stale, so it is triaged again");
        var (empty, _) = await ui.PostAsync(Tasks() + "/1/answer", new { text = " " });
        Assert.Equal(HttpStatusCode.BadRequest, empty);
    }

    [Fact]
    public async Task Start_launches_one_detached_job_and_is_refused_while_a_run_holds_the_project()
    {
        await using var ui = await WithProjectAsync();
        await ui.PostAsync(Tasks(), new { title = "One" });
        var (status, job) = await ui.PostAsync(Tasks() + "/1/start");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("queued", job.GetProperty("status").GetString());
        var launched = Assert.Single(ui.Launcher.Launched);
        Assert.Equal(["implement", "1", "--job", job.GetProperty("id").GetString()!, "--project", "demo", "--workspace", ui.Workspace.Root], launched.Arguments);
        Assert.Equal(ui.RepoFolder, launched.Folder);

        // Another process now holds the project's lock.
        ui.AliveProcesses.Add(777);
        new ProjectRunLock(ui.Workspace, pid => pid == 777, () => 777).Acquire("demo", "pool run");
        var (conflict, body) = await ui.PostAsync(Tasks() + "/1/start");
        Assert.Equal(HttpStatusCode.Conflict, conflict);
        Assert.Contains("already active", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Single(ui.Launcher.Launched);
    }

    [Fact]
    public async Task The_pool_is_ordered_changed_and_run_now_starts_pool_run_in_the_background()
    {
        await using var ui = await WithProjectAsync();
        foreach (var title in new[] { "A", "B", "C" })
        {
            await ui.PostAsync(Tasks(), new { title });
        }

        foreach (var id in new[] { "1", "2", "3" })
        {
            await ui.PostAsync("/api/projects/demo/pool", new { id });
        }

        await ui.PostAsync("/api/projects/demo/pool/3/move", new { position = 1 });
        var (_, pool) = await ui.GetAsync("/api/projects/demo/pool");
        Assert.Equal(["3", "1", "2"], pool.EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToArray());
        Assert.Equal("C", pool[0].GetProperty("title").GetString());

        await ui.SendAsync(HttpMethod.Delete, "/api/projects/demo/pool/1");
        var (_, run) = await ui.PostAsync("/api/projects/demo/pool/run", new { limit = 2 });
        Assert.True(run.GetProperty("pid").GetInt32() > 0);
        Assert.Equal(["pool", "run", "--project", "demo", "--workspace", ui.Workspace.Root, "--limit", "2"], ui.Launcher.Launched.Single().Arguments);

        var (bad, _) = await ui.PostAsync("/api/projects/demo/pool/run", new { limit = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
    }

    [Fact]
    public async Task Run_now_with_an_empty_pool_is_refused()
    {
        await using var ui = await WithProjectAsync();
        var (status, _) = await ui.PostAsync("/api/projects/demo/pool/run", new { });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Empty(ui.Launcher.Launched);
    }

    [Fact]
    public async Task The_night_run_is_only_installed_after_the_plan_is_confirmed()
    {
        await using var ui = await WithProjectAsync();
        await ui.PostAsync(Tasks(), new { title = "A" });
        await ui.PostAsync("/api/projects/demo/pool", new { id = "1" });

        var (_, plan) = await ui.PostAsync("/api/projects/demo/pool/schedule/plan", new { at = "02:30", limit = 3 });
        Assert.Contains("02:30", plan.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Equal("A", plan.GetProperty("items")[0].GetProperty("title").GetString());
        Assert.Empty(ui.Scheduler.Jobs);

        var (refused, _) = await ui.PostAsync("/api/projects/demo/pool/schedule", new { at = "02:30", confirmed = false });
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Empty(ui.Scheduler.Jobs);

        var (_, shown) = await ui.PostAsync("/api/projects/demo/pool/schedule", new { at = "02:30", limit = 3, confirmed = true });
        Assert.True(shown.GetProperty("installedInOs").GetBoolean());
        var job = Assert.Single(ui.Scheduler.Jobs.Values);
        Assert.Contains("pool run", job.Command, StringComparison.Ordinal);

        await ui.SendAsync(HttpMethod.Delete, "/api/projects/demo/pool/schedule");
        Assert.Empty(ui.Scheduler.Jobs);
        var (badTime, _) = await ui.PostAsync("/api/projects/demo/pool/schedule/plan", new { at = "25:99" });
        Assert.Equal(HttpStatusCode.BadRequest, badTime);
    }

    [Fact]
    public async Task Runs_show_history_gate_report_diff_and_the_agreement_with_the_person()
    {
        await using var ui = await WithProjectAsync();
        var runs = new RunStore(ui.Workspace, "demo");
        var passed = runs.Record(Result("1", GateOutcome.Passed), DateTimeOffset.UtcNow.AddMinutes(-5));
        var rejected = runs.Record(Result("2", GateOutcome.Rejected), DateTimeOffset.UtcNow.AddMinutes(-4));
        var folder = ProjectPaths.RunFolder(ui.RepoFolder, "1", ui.Workspace.ProjectFolder("demo"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "gate.json"), """{"outcome":"Passed","highRisk":false,"findings":[],"files":[],"filesChanged":1,"buildSucceeded":true,"testsSucceeded":true}""");
        await File.WriteAllTextAsync(Path.Combine(folder, "change.patch"), "diff --git a/x b/x\n+added\n-removed\n");
        await File.WriteAllTextAsync(Path.Combine(folder, "PULL_REQUEST.md"), "# Draft");

        var (_, detail) = await ui.GetAsync($"/api/projects/demo/runs/{passed.Id}");
        Assert.True(detail.GetProperty("gate").GetProperty("buildSucceeded").GetBoolean());
        Assert.Contains("+added", detail.GetProperty("diff").GetString(), StringComparison.Ordinal);
        Assert.Equal("# Draft", detail.GetProperty("report").GetString());

        await ui.PostAsync($"/api/projects/demo/runs/{passed.Id}/decision", new { decision = "merged" });
        await ui.PostAsync($"/api/projects/demo/runs/{rejected.Id}/decision", new { decision = "merged" });
        var (_, overview) = await ui.GetAsync("/api/projects/demo/runs");
        var stats = overview.GetProperty("stats");
        Assert.Equal(2, stats.GetProperty("decided").GetInt32());
        Assert.Equal(1, stats.GetProperty("agreed").GetInt32());
        Assert.Equal(1, stats.GetProperty("gateTooStrict").GetInt32());
        Assert.Equal(0.5, stats.GetProperty("agreementRate").GetDouble());
        Assert.Equal(2, overview.GetProperty("runs").GetArrayLength());

        var (bad, _) = await ui.PostAsync($"/api/projects/demo/runs/{passed.Id}/decision", new { decision = "maybe" });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        var (missing, _) = await ui.GetAsync("/api/projects/demo/runs/999");
        Assert.Equal(HttpStatusCode.NotFound, missing);
    }

    [Fact]
    public async Task Stop_ends_exactly_one_job_and_leaves_the_other_running()
    {
        await using var ui = await WithProjectAsync();
        await ui.PostAsync(Tasks(), new { title = "A" });
        await ui.PostAsync(Tasks(), new { title = "B" });
        var jobs = new JobService(ui.Workspace, "demo", pid => ui.AliveProcesses.Contains(pid));
        var first = jobs.Create("1");
        var second = jobs.Create("2");
        ui.AliveProcesses.Add(111);
        ui.AliveProcesses.Add(222);
        jobs.MarkRunning(first.Id, 111);
        jobs.MarkRunning(second.Id, 222);
        jobs.SetStep(first.Id, "implementing");

        var (_, overview) = await ui.GetAsync("/api/projects/demo/runs");
        var active = overview.GetProperty("jobs").EnumerateArray().Where(j => j.GetProperty("job").GetProperty("status").GetString() == "running").ToList();
        Assert.Equal(2, active.Count);
        Assert.Contains(active, j => j.GetProperty("job").GetProperty("step").GetString() == "implementing");

        var (_, stopped) = await ui.PostAsync($"/api/projects/demo/jobs/{first.Id}/stop");
        Assert.True(stopped.GetProperty("stopped").GetBoolean());
        Assert.Equal([111], ui.Killer.Killed);
        Assert.Equal(JobStatus.Cancelled, jobs.Get(first.Id)!.Status);
        Assert.Equal(JobStatus.Running, jobs.Get(second.Id)!.Status);

        var (again, second2) = await ui.PostAsync($"/api/projects/demo/jobs/{first.Id}/stop");
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.False(second2.GetProperty("stopped").GetBoolean());
        var (missing, _) = await ui.PostAsync("/api/projects/demo/jobs/jnope/stop");
        Assert.Equal(HttpStatusCode.NotFound, missing);
    }

    private static ImplementResult Result(string id, GateOutcome gate) =>
        new(id, gate == GateOutcome.Passed ? ImplementOutcome.Published : ImplementOutcome.Rejected, "done", "autopilot/" + id, null, new GateDecision(gate, []));

    private static async Task Triage(UiFixture ui, int number, TriageLabel label, string reason)
    {
        var tasks = new LocalTaskService(ui.Workspace, "demo");
        var item = LocalTaskWorkItemSource.ToItem(tasks.Get(number)!);
        var store = new TriageStore(ui.RepoFolder, ui.Workspace.ProjectFolder("demo"));
        await store.SaveAsync(item, new TriageResult(label, reason, [], RiskLevel.Low), CancellationToken.None);
    }
}
