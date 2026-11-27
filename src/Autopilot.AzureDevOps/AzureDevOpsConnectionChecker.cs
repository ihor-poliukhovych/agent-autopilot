using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Autopilot.AzureDevOps;

/// <summary>
/// Connection checks in plain words, for <c>doctor</c> and for a setup wizard. Each check returns one
/// <see cref="ConnectionCheckResult"/> with a message and, when it did not pass, a fix hint. No check writes
/// anything: the write check asks Azure DevOps to validate an update without saving it.
/// </summary>
public sealed class AzureDevOpsConnectionChecker(AzureDevOpsClient client, AzureDevOpsSettings settings, string baseBranch) : IAzureDevOpsConnectionChecker
{
    public const string SignInId = "sign-in";
    public const string QueryId = "query";
    public const string WorkItemReadId = "work-item-read";
    public const string WorkItemWriteId = "work-item-write";
    public const string GitPermissionsId = "git-permissions";
    public const string BranchPolicyId = "branch-policy";

    public async Task<IReadOnlyList<ConnectionCheckResult>> CheckAllAsync(CancellationToken ct)
    {
        var results = new List<ConnectionCheckResult>();
        var signIn = await CheckSignInAsync(ct).ConfigureAwait(false);
        results.Add(signIn);
        if (signIn.Outcome == CheckOutcome.Fail)
        {
            const string Reason = "sign-in does not work yet";
            results.Add(Skipped(QueryId, "Work item query", Reason));
            results.Add(Skipped(WorkItemReadId, "Read work items", Reason));
            results.Add(Skipped(WorkItemWriteId, "Write work items", Reason));
            results.Add(Skipped(GitPermissionsId, "Push branches and open pull requests", Reason));
            results.Add(Skipped(BranchPolicyId, "Base branch policy", Reason));
            return results;
        }

        results.Add(await CheckQueryAsync(null, ct).ConfigureAwait(false));
        results.Add(await CheckWorkItemReadAsync(ct).ConfigureAwait(false));
        results.Add(await CheckWorkItemWriteAsync(ct).ConfigureAwait(false));
        results.Add(await CheckGitPermissionsAsync(ct).ConfigureAwait(false));
        var summary = await GetBranchPolicySummaryAsync(ct).ConfigureAwait(false);
        results.Add(PolicyResult(summary));
        return results;
    }

    public async Task<ConnectionCheckResult> CheckSignInAsync(CancellationToken ct)
    {
        const string Title = "Sign in to Azure DevOps";
        try
        {
            await client.EnsureTokenAsync(ct).ConfigureAwait(false);
        }
        catch (AzureDevOpsAuthException ex)
        {
            return new ConnectionCheckResult(SignInId, Title, CheckOutcome.Fail, $"No token could be obtained: {ex.Message}", SignInFix());
        }

        try
        {
            var project = await client.GetProjectAsync(ct).ConfigureAwait(false);
            return new ConnectionCheckResult(SignInId, Title, CheckOutcome.Ok, $"Signed in with {client.TokenSource}; project \"{project.Name}\" is reachable.");
        }
        catch (AzureDevOpsException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new ConnectionCheckResult(SignInId, Title, CheckOutcome.Fail, $"The token works, but project \"{settings.Project}\" was not found.",
                "Check \"organizationUrl\" and \"project\" in the project settings, and that the sign-in identity is a member of the project.");
        }
        catch (AzureDevOpsException ex)
        {
            return new ConnectionCheckResult(SignInId, Title, CheckOutcome.Fail, ex.Message, SignInFix());
        }
    }

    public async Task<ConnectionCheckResult> CheckQueryAsync(string? query, CancellationToken ct)
    {
        const string Title = "Work item query";
        var text = string.IsNullOrWhiteSpace(query) ? settings.Query : query;
        try
        {
            var ids = await client.QueryWorkItemIdsAsync(text, WorkItemPreviewRequest.MaxTotal + 1, ct).ConfigureAwait(false);
            var count = ids.Count > WorkItemPreviewRequest.MaxTotal ? $"more than {WorkItemPreviewRequest.MaxTotal}" : ids.Count.ToString(CultureInfo.InvariantCulture);
            if (ids.Count == 0)
            {
                var hint = settings.UsesDefaultQuery && string.IsNullOrWhiteSpace(query)
                    ? $"Tag a work item \"{settings.QueryTag}\", or set \"query\" to your own WIQL query."
                    : "Check the conditions of the query (project, state, tags, area path).";
                return new ConnectionCheckResult(QueryId, Title, CheckOutcome.Warn, "The query is valid but no work item matches it.", hint);
            }

            return new ConnectionCheckResult(QueryId, Title, CheckOutcome.Ok,
                $"The query is valid; {count} work item(s) match (the agent reads at most {settings.MaxItems}).");
        }
        catch (AzureDevOpsException ex)
        {
            return new ConnectionCheckResult(QueryId, Title, CheckOutcome.Fail, $"The query was not accepted: {ex.Message}",
                "Fix the WIQL query. A flat query that starts with SELECT [System.Id] FROM WorkItems works best; check field names and quotes.");
        }
    }

