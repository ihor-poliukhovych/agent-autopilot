using System.Net;
using Autopilot.Core;
using Autopilot.Core.Jobs;
using Autopilot.Core.Local;
using Autopilot.Core.Tasks;

namespace Autopilot.Web.Tests;

/// <summary>Triage as a job, the triage on the task page, the answer on Needs a person, and the filtered task list.</summary>
public sealed class TaskTriageAndListTests
{
    private static string Tasks() => "/api/projects/demo/tasks";

    private static async Task<UiFixture> WithProjectAsync()
    {
        var ui = new UiFixture();
        await ui.CreateProjectAsync();
        return ui;
    }

    private static async Task SaveTriageAsync(UiFixture ui, int number, TriageLabel label, string reason)
    {
        var item = LocalTaskWorkItemSource.ToItem(new LocalTaskService(ui.Workspace, "demo").Get(number)!);
        var store = new TriageStore(ui.RepoFolder, ui.Workspace.ProjectFolder("demo"));
        await store.SaveAsync(item, new TriageResult(label, reason, [], RiskLevel.Low), CancellationToken.None);
    }

    [Fact]
    public async Task Triage_starts_a_triage_job_the_page_can_follow()
    {
        await using var ui = await WithProjectAsync();
        await ui.PostAsync(Tasks(), new { title = "One" });

        var (status, job) = await ui.PostAsync(Tasks() + "/1/triage");

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("triage", job.GetProperty("kind").GetString());
        Assert.True(job.GetProperty("isActive").GetBoolean());
        var launched = Assert.Single(ui.Launcher.Launched);
        Assert.Equal(["triage", "1", "--job", job.GetProperty("id").GetString()!, "--project", "demo", "--workspace", ui.Workspace.Root], launched.Arguments);

        var (_, list) = await ui.GetAsync(Tasks());
        Assert.True(list.GetProperty("triageJobs").GetProperty("1").GetProperty("isActive").GetBoolean());
        Assert.Empty(list.GetProperty("activeJobs").EnumerateObject());
    }

