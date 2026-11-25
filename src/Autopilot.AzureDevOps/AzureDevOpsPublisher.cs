using System.Globalization;
using Autopilot.Core;
using Autopilot.Core.Gate;
using Autopilot.Core.Local;

namespace Autopilot.AzureDevOps;

/// <summary>
/// Publishes a passed change as a draft pull request: pushes the agent branch (never forced) and
/// opens a draft PR linked to the work item. It never sets auto-complete, never votes and never
/// completes the PR; a person reviews and merges it.
/// </summary>
public sealed class AzureDevOpsPublisher : IPublisher
{
    public const string TitlePrefix = "[autopilot] ";

    /// <summary>Longest description the publisher sends; longer text is cut with a note.</summary>
    public const int MaxDescriptionLength = 4000;

    private readonly AzureDevOpsClient _client;
    private readonly AzureDevOpsSettings _settings;
    private readonly string _projectRoot;
    private readonly string _baseBranch;
    private readonly string _branchPrefix;
    private readonly IGitPusher _pusher;
    private readonly Func<PublishRequest, string> _description;

    public AzureDevOpsPublisher(
        AzureDevOpsClient client,
        AzureDevOpsSettings settings,
        string projectRoot,
        string baseBranch,
        string branchPrefix,
        IGitPusher? pusher = null,
        Func<PublishRequest, string>? description = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchPrefix);
        if (string.IsNullOrWhiteSpace(settings.Repository))
        {
            throw new AzureDevOpsSettingsException(["Set \"repository\" to the name of the Git repository the agent pushes to, for example \"shop-api\"."]);
        }

        _client = client;
        _settings = settings;
        _projectRoot = projectRoot;
        _baseBranch = baseBranch;
        _branchPrefix = branchPrefix;
        _pusher = pusher ?? new GitPusher(settings.OrganizationUrl);
        _description = description ?? DefaultDescription;
    }

    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (request.Decision.Outcome != GateOutcome.Passed)
            {
                throw new AzureDevOpsPublishException("Refusing to open a pull request: the merge gate did not pass this change.");
            }

            GitPusher.EnsureAgentBranch(request.BranchName, _branchPrefix);
            var repository = await _client.GetRepositoryAsync(_settings.Repository!, ct).ConfigureAwait(false);
            var credential = await _client.GetCredentialAsync(ct).ConfigureAwait(false);
            await _pusher.PushAsync(_projectRoot, _settings.Remote, request.BranchName, _branchPrefix, credential, ct).ConfigureAwait(false);

            var pr = await _client.CreateDraftPullRequestAsync(
                repository.Id,
                new PullRequestCreate(
                    request.BranchName,
                    StripHeads(_baseBranch),
                    Title(request.WorkItem.Title),
                    Cut(_description(request)),
                    request.WorkItem.Id),
                ct).ConfigureAwait(false);

            return new PublishResult(true, WebUrl(pr, repository), null);
        }
        catch (Exception ex) when (ex is AzureDevOpsException or AzureDevOpsAuthException or AzureDevOpsPublishException)
        {
            return new PublishResult(false, string.Empty, ex.Message);
        }
    }

    public static string Title(string workItemTitle)
    {
        var single = string.Join(' ', (workItemTitle ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return TitlePrefix + (single.Length == 0 ? "change" : single);
    }

    /// <summary>The same markdown as the local PULL_REQUEST.md, without the pointer to the local patch file.</summary>
    public static string DefaultDescription(PublishRequest request)
    {
        var text = LocalPublisher.PullRequestText(request);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => !l.StartsWith("- Patch: ", StringComparison.Ordinal));
        return string.Join('\n', lines);
    }

    internal static string Cut(string description)
    {
        const string note = "\n\n(Cut to fit the pull request description limit; the full text is in the local run folder.)";
        return description.Length <= MaxDescriptionLength
            ? description
            : description[..(MaxDescriptionLength - note.Length)] + note;
    }

    private string WebUrl(AdoPullRequest pr, AdoRepository repository)
    {
        if (!string.IsNullOrEmpty(pr.WebUrl))
        {
            return pr.WebUrl;
        }

        var id = pr.PullRequestId.ToString(CultureInfo.InvariantCulture);
        var repoWeb = repository.WebUrl
            ?? $"{_client.OrganizationUrl.AbsoluteUri.TrimEnd('/')}/{Uri.EscapeDataString(_settings.Project)}/_git/{Uri.EscapeDataString(repository.Name)}";
        return repoWeb.TrimEnd('/') + "/pullrequest/" + id;
    }

    private static string StripHeads(string branch) =>
        branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch;
}