    public async Task<ConnectionCheckResult> CheckWorkItemReadAsync(CancellationToken ct)
    {
        const string Title = "Read work items";
        try
        {
            var id = await SampleWorkItemAsync(ct).ConfigureAwait(false);
            if (id is null)
            {
                return Skipped(WorkItemReadId, Title, "the project has no work item to try with");
            }

            var items = await client.GetWorkItemsAsync([id.Value], ct).ConfigureAwait(false);
            return items.Count == 1
                ? new ConnectionCheckResult(WorkItemReadId, Title, CheckOutcome.Ok, $"Work item {id} can be read (fields, comments and links).")
                : new ConnectionCheckResult(WorkItemReadId, Title, CheckOutcome.Fail, $"Work item {id} was listed but could not be read.", WorkItemFix());
        }
        catch (AzureDevOpsException ex)
        {
            return new ConnectionCheckResult(WorkItemReadId, Title, CheckOutcome.Fail, ex.Message, WorkItemFix());
        }
    }

    public async Task<ConnectionCheckResult> CheckWorkItemWriteAsync(CancellationToken ct)
    {
        const string Title = "Write work items";
        try
        {
            var id = await SampleWorkItemAsync(ct).ConfigureAwait(false);
            if (id is null)
            {
                return Skipped(WorkItemWriteId, Title, "the project has no work item to try with");
            }

            var item = (await client.GetWorkItemsAsync([id.Value], ct).ConfigureAwait(false)).FirstOrDefault();
            if (item is null)
            {
                return Skipped(WorkItemWriteId, Title, "the sample work item could not be read");
            }

            // validateOnly: Azure DevOps runs its checks (including permissions) and saves nothing.
            var tags = item.GetString(WorkItemFields.Tags) ?? string.Empty;
            await client.UpdateWorkItemAsync(id.Value, item.Rev, [new WorkItemPatchOp("add", "/fields/" + WorkItemFields.Tags, tags)], validateOnly: true, ct).ConfigureAwait(false);
            return new ConnectionCheckResult(WorkItemWriteId, Title, CheckOutcome.Ok,
                $"Azure DevOps accepts an update of work item {id} (checked without saving anything), so tags, comments and states can be written.");
        }
        catch (AzureDevOpsException ex)
        {
            return new ConnectionCheckResult(WorkItemWriteId, Title, CheckOutcome.Fail, $"Writing a work item is not allowed: {ex.Message}",
                "Give the PAT the scope \"Work Items: Read & write\" and make the identity a Contributor of the project (and of the area path of the items).");
        }
    }

