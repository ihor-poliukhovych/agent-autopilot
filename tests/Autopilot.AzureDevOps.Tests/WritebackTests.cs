using System.Net;
using System.Text.Json;
using Autopilot.Core;

namespace Autopilot.AzureDevOps.Tests;

public class WritebackTests
{
    private const string ItemUrl = Ado.Org + "/Shop/_apis/wit/workitems/42";
    private const string CommentsUrl = Ado.Org + "/Shop/_apis/wit/workItems/42/comments";

    private static string Batch(string state = "New", string tags = "autopilot", string assigned = "", int rev = 5) =>
        "{\"count\":1,\"value\":[{\"id\":42,\"rev\":" + rev + ",\"fields\":{\"System.Id\":42,\"System.Title\":\"T\",\"System.State\":\"" + state
        + "\",\"System.Tags\":\"" + tags + "\",\"System.WorkItemType\":\"Bug\"" + assigned + "}}]}";

    private static FakeHandler Handler(string batch, string commentsJson = "{\"comments\":[]}") => new FakeHandler()
        .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", batch)
        .On(HttpMethod.Patch, ItemUrl, "{\"id\":42,\"rev\":6}")
        .On(HttpMethod.Get, CommentsUrl, commentsJson)
        .On(HttpMethod.Post, CommentsUrl, "{\"id\":9,\"text\":\"x\"}");

    private static AzureDevOpsWriteback Writeback(FakeHandler handler, params (WorkflowEvent Event, string State)[] map) =>
        new(Ado.Client(handler), new AzureDevOpsSettings
        {
            OrganizationUrl = new Uri(Ado.Org),
            Project = Ado.Project,
            Repository = Ado.Repo,
            StateMap = map.ToDictionary(m => m.Event, m => m.State),
        });

    private static readonly TriageResult Ready = new(TriageLabel.AgentReady, "Small.", ["It returns 404"], RiskLevel.Low);

    private static JsonElement[] Patch(FakeHandler handler) =>
        JsonDocument.Parse(handler.Requests.Single(r => r.Method == HttpMethod.Patch).Body).RootElement.EnumerateArray().ToArray();

    [Fact]
    public async Task Triage_sets_tag_and_state_in_one_patch_and_posts_one_comment()
    {
        var handler = Handler(Batch());

        var result = await Writeback(handler, (WorkflowEvent.AgentReady, "Active")).RecordTriageAsync(42, Ready, default);

        var ops = Patch(handler);
        Assert.Equal("test", ops[0].GetProperty("op").GetString());
        Assert.Equal(5, ops[0].GetProperty("value").GetInt32());
        Assert.Equal("autopilot; autopilot:agent-ready", ops.Single(o => o.GetProperty("path").GetString() == "/fields/System.Tags").GetProperty("value").GetString());
        Assert.Equal("Active", ops.Single(o => o.GetProperty("path").GetString() == "/fields/System.State").GetProperty("value").GetString());
        Assert.Equal(["triage tag", "state to \"Active\"", "triage comment"], result.Changed);
        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Running_triage_again_changes_nothing()
    {
        var first = Handler(Batch());
        await Writeback(first, (WorkflowEvent.AgentReady, "Active")).RecordTriageAsync(42, Ready, default);
        var postedComment = JsonDocument.Parse(first.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)).Body)
            .RootElement.GetProperty("text").GetString()!;

        // The item and its comments now look as the first run left them.
        var second = Handler(Batch("Active", "autopilot; autopilot:agent-ready"), JsonSerializer.Serialize(new { comments = new[] { new { id = 9, text = postedComment } } }));
        var result = await Writeback(second, (WorkflowEvent.AgentReady, "Active")).RecordTriageAsync(42, Ready, default);

