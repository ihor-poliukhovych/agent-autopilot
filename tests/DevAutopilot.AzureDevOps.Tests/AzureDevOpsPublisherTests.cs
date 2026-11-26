using System.Net;
using System.Text.Json;
using DevAutopilot.Core.Gate;

namespace DevAutopilot.AzureDevOps.Tests;

public class AzureDevOpsPublisherTests
{
    private static FakeHandler Handler(HttpStatusCode prStatus = HttpStatusCode.Created, string? prJson = null) => new FakeHandler()
        .On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/git/repositories/shop-api", Ado.RepositoryJson)
        .On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/git/repositories/{Ado.RepoId}/pullrequests", "{\"count\":0,\"value\":[]}")
        .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/git/repositories/{Ado.RepoId}/pullrequests", prJson ?? AzureDevOpsClientTests.PullRequestJson, prStatus);

    private static AzureDevOpsPublisher Publisher(FakeHandler handler, RecordingPusher pusher) =>
        new(Ado.Client(handler), Ado.Settings(), "C:/work/shop", "main", "autopilot/", pusher);

    [Fact]
    public async Task Pushes_the_branch_then_opens_a_draft_pull_request()
    {
        var handler = Handler();
        var pusher = new RecordingPusher();

        var result = await Publisher(handler, pusher).PublishAsync(Ado.Request(), default);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal($"{Ado.Org}/Shop/_git/shop-api/pullrequest/22", result.Location);
        var push = Assert.Single(pusher.Pushes);
        Assert.Equal(("C:/work/shop", "origin", "autopilot/42"), (push.Path, push.Remote, push.Branch));

        var pr = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        var body = JsonDocument.Parse(pr.Body).RootElement;
        Assert.True(body.GetProperty("isDraft").GetBoolean());
        Assert.Equal("refs/heads/autopilot/42", body.GetProperty("sourceRefName").GetString());
        Assert.Equal("refs/heads/main", body.GetProperty("targetRefName").GetString());
        Assert.Equal("[autopilot] Return 404 for unknown products", body.GetProperty("title").GetString());
        Assert.Equal("42", body.GetProperty("workItemRefs")[0].GetProperty("id").GetString());
        Assert.False(body.TryGetProperty("autoCompleteSetBy", out _));
        Assert.False(body.TryGetProperty("completionOptions", out _));

        var description = body.GetProperty("description").GetString()!;
        Assert.Contains("# Return 404 for unknown products", description, StringComparison.Ordinal);
        Assert.Contains("waiting for a person", description, StringComparison.Ordinal);
        Assert.Contains("ProductsTests.Unknown_id_returns_404", description, StringComparison.Ordinal);
        Assert.DoesNotContain("change.patch", description, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch || r.Uri.AbsolutePath.Contains("/reviewers", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("feature/x")]
    [InlineData("autopilot/")]
    [InlineData("autopilot/42:main")]
    [InlineData("autopilot/../main")]
    [InlineData("autopilot/a b")]
    [InlineData("+autopilot/42")]
    public async Task Refuses_branches_that_are_not_agent_branches(string branch)
    {
        var handler = Handler();
        var pusher = new RecordingPusher();

        var result = await Publisher(handler, pusher).PublishAsync(Ado.Request(branch), default);

        Assert.False(result.Succeeded);
        Assert.Contains("Refusing to push", result.Error, StringComparison.Ordinal);
        Assert.Empty(pusher.Pushes);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Refuses_a_change_the_gate_rejected()
    {
        var handler = Handler();
        var pusher = new RecordingPusher();

        var result = await Publisher(handler, pusher).PublishAsync(Ado.Request(outcome: GateOutcome.Rejected), default);

        Assert.False(result.Succeeded);
        Assert.Empty(pusher.Pushes);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Pull_request_conflict_is_reported_without_the_token()
    {
        var handler = Handler(HttpStatusCode.Conflict, "{\"message\":\"TF401179: An active pull request for the source and target branch already exists.\"}");

        var result = await Publisher(handler, new RecordingPusher()).PublishAsync(Ado.Request(), default);

        Assert.False(result.Succeeded);
        Assert.Contains("TF401179", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(Ado.Token, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_failure_stops_before_the_pull_request()
    {
        var handler = Handler();
        var pusher = new RecordingPusher { Throw = new AzureDevOpsPublishException("git push failed") };

        var result = await Publisher(handler, pusher).PublishAsync(Ado.Request(), default);

        Assert.False(result.Succeeded);
        Assert.Equal("git push failed", result.Error);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public void Long_descriptions_are_cut_with_a_note()
    {
        var cut = AzureDevOpsPublisher.Cut(new string('x', 10_000));

        Assert.Equal(AzureDevOpsPublisher.MaxDescriptionLength, cut.Length);
        Assert.EndsWith("local run folder.)", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_is_one_line_with_the_prefix() =>
        Assert.Equal("[autopilot] Fix the cart total", AzureDevOpsPublisher.Title("Fix the\r\ncart total"));

    [Fact]
    public void Publisher_needs_a_repository() =>
        Assert.Throws<AzureDevOpsSettingsException>(() =>
            new AzureDevOpsPublisher(Ado.Client(new FakeHandler()), Ado.Settings(repository: null), "C:/work", "main", "autopilot/"));
}
