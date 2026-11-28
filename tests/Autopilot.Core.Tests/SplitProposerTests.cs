using System.Text.Json;
using Autopilot.Core.Pool;
using Autopilot.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Tests;

public sealed class SplitProposerTests : IDisposable
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

    private sealed class ScriptedRunner(Func<AgentRunResult> respond) : IAgentRunner
    {
        public List<AgentRunRequest> Requests { get; } = [];

        public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond());
        }
    }

    private static AgentRunResult Ok(string? json) => new(
        true, "done", json is null ? null : JsonDocument.Parse(json).RootElement.Clone(), 1, TokenUsage.Zero, TimeSpan.Zero, "m", null);

    private SplitProposer Proposer(ScriptedRunner runner) => new(runner, _temp, Path.Combine(_temp, "runs"));

    [Fact]
    public async Task Valid_list_is_parsed_in_the_agents_order_with_a_schema_request()
    {
        var runner = new ScriptedRunner(() => Ok("""
            {"children":[
              {"title":"First","body":"Do 1","labels":["a"],"paths":["src/A/**"]},
              {"title":"Second","body":"Do 2","labels":[],"paths":[]}]}
            """));

        var result = await Proposer(runner).ProposeAsync("Big", "Body");

        Assert.True(result.Success);
        Assert.Equal(["First", "Second"], result.Proposals.Select(p => p.Title));
        Assert.Equal(["a"], result.Proposals[0].Labels);
        Assert.Equal(["src/A/**"], result.Proposals[0].Paths);
        var request = Assert.Single(runner.Requests);
        Assert.NotNull(request.OutputJsonSchema);
        Assert.Contains("Big", request.Prompt);
    }

    [Fact]
    public async Task Missing_optional_fields_become_empty()
    {
        var runner = new ScriptedRunner(() => Ok("""{"children":[{"title":"Only title"}]}"""));

        var result = await Proposer(runner).ProposeAsync("Big", "");

        Assert.True(result.Success);
        var p = Assert.Single(result.Proposals);
        Assert.Equal(string.Empty, p.Body);
        Assert.Empty(p.Labels);
        Assert.Empty(p.Paths);
    }

    [Fact]
    public async Task Failed_run_empty_output_malformed_output_zero_proposals_and_blank_title_fail_without_throwing()
    {
        var failed = await Proposer(new ScriptedRunner(() => new AgentRunResult(false, "", null, 0, TokenUsage.Zero, TimeSpan.Zero, null, "boom"))).ProposeAsync("t", "b");
        Assert.False(failed.Success);
        Assert.Contains("boom", failed.Error);

        var empty = await Proposer(new ScriptedRunner(() => Ok(null))).ProposeAsync("t", "b");
        Assert.False(empty.Success);

        var malformed = await Proposer(new ScriptedRunner(() => Ok("""{"children":"nope"}"""))).ProposeAsync("t", "b");
        Assert.False(malformed.Success);
        var notObject = await Proposer(new ScriptedRunner(() => Ok("[1,2]"))).ProposeAsync("t", "b");
        Assert.False(notObject.Success);
        var badItem = await Proposer(new ScriptedRunner(() => Ok("""{"children":[3]}"""))).ProposeAsync("t", "b");
        Assert.False(badItem.Success);

        var none = await Proposer(new ScriptedRunner(() => Ok("""{"children":[]}"""))).ProposeAsync("t", "b");
        Assert.False(none.Success);

        var blank = await Proposer(new ScriptedRunner(() => Ok("""{"children":[{"title":"ok"},{"title":"  "}]}"""))).ProposeAsync("t", "b");
        Assert.False(blank.Success);
        Assert.Empty(blank.Proposals);

        var thrown = await Proposer(new ScriptedRunner(() => throw new InvalidOperationException("crash"))).ProposeAsync("t", "b");
        Assert.False(thrown.Success);
        Assert.Contains("crash", thrown.Error);
    }

    [Fact]
    public async Task Parsed_proposals_feed_TaskSplit_which_creates_and_queues_children_in_order()
    {
        var workspace = new Workspace.Workspace(Path.Combine(_temp, "ws"));
        var tasks = new LocalTaskService(workspace, "demo");
        var pool = new PoolService(workspace, "demo");
        var original = tasks.Add("Big task");
        var runner = new ScriptedRunner(() => Ok("""
            {"children":[{"title":"One","paths":["src/A/**"]},{"title":"Two","body":"b"},{"title":"Three"}]}
            """));

        var proposed = await Proposer(runner).ProposeAsync(original.Title, original.Body);
        var result = new TaskSplit(tasks, pool).Split(original.Number, proposed.Proposals);

        Assert.True(result.Success);
        Assert.Equal(["One", "Two", "Three"], result.Children.Select(c => c.Title));
        Assert.Equal(result.Children.Select(c => c.Id), pool.List().Select(e => e.WorkItemId));
        Assert.All(result.Children, c => Assert.Equal(original.Number, c.ParentNumber));
    }
}