    public async Task<ConnectionCheckResult> CheckGitPermissionsAsync(CancellationToken ct)
    {
        const string Title = "Push branches and open pull requests";
        if (string.IsNullOrWhiteSpace(settings.Repository))
        {
            return Skipped(GitPermissionsId, Title, "no \"repository\" is set in the project settings");
        }

        try
        {
            var repository = await client.GetRepositoryAsync(settings.Repository, ct).ConfigureAwait(false);
            var project = string.IsNullOrEmpty(repository.ProjectId) ? (await client.GetProjectAsync(ct).ConfigureAwait(false)).Id : repository.ProjectId;
            var token = GitRepositoryPermissions.RepositoryToken(project, repository.Id);
            var missing = new List<string>();
            foreach (var (bit, _, portal) in AzureDevOpsDoctor.RequiredPermissions)
            {
                if (!await client.HasPermissionAsync(GitRepositoryPermissions.NamespaceId, bit, token, ct).ConfigureAwait(false))
                {
                    missing.Add(portal);
                }
            }

            return missing.Count == 0
                ? new ConnectionCheckResult(GitPermissionsId, Title, CheckOutcome.Ok, $"The identity can push branches and open pull requests in \"{repository.Name}\".")
                : new ConnectionCheckResult(GitPermissionsId, Title, CheckOutcome.Fail, $"The identity is missing: {string.Join(", ", missing)}.",
                    $"In Project settings > Repositories > {repository.Name} > Security, select the agent identity and set {string.Join(", ", missing.Select(m => $"\"{m}\""))} to Allow.");
        }
        catch (AzureDevOpsException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new ConnectionCheckResult(GitPermissionsId, Title, CheckOutcome.Warn, "This token may not look at permissions, so they could not be checked.",
                "Try a branch push by hand, or check Project settings > Repositories > Security in the portal.");
        }
        catch (AzureDevOpsException ex)
        {
            return new ConnectionCheckResult(GitPermissionsId, Title, CheckOutcome.Fail, ex.Message,
                "Check \"repository\" in the project settings and that the identity can read it.");
        }
    }

    public async Task<BranchPolicySummary> GetBranchPolicySummaryAsync(CancellationToken ct)
    {
        var branch = baseBranch.StartsWith("refs/heads/", StringComparison.Ordinal) ? baseBranch : "refs/heads/" + baseBranch;
        if (string.IsNullOrWhiteSpace(settings.Repository))
        {
            return new BranchPolicySummary(branch, [], false, "No repository is set, so the branch policies cannot be read.");
        }

        try
        {
            var repository = await client.GetRepositoryAsync(settings.Repository, ct).ConfigureAwait(false);
            var policies = await client.GetBranchPoliciesAsync(repository.Id, branch, null, ct).ConfigureAwait(false);
            var items = policies.Where(p => !p.IsDeleted).Select(Describe).ToList();
            var human = policies.Any(p => string.Equals(p.TypeId, PolicyTypes.MinimumReviewers, StringComparison.OrdinalIgnoreCase) && AzureDevOpsDoctor.IsGoodReviewerPolicy(p));
            var summary = items.Count == 0
                ? $"No branch policy applies to {branch}: anyone with Contribute rights could merge into it. Add a \"Minimum number of reviewers\" policy."
                : human
                    ? $"{branch} needs a person's approval before a merge ({items.Count} polic{(items.Count == 1 ? "y" : "ies")} in total). The agent cannot merge."
                    : $"{branch} has {items.Count} polic{(items.Count == 1 ? "y" : "ies")} but none that requires another person's approval. Add a required \"Minimum number of reviewers\" policy.";
            return new BranchPolicySummary(branch, items, human, summary);
        }
        catch (AzureDevOpsException ex)
        {
            return new BranchPolicySummary(branch, [], false, $"The branch policies could not be read: {ex.Message}");
        }
    }

    private static ConnectionCheckResult PolicyResult(BranchPolicySummary summary) =>
        summary.RequiresHumanApproval
            ? new ConnectionCheckResult(BranchPolicyId, "Base branch policy", CheckOutcome.Ok, summary.Summary)
            : new ConnectionCheckResult(BranchPolicyId, "Base branch policy", CheckOutcome.Fail, summary.Summary,
                "In Project settings > Repositories > Policies, open the base branch and turn on \"Require a minimum number of reviewers\" with at least 1 reviewer, \"Allow requestors to approve their own changes\" off, and the policy required.");

    private static BranchPolicyItem Describe(AdoPolicyConfiguration policy)
    {
        var name = policy.TypeDisplayName ?? policy.TypeId;
        string detail;
        if (string.Equals(policy.TypeId, PolicyTypes.MinimumReviewers, StringComparison.OrdinalIgnoreCase))
        {
            var count = policy.Settings.ValueKind == JsonValueKind.Object && policy.Settings.TryGetProperty("minimumApproverCount", out var c) && c.TryGetInt32(out var n) ? n : 0;
            var own = policy.Settings.ValueKind == JsonValueKind.Object && policy.Settings.TryGetProperty("creatorVoteCounts", out var v) && v.ValueKind == JsonValueKind.True;
            detail = $"{count} reviewer(s); the author's own vote {(own ? "counts" : "does not count")}";
        }
        else
        {
            detail = policy.IsBlocking ? "blocks the merge until satisfied" : "advisory, does not block";
        }

        return new BranchPolicyItem(name, policy.IsEnabled, policy.IsBlocking, detail);
    }

    /// <summary>The first item the configured query returns, else any item of the project.</summary>
    private async Task<int?> SampleWorkItemAsync(CancellationToken ct)
    {
        var ids = await client.QueryWorkItemIdsAsync(settings.Query, 1, ct).ConfigureAwait(false);
        if (ids.Count > 0)
        {
            return ids[0];
        }

        ids = await client.QueryWorkItemIdsAsync("SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project ORDER BY [System.ChangedDate] DESC", 1, ct).ConfigureAwait(false);
        return ids.Count > 0 ? ids[0] : null;
    }

    private string SignInFix() => settings.Auth switch
    {
        AzureDevOpsAuth.AzureCli => "Run 'az login' as the agent identity (or 'az login --service-principal ...'), then try again.",
        AzureDevOpsAuth.ServicePrincipal =>
            $"Set the environment variables {settings.TenantVariable}, {settings.ClientIdVariable} and {settings.ClientSecretVariable} (user level), make sure the secret has not expired, and that the app is added to the Azure DevOps organisation.",
        _ => $"Create a personal access token for the agent identity and store it in the user environment variable {settings.TokenVariable}; if it was set, it may be expired or lack scopes.",
    };

    private static string WorkItemFix() =>
        "Give the PAT the scope \"Work Items: Read\" (or Read & write) and make the identity a member of the project.";

    private static ConnectionCheckResult Skipped(string id, string title, string reason) =>
        new(id, title, CheckOutcome.Skipped, $"Skipped: {reason}.");
}
