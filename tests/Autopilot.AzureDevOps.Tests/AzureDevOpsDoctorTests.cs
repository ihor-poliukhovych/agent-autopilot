using System.Net;

namespace Autopilot.AzureDevOps.Tests;

public class AzureDevOpsDoctorTests
{
    private static readonly int[] Required = [GitRepositoryPermissions.GenericContribute, GitRepositoryPermissions.CreateBranch, GitRepositoryPermissions.PullRequestContribute];

    /// <summary>A healthy setup; <paramref name="granted"/> overrides which bits the caller has.</summary>
    private static FakeHandler Healthy(ISet<int>? granted = null, string? policyJson = null)
    {
        granted ??= new HashSet<int>(Required);
        var handler = new FakeHandler()
            .On(HttpMethod.Get, $"{Ado.Org}/_apis/projects/Shop", Ado.ProjectJson)
            .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson(3, 2, 1))
            .On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/git/repositories/shop-api", Ado.RepositoryJson)
            .On(HttpMethod.Get, $"{Ado.Org}/Shop/_apis/git/policy/configurations",
                policyJson ?? AzureDevOpsClientTests.PolicyJson(7, enabled: true, blocking: true, approvers: 1, creatorVoteCounts: false));
        foreach (var bit in AllBits)
        {
            handler.On(HttpMethod.Get, $"{Ado.Org}/_apis/permissions/{GitRepositoryPermissions.NamespaceId}/{bit}?",
                $"{{\"count\":1,\"value\":[{(granted.Contains(bit) ? "true" : "false")}]}}");
        }

