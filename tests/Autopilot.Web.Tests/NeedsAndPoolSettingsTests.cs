using System.Net;
using System.Text.Json;
using Autopilot.Core;
using Autopilot.Core.Local;
using Autopilot.Core.Tasks;

namespace Autopilot.Web.Tests;

/// <summary>The options of a question, Save and continue, the budget fields of the Pool page and its queue.</summary>
public sealed class NeedsAndPoolSettingsTests
{
    private const string Tasks = "/api/projects/demo/tasks";
    private const string Pool = "/api/projects/demo/pool";

    private static async Task<UiFixture> WithTasksAsync(int count = 1)
    {
        var ui = new UiFixture();
        await ui.CreateProjectAsync();
        for (var i = 1; i <= count; i++)
        {
            await ui.PostAsync(Tasks, new { title = "Task " + i, body = "Make it faster." });
        }

        return ui;
    }

    private static async Task SaveTriageAsync(UiFixture ui, int number, string reason, IReadOnlyList<string>? options = null, int? recommended = null)
    {
        var item = LocalTaskWorkItemSource.ToItem(new LocalTaskService(ui.Workspace, "demo").Get(number)!);
        var store = new TriageStore(ui.RepoFolder, ui.Workspace.ProjectFolder("demo"));
        await store.SaveAsync(item, new TriageResult(TriageLabel.NeedsHuman, reason, [], RiskLevel.Low, options, recommended), CancellationToken.None);
    }

    private static string[] Strings(JsonElement list) => [.. list.EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public async Task A_question_with_three_options_is_returned_with_the_options_and_the_recommended_one()
    {
        await using var ui = await WithTasksAsync();
        await SaveTriageAsync(ui, 1, "Which search? (A) products, (B) orders, (C) both. I recommend B.", ["products", "orders", "both"], 2);

        var (_, items) = await ui.GetAsync("/api/projects/demo/needs-human");

        var item = Assert.Single(items.EnumerateArray());
        Assert.Equal(["products", "orders", "both"], Strings(item.GetProperty("options")));
        Assert.Equal(1, item.GetProperty("recommended").GetInt32());
        Assert.Equal("Which search?", item.GetProperty("question").GetString());
        Assert.Contains("I recommend B", item.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_old_result_with_options_only_in_the_prose_is_parsed()
    {
        await using var ui = await WithTasksAsync();
        await SaveTriageAsync(ui, 1, "Which search? (A) products, (B) orders, (C) both. I recommend C because it is the least surprising.");

        var (_, items) = await ui.GetAsync("/api/projects/demo/needs-human");

        var item = items[0];
        Assert.Equal(["products", "orders", "both"], Strings(item.GetProperty("options")));
        Assert.Equal(2, item.GetProperty("recommended").GetInt32());
    }

    [Fact]
    public async Task A_question_without_options_has_none_and_stays_whole()
    {
        await using var ui = await WithTasksAsync();
        await SaveTriageAsync(ui, 1, "Who should own this?");

        var (_, items) = await ui.GetAsync("/api/projects/demo/needs-human");

        Assert.Empty(items[0].GetProperty("options").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("recommended").ValueKind);
        Assert.Equal("Who should own this?", items[0].GetProperty("question").GetString());
    }

    [Fact]
    public async Task Save_and_continue_stores_the_answer_queues_the_task_and_starts_a_triage()
    {
        await using var ui = await WithTasksAsync();
        await SaveTriageAsync(ui, 1, "Which? (A) one (B) two.");

        var (status, result) = await ui.PostAsync(Tasks + "/1/answer-and-continue", new { text = "Option B: two" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(result.GetProperty("inPool").GetBoolean());
        Assert.Equal("triage", result.GetProperty("triageJob").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("note").ValueKind);
        Assert.Contains("Option B: two", result.GetProperty("task").GetProperty("task").GetProperty("body").GetString(), StringComparison.Ordinal);
        var launched = Assert.Single(ui.Launcher.Launched);
        Assert.Equal("triage", launched.Arguments[0]);
        var (_, pool) = await ui.GetAsync(Pool);
        Assert.Equal("1", Assert.Single(pool.EnumerateArray()).GetProperty("id").GetString());
        var (_, needs) = await ui.GetAsync("/api/projects/demo/needs-human");
        Assert.True(needs[0].GetProperty("inPool").GetBoolean());
        Assert.True(needs[0].GetProperty("stale").GetBoolean());
    }

    [Fact]
    public async Task Save_and_continue_keeps_the_answer_and_the_pool_entry_when_another_triage_is_running()
    {
        await using var ui = await WithTasksAsync(2);
        await ui.PostAsync(Tasks + "/1/triage");

        var (status, result) = await ui.PostAsync(Tasks + "/2/answer-and-continue", new { text = "Go ahead." });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(result.GetProperty("inPool").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("triageJob").ValueKind);
        Assert.Contains("being triaged", result.GetProperty("note").GetString(), StringComparison.Ordinal);
        var (_, pool) = await ui.GetAsync(Pool);
        Assert.Equal("2", Assert.Single(pool.EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task Save_and_continue_refuses_an_empty_answer_and_changes_nothing()
    {
        await using var ui = await WithTasksAsync();

        var (status, error) = await ui.PostAsync(Tasks + "/1/answer-and-continue", new { text = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("answer", error.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        var (_, pool) = await ui.GetAsync(Pool);
        Assert.Empty(pool.EnumerateArray());
        Assert.Empty(ui.Launcher.Launched);
    }

    [Fact]
    public async Task The_budget_fields_are_saved_and_zero_clears_the_run_limit()
    {
        await using var ui = await WithTasksAsync(0);

        var (saved, view) = await ui.SendAsync(HttpMethod.Put, Pool + "/settings", new { maxAgentRuns = 12, stopAtUsagePercent = 65 });
        Assert.Equal(HttpStatusCode.OK, saved);
        Assert.Equal(12, view.GetProperty("maxAgentRuns").GetInt32());
        Assert.Equal(65, view.GetProperty("stopAtUsagePercent").GetInt32());
        var (_, status) = await ui.GetAsync(Pool + "/status");
        Assert.Equal(12, status.GetProperty("settings").GetProperty("maxAgentRuns").GetInt32());

        var (cleared, none) = await ui.SendAsync(HttpMethod.Put, Pool + "/settings", new { maxAgentRuns = 0 });
        Assert.Equal(HttpStatusCode.OK, cleared);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("maxAgentRuns").ValueKind);
        Assert.Equal(65, none.GetProperty("stopAtUsagePercent").GetInt32());
    }

    [Theory]
    [InlineData("{\"stopAtUsagePercent\":0}")]
    [InlineData("{\"stopAtUsagePercent\":101}")]
    [InlineData("{\"maxAgentRuns\":-3}")]
    public async Task A_budget_value_out_of_range_is_refused(string json)
    {
        await using var ui = await WithTasksAsync(0);

        var (status, _) = await ui.SendAsync(HttpMethod.Put, Pool + "/settings", JsonSerializer.Deserialize<JsonElement>(json));

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task The_queue_shows_tasks_added_after_the_page_first_asked()
    {
        await using var ui = await WithTasksAsync(3);
        var (_, empty) = await ui.GetAsync(Pool);
        Assert.Empty(empty.EnumerateArray());

        await ui.PostAsync(Pool, new { id = "1" });
        await ui.PostAsync(Pool, new { id = "3" });

        var (_, queue) = await ui.GetAsync(Pool + "?fresh=true");
        Assert.Equal(["1", "3"], queue.EnumerateArray().Select(e => e.GetProperty("id").GetString()!).ToArray());
    }
}
