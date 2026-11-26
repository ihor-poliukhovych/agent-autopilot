using System.Globalization;

namespace DevAutopilot.AzureDevOps;

/// <summary>
/// Draft pull requests. The only things this class can do are open a draft (or reuse the one already open
/// for the branch) and change the title and description. It has no way to mark a pull request ready, vote,
/// set auto-complete or complete it: merging is always done by a person.
/// </summary>
public sealed class AzureDevOpsPullRequests(AzureDevOpsClient client, AzureDevOpsSettings settings) : IAzureDevOpsPullRequests
{
    public async Task<DraftPullRequestResult> EnsureDraftAsync(DraftPullRequestRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var repository = await GetRepositoryAsync(ct).ConfigureAwait(false);
        var target = StripHeads(request.TargetBranch);
        var source = StripHeads(request.SourceBranch);

        var open = await client.FindActivePullRequestsAsync(repository.Id, source, target, ct).ConfigureAwait(false);
        if (open.FirstOrDefault() is { } existing)
        {
            return ToResult(existing, repository, created: false);
        }

        var created = await client.CreateDraftPullRequestAsync(
            repository.Id,
            new PullRequestCreate(source, target, request.Title, AzureDevOpsPublisher.Cut(request.Description), request.WorkItemId?.ToString(CultureInfo.InvariantCulture)),
            ct).ConfigureAwait(false);
        return ToResult(created, repository, created: true);
    }

    public async Task<DraftPullRequestResult> UpdateDescriptionAsync(int pullRequestId, string description, string? title, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(description);
        var repository = await GetRepositoryAsync(ct).ConfigureAwait(false);
        var current = await client.GetPullRequestAsync(repository.Id, pullRequestId, ct).ConfigureAwait(false);
        if (!string.Equals(current.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            throw new AzureDevOpsException($"Pull request {pullRequestId} is {current.Status ?? "not open"}; only an open pull request is updated.", null);
        }

        var text = AzureDevOpsPublisher.Cut(description);
        if (string.Equals(current.Description, text, StringComparison.Ordinal) && (title is null || string.Equals(current.Title, title, StringComparison.Ordinal)))
        {
            return ToResult(current, repository, created: false);
        }

        var updated = await client.UpdatePullRequestTextAsync(repository.Id, pullRequestId, title, text, ct).ConfigureAwait(false);
        return ToResult(updated, repository, created: false);
    }

    private async Task<AdoRepository> GetRepositoryAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Repository))
        {
            throw new AzureDevOpsSettingsException(["Set \"repository\" to the name of the Git repository the agent pushes to, for example \"shop-api\"."]);
        }

        return await client.GetRepositoryAsync(settings.Repository, ct).ConfigureAwait(false);
    }

    private DraftPullRequestResult ToResult(AdoPullRequest pr, AdoRepository repository, bool created)
    {
        var url = pr.WebUrl;
        if (string.IsNullOrEmpty(url))
        {
            var repoWeb = repository.WebUrl
                ?? $"{client.OrganizationUrl.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(settings.Project)}/_git/{Uri.EscapeDataString(repository.Name)}";
            url = repoWeb.TrimEnd('/') + "/pullrequest/" + pr.PullRequestId.ToString(CultureInfo.InvariantCulture);
        }

        return new DraftPullRequestResult(pr.PullRequestId, url, created, pr.IsDraft);
    }

    internal static string StripHeads(string branch) =>
        branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch;
}