        return handler;
    }

    private static IEnumerable<int> AllBits =>
        AzureDevOpsDoctor.RequiredPermissions.Select(p => p.Bit).Concat(AzureDevOpsDoctor.ForbiddenPermissions.Select(p => p.Bit));

    private static Task<IReadOnlyList<AzureDevOpsCheck>> Run(FakeHandler handler, IAzureDevOpsTokenProvider? tokens = null, string? repository = Ado.Repo) =>
        new AzureDevOpsDoctor(Ado.Client(handler, [], tokens), Ado.Settings(repository), "main").RunAsync(default);

    [Fact]
    public async Task Healthy_setup_passes_every_check()
    {
        var handler = Healthy();

        var checks = await Run(handler);

        Assert.All(checks, c => Assert.Equal(AzureDevOpsCheckStatus.Ok, c.Status));
        Assert.Contains(checks, c => c.Name == "Work item query" && c.Detail == "3 item(s) match (default query, tag \"autopilot\")");
        Assert.Equal(4 + AzureDevOpsDoctor.RequiredPermissions.Count + AzureDevOpsDoctor.ForbiddenPermissions.Count + 1, checks.Count);
        var permissionRequest = handler.Requests.First(r => r.Uri.AbsolutePath.StartsWith("/contoso/_apis/permissions/", StringComparison.Ordinal));
        Assert.Contains($"tokens=repoV2%2F{Ado.ProjectId}%2F{Ado.RepoId}", permissionRequest.Uri.Query, StringComparison.Ordinal);
        var policyRequest = handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/policy/configurations", StringComparison.Ordinal));
        Assert.Contains("refName=refs%2Fheads%2Fmain", policyRequest.Uri.Query, StringComparison.Ordinal);
        Assert.Contains($"policyType={PolicyTypes.MinimumReviewers}", policyRequest.Uri.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GitRepositoryPermissions.ForcePush, "ForcePush")]
    [InlineData(GitRepositoryPermissions.EditPolicies, "EditPolicies")]
    [InlineData(GitRepositoryPermissions.ManagePermissions, "ManagePermissions")]
    [InlineData(GitRepositoryPermissions.PolicyExempt, "PolicyExempt")]
    [InlineData(GitRepositoryPermissions.PullRequestBypassPolicy, "PullRequestBypassPolicy")]
    public async Task Each_forbidden_right_fails_with_a_fix(int bit, string name)
    {
        var granted = new HashSet<int>(Required) { bit };

        var checks = await Run(Healthy(granted));

        var failed = Assert.Single(checks, c => c.Status == AzureDevOpsCheckStatus.Fail);
        Assert.Contains(name, failed.Name, StringComparison.Ordinal);
        Assert.Contains("Deny", failed.Fix, StringComparison.Ordinal);
        Assert.Contains("shop-api > Security", failed.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_contribute_right_fails()
    {
        var granted = new HashSet<int> { GitRepositoryPermissions.CreateBranch, GitRepositoryPermissions.PullRequestContribute };

        var checks = await Run(Healthy(granted));

        var failed = Assert.Single(checks, c => c.Status == AzureDevOpsCheckStatus.Fail);
        Assert.Equal("Agent can Contribute", failed.Name);
        Assert.Contains("Allow", failed.Fix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, 1, false, "disabled")]
    [InlineData(true, false, 1, false, "optional")]
    [InlineData(true, true, 0, false, "fewer than 1")]
    [InlineData(true, true, 1, true, "approve their own change")]
    [InlineData(true, true, 1, null, "does not say")]
    public async Task Weak_branch_policy_fails(bool enabled, bool blocking, int approvers, bool? creatorVoteCounts, string expected)
    {
        var checks = await Run(Healthy(policyJson: AzureDevOpsClientTests.PolicyJson(7, enabled, blocking, approvers, creatorVoteCounts)));

        var failed = Assert.Single(checks, c => c.Status == AzureDevOpsCheckStatus.Fail);
        Assert.StartsWith("Target branch refs/heads/main", failed.Name, StringComparison.Ordinal);
        Assert.Contains(expected, failed.Detail, StringComparison.Ordinal);
        Assert.Contains("Require a minimum number of reviewers", failed.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_branch_policy_fails()
    {
        var checks = await Run(Healthy(policyJson: "{\"count\":0,\"value\":[]}"));

        var failed = Assert.Single(checks, c => c.Status == AzureDevOpsCheckStatus.Fail);
        Assert.Contains("no \"Minimum number of reviewers\" policy", failed.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_good_policy_among_weak_ones_passes()
    {
        const string json = """
            [
              { "id": 1, "isEnabled": false, "isBlocking": true, "settings": { "minimumApproverCount": 2, "creatorVoteCounts": false }, "type": { "id": "fa4e907d-c16b-4a4c-9dfa-4906e5d171dd" } },
              { "id": 2, "isEnabled": true, "isBlocking": true, "settings": { "minimumApproverCount": 1, "creatorVoteCounts": false }, "type": { "id": "fa4e907d-c16b-4a4c-9dfa-4906e5d171dd" } }
            ]
            """;

        var checks = await Run(Healthy(policyJson: json));

        Assert.All(checks, c => Assert.Equal(AzureDevOpsCheckStatus.Ok, c.Status));
    }

    [Fact]
    public async Task Missing_token_fails_first_and_skips_the_rest_without_requests()
    {
        var handler = Healthy();
        var tokens = new EnvironmentTokenProvider("AUTOPILOT_ADO_PAT", _ => null);

        var checks = await Run(handler, tokens);

        Assert.Equal(AzureDevOpsCheckStatus.Fail, checks[0].Status);
        Assert.Contains("AUTOPILOT_ADO_PAT", checks[0].Fix, StringComparison.Ordinal);
        Assert.All(checks.Skip(1), c => Assert.Equal(AzureDevOpsCheckStatus.Skip, c.Status));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Unreachable_project_fails_with_the_api_advice()
    {
        var handler = new FakeHandler().On(HttpMethod.Get, $"{Ado.Org}/_apis/projects/Shop", "{\"message\":\"TF200016: project does not exist\"}", HttpStatusCode.NotFound);

        var checks = await Run(handler);

        Assert.Equal(AzureDevOpsCheckStatus.Ok, checks[0].Status);
        Assert.Equal(AzureDevOpsCheckStatus.Fail, checks[1].Status);
        Assert.Contains("404", checks[1].Fix, StringComparison.Ordinal);
        Assert.All(checks.Skip(2), c => Assert.Equal(AzureDevOpsCheckStatus.Skip, c.Status));
    }

    [Fact]
    public async Task Unreachable_repository_fails_and_skips_repository_checks()
    {
        var handler = new FakeHandler()
            .On(HttpMethod.Get, $"{Ado.Org}/_apis/projects/Shop", Ado.ProjectJson)
            .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson());

        var checks = await Run(handler);

        Assert.Contains(checks, c => c.Name == "Work item query" && c.Status == AzureDevOpsCheckStatus.Ok && c.Detail.Contains("no item matches", StringComparison.Ordinal));
        Assert.Contains(checks, c => c.Name == "Azure DevOps repository" && c.Status == AzureDevOpsCheckStatus.Fail);
        Assert.Contains(checks, c => c.Name == "Agent identity rights on the repository" && c.Status == AzureDevOpsCheckStatus.Skip);
    }

    [Fact]
    public async Task Permission_check_that_the_token_may_not_run_is_skipped()
    {
        var handler = new FakeHandler()
            .On(HttpMethod.Get, $"{Ado.Org}/_apis/permissions/", "{\"message\":\"forbidden\"}", HttpStatusCode.Forbidden);
        foreach (var (method, prefix, json) in new[]
        {
            (HttpMethod.Get, $"{Ado.Org}/_apis/projects/Shop", Ado.ProjectJson),
            (HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson(1)),
            (HttpMethod.Get, $"{Ado.Org}/Shop/_apis/git/repositories/shop-api", Ado.RepositoryJson),
            (HttpMethod.Get, $"{Ado.Org}/Shop/_apis/git/policy/configurations", AzureDevOpsClientTests.PolicyJson(7, true, true, 1, false)),
        })
        {
            handler.On(method, prefix, json);
        }

        var checks = await Run(handler);

        Assert.Equal(AzureDevOpsDoctor.RequiredPermissions.Count + AzureDevOpsDoctor.ForbiddenPermissions.Count,
            checks.Count(c => c.Status == AzureDevOpsCheckStatus.Skip));
        Assert.DoesNotContain(checks, c => c.Status == AzureDevOpsCheckStatus.Fail);
    }

    [Fact]
    public async Task No_repository_setting_skips_repository_checks()
    {
        var checks = await Run(Healthy(), repository: null);

        Assert.Contains(checks, c => c.Name == "Azure DevOps repository" && c.Status == AzureDevOpsCheckStatus.Skip);
        Assert.DoesNotContain(checks, c => c.Status == AzureDevOpsCheckStatus.Fail);
    }
}