        Assert.False(result.ChangedAnything);
        Assert.DoesNotContain(second.Requests, r => r.Method == HttpMethod.Patch || (r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Finished_items_keep_their_state()
    {
        var handler = Handler(Batch("Done"));

        var result = await Writeback(handler, (WorkflowEvent.AgentReady, "Active")).TransitionAsync(42, WorkflowEvent.AgentReady, default);

        Assert.False(result.ChangedAnything);
        Assert.Contains("finished", Assert.Single(result.Unchanged), StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task Unmapped_events_change_no_state()
    {
        var handler = Handler(Batch());

        var result = await Writeback(handler).TransitionAsync(42, WorkflowEvent.DraftPullRequest, default);

        Assert.Contains("no state is mapped", Assert.Single(result.Unchanged), StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Add_tag_is_idempotent_and_keeps_other_tags()
    {
        var handler = Handler(Batch(tags: "a; b"));
        var writeback = Writeback(handler);

        var added = await writeback.AddTagAsync(42, "c", default);
        var again = await writeback.AddTagAsync(42, "B", default);

        Assert.True(added.ChangedAnything);
        Assert.False(again.ChangedAnything);
        Assert.Equal("a; b; c", Patch(handler).Single(o => o.GetProperty("op").GetString() == "add").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Same_comment_is_posted_once_and_different_comment_is_posted()
    {
        var comment = AzureDevOpsWriteback.RunSummaryComment("run-1", "passed", ["Gate passed"], "https://example.test/pr/1");
        var first = Handler(Batch());
        await Writeback(first).PostCommentAsync(42, comment, default);
        var text = JsonDocument.Parse(first.Requests.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)).Body)
            .RootElement.GetProperty("text").GetString()!;
        Assert.Contains("ref:run-summary:run-1", text, StringComparison.Ordinal);

        var existing = JsonSerializer.Serialize(new { comments = new[] { new { id = 1, text } } });
        var second = Handler(Batch(), existing);
        var repeated = await Writeback(second).PostCommentAsync(42, comment, default);
        var other = await Writeback(second).PostCommentAsync(42, AzureDevOpsWriteback.RunSummaryComment("run-2", "failed", [], null), default);

        Assert.False(repeated.ChangedAnything);
        Assert.True(other.ChangedAnything);
        Assert.Single(second.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public void Needs_human_comment_lists_the_questions_encoded()
    {
        var comment = AzureDevOpsWriteback.NeedsHumanComment(["Which <currency>?", "Is X in scope?"]);

        Assert.Equal(CommentKind.NeedsHuman, comment.Kind);
        Assert.Contains("<li>Which &lt;currency&gt;?</li><li>Is X in scope?</li>", comment.BodyHtml, StringComparison.Ordinal);
        Assert.Contains(AzureDevOpsWriteback.CommentMarker, comment.BodyHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assign_sets_the_assignee_once()
    {
        var assigned = ",\"System.AssignedTo\":{\"displayName\":\"Sam Lee\",\"uniqueName\":\"sam@contoso.example\"}";

        var set = Handler(Batch());
        var changed = await Writeback(set).AssignAsync(42, "sam@contoso.example", default);
        var same = Handler(Batch(assigned: assigned));
        var unchanged = await Writeback(same).AssignAsync(42, "sam@contoso.example", default);
        var cleared = Handler(Batch(assigned: assigned));
        await Writeback(cleared).AssignAsync(42, null, default);

        Assert.True(changed.ChangedAnything);
        Assert.Equal("sam@contoso.example", Patch(set).Single(o => o.GetProperty("path").GetString() == "/fields/System.AssignedTo").GetProperty("value").GetString());
        Assert.False(unchanged.ChangedAnything);
        Assert.Equal(string.Empty, Patch(cleared).Single(o => o.GetProperty("path").GetString() == "/fields/System.AssignedTo").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Link_pull_request_adds_an_artifact_link_once()
    {
        var empty = new FakeHandler()
            .On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/git/repositories/{Ado.RepoId}", Ado.RepositoryJson)
            .On(HttpMethod.Get, ItemUrl, "{\"id\":42,\"rev\":5}")
            .On(HttpMethod.Patch, ItemUrl, "{\"id\":42,\"rev\":6}");
        var result = await Writeback(empty).LinkPullRequestAsync(42, Ado.RepoId, 22, default);

        var op = Patch(empty).Single();
        Assert.Equal("/relations/-", op.GetProperty("path").GetString());
        Assert.Equal("ArtifactLink", op.GetProperty("value").GetProperty("rel").GetString());
        Assert.Equal($"vstfs:///Git/PullRequestId/{Ado.ProjectId}%2F{Ado.RepoId}%2F22", op.GetProperty("value").GetProperty("url").GetString());
        Assert.True(result.ChangedAnything);

        var linked = new FakeHandler()
            .On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/git/repositories/{Ado.RepoId}", Ado.RepositoryJson)
            .On(HttpMethod.Get, ItemUrl, $"{{\"id\":42,\"relations\":[{{\"rel\":\"ArtifactLink\",\"url\":\"vstfs:///Git/PullRequestId/{Ado.ProjectId}%2F{Ado.RepoId}%2F22\"}}]}}");
        var again = await Writeback(linked).LinkPullRequestAsync(42, Ado.RepoId, 22, default);

        Assert.False(again.ChangedAnything);
        Assert.DoesNotContain(linked.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task A_lost_revision_race_reads_again_and_retries_once()
    {
        var handler = new FakeHandler()
            .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", Batch())
            .OnSequence(HttpMethod.Patch, ItemUrl,
                () => FakeHandler.JsonResponse("{\"message\":\"rev changed\"}", HttpStatusCode.PreconditionFailed),
                () => FakeHandler.JsonResponse("{\"id\":42,\"rev\":7}"));

        var result = await Writeback(handler).AddTagAsync(42, "x", default);

        Assert.True(result.ChangedAnything);
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Patch));
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public void State_map_and_max_items_are_read_from_the_settings_and_checked()
    {
        var settings = AzureDevOpsSettings.FromModule(Ado.Module(
            ("organizationUrl", Ado.Org), ("project", "Shop"), ("maxItems", "120"), ("stateMap", "agent-ready=Active; draft-pr = Resolved")), false);

        Assert.Equal(120, settings.MaxItems);
        Assert.Equal("Active", settings.StateMap[WorkflowEvent.AgentReady]);
        Assert.Equal("Resolved", settings.StateMap[WorkflowEvent.DraftPullRequest]);

        var bad = Assert.Throws<AzureDevOpsSettingsException>(() => AzureDevOpsSettings.FromModule(Ado.Module(
            ("organizationUrl", Ado.Org), ("project", "Shop"), ("maxItems", "0"), ("stateMap", "later=Done")), false));
        Assert.Equal(2, bad.Problems.Count);
    }
}
