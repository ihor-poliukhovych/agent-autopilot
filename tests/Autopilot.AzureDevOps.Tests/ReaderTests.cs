using System.Text.Json;

namespace Autopilot.AzureDevOps.Tests;

public class ReaderTests
{
    private static string Item(int id, string title = "Item", string state = "Active", string tags = "autopilot; backend") =>
        "{\"id\":" + id + ",\"rev\":3,\"fields\":{\"System.Id\":" + id + ",\"System.Title\":\"" + title + " " + id + "\",\"System.WorkItemType\":\"Bug\",\"System.State\":\"" + state + "\",\"System.Tags\":\"" + tags + "\",\"System.Description\":\"<p>Body</p>\",\"System.AssignedTo\":{\"displayName\":\"Sam Lee\",\"uniqueName\":\"sam@contoso.example\"}}}";

    private static AzureDevOpsReader Reader(FakeHandler handler) => new(Ado.Client(handler), Ado.Settings());

    [Fact]
    public async Task Preview_returns_one_page_and_says_there_is_more()
    {
        var handler = new FakeHandler()
            .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson(1, 2, 3, 4, 5))
            .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", $"{{\"count\":2,\"value\":[{Item(3)},{Item(4)}]}}");

        var page = await Reader(handler).PreviewAsync(new WorkItemPreviewRequest(Skip: 2, Take: 2), default);

        Assert.Equal(5, page.Total);
        Assert.True(page.HasMore);
        Assert.False(page.Truncated);
        Assert.Equal([3, 4], page.Items.Select(i => i.Id));
        var first = page.Items[0];
        Assert.Equal(("Item 3", "Bug", "Active", "Sam Lee"), (first.Title, first.Type, first.State, first.AssignedTo));
        Assert.Equal(["autopilot", "backend"], first.Tags);
        Assert.Equal($"{Ado.Org}/Shop/_workitems/edit/3", first.Url);
        var batch = handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("workitemsbatch", StringComparison.Ordinal));
        Assert.Equal([3, 4], JsonDocument.Parse(batch.Body).RootElement.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public async Task Preview_uses_the_given_query_and_caps_the_total()
    {
        var many = Enumerable.Range(1, WorkItemPreviewRequest.MaxTotal + 1).ToArray();
        var handler = new FakeHandler()
            .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson(many))
            .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", "{\"count\":0,\"value\":[]}");

        var page = await Reader(handler).PreviewAsync(new WorkItemPreviewRequest("SELECT [System.Id] FROM WorkItems", 0, 1000), default);

        Assert.True(page.Truncated);
        Assert.Equal(WorkItemPreviewRequest.MaxTotal, page.Total);
        Assert.Equal(WorkItemPreviewRequest.MaxTake, page.Take);
        var wiql = handler.Requests.First();
        Assert.Contains($"$top={WorkItemPreviewRequest.MaxTotal + 1}", wiql.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("SELECT [System.Id] FROM WorkItems", wiql.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_of_an_empty_result_does_not_read_items()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson());

        var page = await Reader(handler).PreviewAsync(new WorkItemPreviewRequest(), default);

        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Preview_refuses_an_empty_or_oversized_query()
    {
        var reader = Reader(new FakeHandler());

        await Assert.ThrowsAsync<ArgumentException>(() => reader.PreviewAsync(new WorkItemPreviewRequest("  "), default));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.PreviewAsync(new WorkItemPreviewRequest(new string('x', WorkItemPreviewRequest.MaxQueryLength + 1)), default));
    }

    [Fact]
    public async Task Load_returns_fields_comments_and_links()
    {
        var handler = new FakeHandler()
            .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", $"{{\"count\":1,\"value\":[{Item(42)}]}}")
            .On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/wit/workItems/42/comments", """
                {"count":2,"comments":[
                  {"id":1,"text":"<p>Please look at <b>this</b></p>","createdBy":{"displayName":"Pat"},"createdDate":"2026-11-20T10:00:00Z"},
                  {"id":2,"text":"<p><b>Written by Autopilot</b>: triage</p>","createdBy":{"displayName":"Agent"},"createdDate":"2026-11-21T10:00:00Z"}]}
                """)
            .On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/wit/workitems/42", """
                {"id":42,"rev":3,"relations":[
                  {"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://dev.azure.com/contoso/_apis/wit/workItems/7","attributes":{"name":"Parent"}},
                  {"rel":"System.LinkTypes.Related","url":"https://dev.azure.com/contoso/_apis/wit/workItems/9","attributes":{"name":"Related"}},
                  {"rel":"ArtifactLink","url":"vstfs:///Git/PullRequestId/p%2Fr%2F5","attributes":{"name":"Pull Request"}}]}
                """);

        var detail = await Reader(handler).LoadAsync(42, default);

        Assert.NotNull(detail);
        Assert.Equal(3, detail.Revision);
        Assert.Equal("Bug", detail.Type);
        Assert.Equal("Item 42", detail.Item.Title);
        Assert.Equal("Active", detail.Fields["System.State"]);
        Assert.Equal("Sam Lee", detail.Fields["System.AssignedTo"]);
        Assert.Equal(["Please look at this", "Written by Autopilot: triage"], detail.Comments.Select(c => c.Text));
        Assert.Equal([false, true], detail.Comments.Select(c => c.WrittenByAutopilot));
        Assert.Equal([LinkKind.Parent, LinkKind.Related, LinkKind.PullRequest], detail.Links.Select(l => l.Kind));
        Assert.Equal([7, 9, (int?)null], detail.Links.Select(l => l.WorkItemId));
    }

    [Fact]
    public async Task Load_of_a_missing_item_is_null()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", "{\"count\":0,\"value\":[]}");

        Assert.Null(await Reader(handler).LoadAsync(1, default));
    }

    [Fact]
    public async Task Comment_paging_follows_the_continuation_token_up_to_the_limit()
    {
        var handler = new FakeHandler().OnSequence(
            HttpMethod.Get,
            $"{Ado.Org}/Shop/_apis/wit/workItems/42/comments",
            () => FakeHandler.JsonResponse("{\"comments\":[{\"id\":1,\"text\":\"a\"}],\"continuationToken\":\"next\"}"),
            () => FakeHandler.JsonResponse("{\"comments\":[{\"id\":2,\"text\":\"b\"}]}"));

        var comments = await Ado.Client(handler).GetCommentsAsync(42, 10, default);

        Assert.Equal([1, 2], comments.Select(c => c.Id));
        Assert.Contains("continuationToken=next", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }
}