    [Fact]
    public async Task A_second_triage_of_the_same_task_returns_the_running_job_and_another_task_is_refused()
    {
        await using var ui = await WithProjectAsync();
        await ui.PostAsync(Tasks(), new { title = "One" });
        await ui.PostAsync(Tasks(), new { title = "Two" });
        var (_, first) = await ui.PostAsync(Tasks() + "/1/triage");

        var (_, again) = await ui.PostAsync(Tasks() + "/1/triage");
        var (other, body) = await ui.PostAsync(Tasks() + "/2/triage");

        Assert.Equal(first.GetProperty("id").GetString(), again.GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Conflict, other);
        Assert.Contains("being triaged", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Single(ui.Launcher.Launched);
    }

    [Fact]
    public async Task A_failed_triage_shows_its_error_on_the_job_and_the_task_page()
    {
        await using var ui = await WithProjectAsync();
        await ui.PostAsync(Tasks(), new { title = "One" });
        var (_, started) = await ui.PostAsync(Tasks() + "/1/triage");
        var id = started.GetProperty("id").GetString()!;
        new JobService(ui.Workspace, "demo").Finish(id, JobStatus.Failed, "git worktree add failed: Filename too long");

        var (_, job) = await ui.GetAsync($"/api/projects/demo/jobs/{id}");
        Assert.Equal("failed", job.GetProperty("job").GetProperty("status").GetString());
        Assert.Contains("Filename too long", job.GetProperty("job").GetProperty("message").GetString(), StringComparison.Ordinal);

        var (_, detail) = await ui.GetAsync(Tasks() + "/1");
        Assert.Contains("Filename too long", detail.GetProperty("triageJob").GetProperty("message").GetString(), StringComparison.Ordinal);

        // A finished job does not block the next triage.
        var (next, _) = await ui.PostAsync(Tasks() + "/1/triage");
        Assert.Equal(HttpStatusCode.Accepted, next);
        Assert.Equal(2, ui.Launcher.Launched.Count);
    }

    [Fact]
    public async Task A_launcher_that_throws_fails_the_job_and_the_request()
    {
        await using var ui = await WithProjectAsync();
        await ui.PostAsync(Tasks(), new { title = "One" });
        ui.Launcher.Failure = new InvalidOperationException("cannot start");

        var (status, _) = await ui.PostAsync(Tasks() + "/1/triage");

        Assert.NotEqual(HttpStatusCode.Accepted, status);
        var job = new JobService(ui.Workspace, "demo").List().Single();
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("cannot start", job.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_task_page_has_the_triage_result_and_the_stale_flag()
    {
        await using var ui = await WithProjectAsync();
        await ui.PostAsync(Tasks(), new { title = "Rename a field", body = "Rename Name to FullName." });

        var (_, none) = await ui.GetAsync(Tasks() + "/1");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, none.GetProperty("triage").ValueKind);

        await SaveTriageAsync(ui, 1, TriageLabel.AgentReady, "Small and clear.");
        var (_, detail) = await ui.GetAsync(Tasks() + "/1");
        var triage = detail.GetProperty("triage");
        Assert.Equal("agent-ready", triage.GetProperty("label").GetString());
        Assert.Equal("low", triage.GetProperty("risk").GetString());
        Assert.Equal("Small and clear.", triage.GetProperty("reason").GetString());
        Assert.False(triage.GetProperty("stale").GetBoolean());
        Assert.True(triage.TryGetProperty("triagedAt", out _));

        await ui.SendAsync(HttpMethod.Put, Tasks() + "/1", new { body = "Rename Name to DisplayName." });
        var (_, after) = await ui.GetAsync(Tasks() + "/1");
        Assert.True(after.GetProperty("triage").GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task Needs_a_person_shows_the_answer_that_was_given()
    {
        await using var ui = await WithProjectAsync();
        await ui.PostAsync(Tasks(), new { title = "Speed up search", body = "Make it faster." });
        await SaveTriageAsync(ui, 1, TriageLabel.NeedsHuman, "Which query? A: products, B: orders. I recommend A.");

        var (_, before) = await ui.GetAsync("/api/projects/demo/needs-human");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, before[0].GetProperty("answer").ValueKind);

        await ui.PostAsync(Tasks() + "/1/answer", new { text = "A, the product search." });
        await ui.PostAsync(Tasks() + "/1/answer", new { text = "Actually both." });

        var (_, after) = await ui.GetAsync("/api/projects/demo/needs-human");
        Assert.Equal("Actually both.", after[0].GetProperty("answer").GetString());
        Assert.True(after[0].GetProperty("stale").GetBoolean());
    }

    private static async Task<UiFixture> WithManyTasksAsync()
    {
        var ui = await WithProjectAsync();
        for (var i = 1; i <= 7; i++)
        {
            await ui.PostAsync(Tasks(), new { title = i % 2 == 0 ? $"Fix login {i}" : $"Add report {i}" });
        }

        await SaveTriageAsync(ui, 1, TriageLabel.AgentReady, "ok");
        await SaveTriageAsync(ui, 2, TriageLabel.NeedsHuman, "why");
        await SaveTriageAsync(ui, 3, TriageLabel.NeedsHuman, "why");
        await ui.SendAsync(HttpMethod.Put, Tasks() + "/3", new { body = "changed" });
        return ui;
    }

    [Fact]
    public async Task Without_a_page_size_the_list_returns_everything()
    {
        await using var ui = await WithManyTasksAsync();
        var (_, list) = await ui.GetAsync(Tasks());
        Assert.Equal(7, list.GetProperty("items").GetArrayLength());
        Assert.Equal(7, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_list_is_paged_and_a_page_past_the_end_shows_the_last_page()
    {
        await using var ui = await WithManyTasksAsync();

        var (_, first) = await ui.GetAsync(Tasks() + "?pageSize=3&page=1");
        Assert.Equal(["1", "2", "3"], first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(7, first.GetProperty("total").GetInt32());

        var (_, last) = await ui.GetAsync(Tasks() + "?pageSize=3&page=99");
        Assert.Equal(3, last.GetProperty("page").GetInt32());
        Assert.Equal(["7"], last.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
    }

    [Fact]
    public async Task The_list_filters_by_triage_state_and_counts_each_state()
    {
        await using var ui = await WithManyTasksAsync();

        var (_, needs) = await ui.GetAsync(Tasks() + "?state=needs-human");
        Assert.Equal(["2", "3"], needs.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
        var (_, ready) = await ui.GetAsync(Tasks() + "?state=agent-ready");
        Assert.Single(ready.GetProperty("items").EnumerateArray());
        var (_, untriaged) = await ui.GetAsync(Tasks() + "?state=not-triaged");
        Assert.Equal(4, untriaged.GetProperty("total").GetInt32());
        var (_, stale) = await ui.GetAsync(Tasks() + "?state=stale");
        Assert.Equal("3", stale.GetProperty("items")[0].GetProperty("id").GetString());

        var counts = needs.GetProperty("counts");
        Assert.Equal(7, counts.GetProperty("all").GetInt32());
        Assert.Equal(2, counts.GetProperty("needsHuman").GetInt32());
        Assert.Equal(1, counts.GetProperty("agentReady").GetInt32());
        Assert.Equal(4, counts.GetProperty("notTriaged").GetInt32());
        Assert.Equal(1, counts.GetProperty("stale").GetInt32());

        var (bad, body) = await ui.GetAsync(Tasks() + "?state=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Contains("state", body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_list_searches_titles_and_task_numbers_and_combines_with_a_state()
    {
        await using var ui = await WithManyTasksAsync();

        var (_, login) = await ui.GetAsync(Tasks() + "?q=LOGIN");
        Assert.Equal(["2", "4", "6"], login.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
        var (_, byNumber) = await ui.GetAsync(Tasks() + "?q=%235");
        Assert.Equal(["5"], byNumber.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
        var (_, both) = await ui.GetAsync(Tasks() + "?q=login&state=needs-human");
        Assert.Equal(["2"], both.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray());
        var (_, none) = await ui.GetAsync(Tasks() + "?q=nothing");
        Assert.Equal(0, none.GetProperty("total").GetInt32());
        Assert.Equal(7, none.GetProperty("counts").GetProperty("all").GetInt32());
    }
}
