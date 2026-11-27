using Autopilot.Core;
using Autopilot.Core.Local;
using Autopilot.Core.Pool;
using Autopilot.Core.Tasks;

namespace Autopilot.Web.Tests;

/// <summary>The Inbox list: needs-human questions and parked tasks together, oldest first.</summary>
public sealed class InboxApiTests
{
    private const string Inbox = "/api/projects/demo/inbox";

    [Fact]
    public async Task An_empty_project_returns_an_empty_list()
    {
        await using var ui = new UiFixture();
        await ui.CreateProjectAsync();

        var (_, items) = await ui.GetAsync(Inbox);

        Assert.Empty(items.EnumerateArray());
    }

    [Fact]
    public async Task Questions_and_parked_tasks_come_in_one_list_oldest_first()
    {
        await using var ui = new UiFixture();
        await ui.CreateProjectAsync();
        for (var i = 1; i <= 3; i++)
        {
            await ui.PostAsync("/api/projects/demo/tasks", new { title = "Task " + i, body = "Do it." });
        }

        var item = LocalTaskWorkItemSource.ToItem(new LocalTaskService(ui.Workspace, "demo").Get(1)!);
        await new TriageStore(ui.RepoFolder, ui.Workspace.ProjectFolder("demo"))
            .SaveAsync(item, new TriageResult(TriageLabel.NeedsHuman, "Which order? (A) by name, (B) by date.", [], RiskLevel.Low, ["by name", "by date"], 0), CancellationToken.None);
        var state = new PoolStateStore(ui.Workspace, "demo");
        state.Park("3", "paid", "It costs money.", DateTimeOffset.UtcNow.AddHours(1));
        state.Park("2", "other", "Gave up after two tries.", DateTimeOffset.UtcNow.AddHours(-1));

        var (_, items) = await ui.GetAsync(Inbox);

        var list = items.EnumerateArray().ToArray();
        Assert.Equal(["parked", "question", "parked"], list.Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal(["2", "1", "3"], list.Select(e => e.GetProperty("id").GetString()));
        Assert.Equal("Task 2", list[0].GetProperty("title").GetString());
        Assert.Equal("Gave up after two tries.", list[0].GetProperty("parkReason").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, list[1].GetProperty("parkReason").ValueKind);
        Assert.Equal(["by name", "by date"], list[1].GetProperty("question").GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        Assert.All(list, e => Assert.NotEqual(default, e.GetProperty("createdAt").GetDateTimeOffset()));
        Assert.True(list[0].GetProperty("createdAt").GetDateTimeOffset() < list[1].GetProperty("createdAt").GetDateTimeOffset());
        Assert.True(list[1].GetProperty("createdAt").GetDateTimeOffset() < list[2].GetProperty("createdAt").GetDateTimeOffset());
    }
}
