using System.Net;
using System.Text.Json;

namespace Autopilot.AzureDevOps.Tests;

public class PullRequestAndCheckerTests
{
    private static readonly string PrUrl = $"{Ado.Org}/Shop/_apis/git/repositories/{Ado.RepoId}/pullrequests";

    private static AzureDevOpsPullRequests PullRequests(FakeHandler handler) => new(Ado.Client(handler), Ado.Settings());

    private static FakeHandler RepoHandler() => new FakeHandler().On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/git/repositories/shop-api", Ado.RepositoryJson);

    private static readonly DraftPullRequestRequest Request = new("autopilot/42", "main", "[autopilot] T", "Text", 42);

    [Fact]
    public async Task Ensure_draft_creates_a_draft_when_none_is_open()
    {
        var handler = RepoHandler()
            .On(HttpMethod.Get, PrUrl, "{\"count\":0,\"value\":[]}")
            .On(HttpMethod.Post, PrUrl, AzureDevOpsClientTests.PullRequestJson, HttpStatusCode.Created);

        var result = await PullRequests(handler).EnsureDraftAsync(Request, default);

        Assert.True(result.Created);
        Assert.True(result.IsDraft);
        Assert.Equal(22, result.PullRequestId);
        var search = handler.Requests.Single(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath.EndsWith("/pullrequests", StringComparison.Ordinal));
        Assert.Contains("searchCriteria.sourceRefName=refs%2Fheads%2Fautopilot%2F42", search.Uri.Query, StringComparison.Ordinal);
        Assert.Contains("searchCriteria.status=active", search.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_draft_reuses_the_open_pull_request()
    {
        var handler = RepoHandler()
            .On(HttpMethod.Get, PrUrl, $"{{\"count\":1,\"value\":[{AzureDevOpsClientTests.PullRequestJson}]}}");

        var result = await PullRequests(handler).EnsureDraftAsync(Request, default);

        Assert.False(result.Created);
        Assert.Equal(22, result.PullRequestId);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Update_description_only_sends_text_fields_and_skips_when_equal()
    {
        const string Open = "{\"pullRequestId\":22,\"status\":\"active\",\"isDraft\":true,\"title\":\"T\",\"description\":\"Old\",\"repository\":{\"webUrl\":\"" + Ado.Org + "/Shop/_git/shop-api\"}}";
        var handler = RepoHandler()
            .On(HttpMethod.Get, PrUrl + "/22", Open)
            .On(HttpMethod.Patch, PrUrl + "/22", Open.Replace("Old", "New", StringComparison.Ordinal));

        var result = await PullRequests(handler).UpdateDescriptionAsync(22, "New", null, default);
        var same = await PullRequests(handler).UpdateDescriptionAsync(22, "Old", null, default);

        Assert.True(result.IsDraft);
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Get && r.Uri.AbsolutePath.EndsWith("/pullrequests/22", StringComparison.Ordinal)));
        var patch = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Equal(["description"], JsonDocument.Parse(patch.Body).RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(22, same.PullRequestId);
    }

    [Fact]
    public async Task A_closed_pull_request_is_not_updated()
    {
        var handler = RepoHandler().On(HttpMethod.Get, PrUrl + "/22", "{\"pullRequestId\":22,\"status\":\"completed\"}");

        await Assert.ThrowsAsync<AzureDevOpsException>(() => PullRequests(handler).UpdateDescriptionAsync(22, "x", null, default));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task The_module_never_asks_to_make_a_pull_request_ready()
    {
        const string Open = "{\"pullRequestId\":22,\"status\":\"active\",\"isDraft\":true,\"description\":\"Old\"}";
        var handler = RepoHandler()
            .On(HttpMethod.Get, PrUrl + "/22", Open)
            .On(HttpMethod.Patch, PrUrl + "/22", Open)
            .On(HttpMethod.Get, PrUrl, "{\"count\":0,\"value\":[]}")
            .On(HttpMethod.Post, PrUrl, AzureDevOpsClientTests.PullRequestJson, HttpStatusCode.Created);
        var service = PullRequests(handler);

        await service.EnsureDraftAsync(Request, default);
        await service.UpdateDescriptionAsync(22, "New text", "New title", default);

        foreach (var request in handler.Requests.Where(r => r.Body.Length > 0))
        {
            var root = JsonDocument.Parse(request.Body).RootElement;
            Assert.False(root.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.False);
            Assert.False(root.TryGetProperty("status", out _));
            Assert.False(root.TryGetProperty("autoCompleteSetBy", out _));
            Assert.False(root.TryGetProperty("completionOptions", out _));
        }
    }

    // ---------- connection checks ----------

    private static FakeHandler Healthy() => RepoHandler()
        .On(HttpMethod.Get, $"{Ado.Org}/_apis/projects/Shop", Ado.ProjectJson)
        .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson(42))
        .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", "{\"count\":1,\"value\":[{\"id\":42,\"rev\":5,\"fields\":{\"System.Tags\":\"a\",\"System.Title\":\"T\"}}]}")
        .On(HttpMethod.Patch, $"{Ado.Org}/Shop/_apis/wit/workitems/42", "{\"id\":42,\"rev\":5}")
        .On(HttpMethod.Get, $"{Ado.Org}/_apis/permissions/", "{\"count\":1,\"value\":[true]}")
        .On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/git/policy/configurations", AzureDevOpsClientTests.PolicyJson(7, enabled: true, blocking: true, approvers: 1, creatorVoteCounts: false));

    private static AzureDevOpsConnectionChecker Checker(FakeHandler handler, IAzureDevOpsTokenProvider? tokens = null, AzureDevOpsSettings? settings = null) =>
        new(Ado.Client(handler, null, tokens), settings ?? Ado.Settings(), "main");

    [Fact]
    public async Task A_healthy_setup_passes_every_check_and_the_write_check_saves_nothing()
    {
        var handler = Healthy();

        var results = await Checker(handler).CheckAllAsync(default);

        Assert.Equal(
            ["sign-in", "query", "work-item-read", "work-item-write", "git-permissions", "branch-policy"],
            results.Select(r => r.Id));
        Assert.All(results, r => Assert.Equal(CheckOutcome.Ok, r.Outcome));
        Assert.All(results, r => Assert.Null(r.Fix));
        var patch = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.Contains("validateOnly=true", patch.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_token_fails_sign_in_with_a_fix_and_skips_the_rest()
    {
        var results = await Checker(Healthy(), new MissingTokenProvider()).CheckAllAsync(default);

        Assert.Equal(CheckOutcome.Fail, results[0].Outcome);
        Assert.Contains("AUTOPILOT_ADO_PAT", results[0].Fix, StringComparison.Ordinal);
        Assert.All(results.Skip(1), r => Assert.Equal(CheckOutcome.Skipped, r.Outcome));
    }

    [Fact]
    public async Task The_sign_in_fix_depends_on_the_auth_method()
    {
        var handler = new FakeHandler().On(HttpMethod.Get, $"{Ado.Org}/_apis/projects/Shop", "{\"message\":\"bad\"}", HttpStatusCode.Unauthorized);
        var settings = new AzureDevOpsSettings { OrganizationUrl = new Uri(Ado.Org), Project = Ado.Project, Auth = AzureDevOpsAuth.ServicePrincipal };

        var result = await Checker(handler, settings: settings).CheckSignInAsync(default);

        Assert.Equal(CheckOutcome.Fail, result.Outcome);
        Assert.Contains("AUTOPILOT_ADO_CLIENT_SECRET", result.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain(Ado.Token, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_query_fails_with_the_server_message_and_a_hint()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", "{\"message\":\"VS403: unknown field\"}", HttpStatusCode.BadRequest);

        var result = await Checker(handler).CheckQueryAsync("SELECT nonsense", default);

        Assert.Equal(CheckOutcome.Fail, result.Outcome);
        Assert.Contains("unknown field", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Fix);
    }

    [Fact]
    public async Task An_empty_query_result_is_a_warning_not_a_failure()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson());

        var result = await Checker(handler).CheckQueryAsync(null, default);

        Assert.Equal(CheckOutcome.Warn, result.Outcome);
        Assert.Contains("autopilot", result.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_write_fails_with_the_scope_to_add()
    {
        var handler = Healthy();
        var forbidden = new FakeHandler()
            .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson(42))
            .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", "{\"count\":1,\"value\":[{\"id\":42,\"rev\":5,\"fields\":{}}]}")
            .On(HttpMethod.Patch, $"{Ado.Org}/Shop/_apis/wit/workitems/42", "{\"message\":\"no\"}", HttpStatusCode.Forbidden);

        var result = await Checker(forbidden).CheckWorkItemWriteAsync(default);

        Assert.Equal(CheckOutcome.Fail, result.Outcome);
        Assert.Contains("Work Items: Read & write", result.Fix, StringComparison.Ordinal);
        Assert.NotNull(handler);
    }

    [Fact]
    public async Task Missing_git_rights_are_named()
    {
        var handler = RepoHandler()
            .On(HttpMethod.Get, $"{Ado.Org}/_apis/permissions/{GitRepositoryPermissions.NamespaceId}/{GitRepositoryPermissions.GenericContribute}?", "{\"count\":1,\"value\":[true]}")
            .On(HttpMethod.Get, $"{Ado.Org}/_apis/permissions/{GitRepositoryPermissions.NamespaceId}/{GitRepositoryPermissions.CreateBranch}?", "{\"count\":1,\"value\":[false]}")
            .On(HttpMethod.Get, $"{Ado.Org}/_apis/permissions/{GitRepositoryPermissions.NamespaceId}/{GitRepositoryPermissions.PullRequestContribute}?", "{\"count\":1,\"value\":[true]}");

        var result = await Checker(handler).CheckGitPermissionsAsync(default);

        Assert.Equal(CheckOutcome.Fail, result.Outcome);
        Assert.Contains("Create branch", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Contribute,", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Branch_policy_summary_lists_policies_in_words()
    {
        var summary = await Checker(Healthy()).GetBranchPolicySummaryAsync(default);

        Assert.True(summary.RequiresHumanApproval);
        Assert.Equal("refs/heads/main", summary.Branch);
        var policy = Assert.Single(summary.Policies);
        Assert.True(policy.Required);
        Assert.Contains("1 reviewer(s)", policy.Detail, StringComparison.Ordinal);
        Assert.Contains("needs a person's approval", summary.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_reviewer_policy_the_summary_says_so()
    {
        var handler = RepoHandler().On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/git/policy/configurations", "{\"count\":0,\"value\":[]}");

        var summary = await Checker(handler).GetBranchPolicySummaryAsync(default);

        Assert.False(summary.RequiresHumanApproval);
        Assert.Contains("No branch policy applies", summary.Summary, StringComparison.Ordinal);
    }

    private sealed class MissingTokenProvider : IAzureDevOpsTokenProvider
    {
        public Task<AzureDevOpsCredential> GetCredentialAsync(CancellationToken ct) =>
            throw new AzureDevOpsAuthException("The environment variable AUTOPILOT_ADO_PAT is not set.");

        public string Describe() => "AUTOPILOT_ADO_PAT";
    }
}
