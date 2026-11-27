using System.Net;
using System.Text.Json;

namespace Autopilot.AzureDevOps;

public enum AzureDevOpsCheckStatus
{
    Ok,
    Fail,
    Skip,
}

/// <summary>One doctor line for the Azure DevOps module.</summary>
public sealed record AzureDevOpsCheck(AzureDevOpsCheckStatus Status, string Name, string Detail, string? Fix = null);

/// <summary>
/// Checks the Azure DevOps setup: token, project, repository, query, the agent identity's rights on the
/// repository, and a blocking human-review policy on the target branch.
/// </summary>
public sealed class AzureDevOpsDoctor(AzureDevOpsClient client, AzureDevOpsSettings settings, string baseBranch)
{
    private const string RepoSecurityPath = "Project settings > Repositories > {0} > Security, select the agent identity";

    /// <summary>Rights the agent identity must not have, with the name shown in the web portal.</summary>
    public static IReadOnlyList<(int Bit, string Name, string PortalName)> ForbiddenPermissions { get; } =
    [
        (GitRepositoryPermissions.ForcePush, "ForcePush", "Force push (rewrite history and delete branches)"),
        (GitRepositoryPermissions.EditPolicies, "EditPolicies", "Edit policies"),
        (GitRepositoryPermissions.ManagePermissions, "ManagePermissions", "Manage permissions"),
        (GitRepositoryPermissions.PolicyExempt, "PolicyExempt", "Bypass policies when pushing"),
        (GitRepositoryPermissions.PullRequestBypassPolicy, "PullRequestBypassPolicy", "Bypass policies when completing pull requests"),
    ];

    /// <summary>Rights the agent identity needs to push its branch and open a pull request.</summary>
    public static IReadOnlyList<(int Bit, string Name, string PortalName)> RequiredPermissions { get; } =
    [
        (GitRepositoryPermissions.GenericContribute, "GenericContribute", "Contribute"),
        (GitRepositoryPermissions.CreateBranch, "CreateBranch", "Create branch"),
        (GitRepositoryPermissions.PullRequestContribute, "PullRequestContribute", "Contribute to pull requests"),
    ];

    public async Task<IReadOnlyList<AzureDevOpsCheck>> RunAsync(CancellationToken ct)
    {
        var results = new List<AzureDevOpsCheck>();

        // 1. Token
        try
        {
            await client.EnsureTokenAsync(ct).ConfigureAwait(false);
            results.Add(Ok("Azure DevOps token", $"found ({client.TokenSource})"));
        }
        catch (AzureDevOpsAuthException ex)
        {
            results.Add(Fail("Azure DevOps token", "no token", ex.Message));
            SkipRest(results, "no token");
            return results;
        }

        // 2. Project
        AdoProject project;
        try
        {
            project = await client.GetProjectAsync(ct).ConfigureAwait(false);
            results.Add(Ok("Azure DevOps project", $"\"{project.Name}\" is reachable"));
        }
        catch (AzureDevOpsException ex)
        {
            results.Add(Fail("Azure DevOps project", $"\"{settings.Project}\" is not reachable", ex.Message));
            SkipRest(results, "the project is not reachable");
            return results;
        }

        // 3. Query
        try
        {
            var ids = await client.QueryWorkItemIdsAsync(settings.Query, settings.MaxItems, ct).ConfigureAwait(false);
            var selection = settings.UsesDefaultQuery
                ? $"default query, tag \"{settings.QueryTag}\""
                : "custom query";
            results.Add(ids.Count > 0
                ? Ok("Work item query", $"{ids.Count} item(s) match ({selection})")
                : Ok("Work item query", settings.UsesDefaultQuery
                    ? $"runs, but no item matches yet ({selection}); tag a work item \"{settings.QueryTag}\" (or change \"query\") to give the agent work"
                    : $"runs, but no item matches yet ({selection})"));
        }
        catch (AzureDevOpsException ex)
        {
            results.Add(Fail("Work item query", "the query failed", ex.Message));
        }

        // 4. Repository
        if (string.IsNullOrWhiteSpace(settings.Repository))
        {
            results.Add(Skip("Azure DevOps repository", "no \"repository\" setting; the publisher and the checks below need it"));
            SkipRepositoryChecks(results, "no repository setting");
            return results;
        }

        AdoRepository repository;
        try
        {
            repository = await client.GetRepositoryAsync(settings.Repository, ct).ConfigureAwait(false);
            results.Add(Ok("Azure DevOps repository", $"\"{repository.Name}\" is reachable"));
        }
        catch (AzureDevOpsException ex)
        {
            results.Add(Fail("Azure DevOps repository", $"\"{settings.Repository}\" is not reachable", ex.Message));
            SkipRepositoryChecks(results, "the repository is not reachable");
            return results;
        }

        // 5. Permissions
        var projectId = string.IsNullOrEmpty(repository.ProjectId) ? project.Id : repository.ProjectId;
        var token = GitRepositoryPermissions.RepositoryToken(projectId, repository.Id);
        var where = string.Format(System.Globalization.CultureInfo.InvariantCulture, RepoSecurityPath, repository.Name);
        foreach (var (bit, _, portal) in RequiredPermissions)
        {
            results.Add(await PermissionCheckAsync(token, bit, $"Agent can {portal}", mustHave: true,
                $"In {where}, set \"{portal}\" to Allow.", ct).ConfigureAwait(false));
        }

        foreach (var (bit, name, portal) in ForbiddenPermissions)
        {
            results.Add(await PermissionCheckAsync(token, bit, $"Agent cannot {portal} ({name})", mustHave: false,
                $"In {where}, set \"{portal}\" to Deny (also check the groups the identity belongs to, such as Project Administrators).", ct).ConfigureAwait(false));
        }

        // 6. Branch policy
        results.Add(await BranchPolicyCheckAsync(repository, ct).ConfigureAwait(false));
        return results;
    }

    private async Task<AzureDevOpsCheck> PermissionCheckAsync(string token, int bit, string name, bool mustHave, string fix, CancellationToken ct)
    {
        try
        {
            var has = await client.HasPermissionAsync(GitRepositoryPermissions.NamespaceId, bit, token, ct).ConfigureAwait(false);
            if (has == mustHave)
            {
                return Ok(name, mustHave ? "allowed" : "not allowed");
            }

            return Fail(name, mustHave ? "the agent identity does not have this right" : "the agent identity has this right", fix);
        }
        catch (AzureDevOpsException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Skip(name, "could not check with this token; check it by hand in the portal");
        }
        catch (AzureDevOpsException ex)
        {
            return Fail(name, "the permission check failed", ex.Message);
        }
    }

    private async Task<AzureDevOpsCheck> BranchPolicyCheckAsync(AdoRepository repository, CancellationToken ct)
    {
        var branch = baseBranch.StartsWith("refs/heads/", StringComparison.Ordinal) ? baseBranch : "refs/heads/" + baseBranch;
        var name = $"Target branch {branch} needs a human approval";
        var fix = $"In Project settings > Repositories > {repository.Name} > Policies > Branch policies, open {branch} and turn on " +
            "\"Require a minimum number of reviewers\" with at least 1 reviewer, leave \"Allow requestors to approve their own changes\" off, and keep the policy required (not optional).";
        IReadOnlyList<AdoPolicyConfiguration> policies;
        try
        {
            policies = await client.GetBranchPoliciesAsync(repository.Id, branch, PolicyTypes.MinimumReviewers, ct).ConfigureAwait(false);
        }
        catch (AzureDevOpsException ex)
        {
            return Fail(name, "could not read the branch policies", ex.Message);
        }

        var reviewers = policies
            .Where(p => string.Equals(p.TypeId, PolicyTypes.MinimumReviewers, StringComparison.OrdinalIgnoreCase) && !p.IsDeleted)
            .ToList();
        if (reviewers.Any(IsGoodReviewerPolicy))
        {
            return Ok(name, "an enabled, required \"Minimum number of reviewers\" policy (at least 1, the author's vote does not count) applies");
        }

        if (reviewers.Count == 0)
        {
            return Fail(name, "no \"Minimum number of reviewers\" policy applies to this branch", fix);
        }

        var problems = new List<string>();
        foreach (var policy in reviewers)
        {
            if (!policy.IsEnabled)
            {
                problems.Add($"policy {policy.Id} is disabled");
            }
            else if (!policy.IsBlocking)
            {
                problems.Add($"policy {policy.Id} is optional, not required");
            }
            else if (MinimumApprovers(policy) < 1)
            {
                problems.Add($"policy {policy.Id} needs fewer than 1 approver");
            }
            else if (CreatorVoteCounts(policy) is not { } creatorVotes)
            {
                problems.Add($"policy {policy.Id} does not say whether the author's own vote counts");
            }
            else if (creatorVotes)
            {
                problems.Add($"policy {policy.Id} lets the author approve their own change");
            }
        }

        return Fail(name, string.Join("; ", problems), fix);
    }

    public static bool IsGoodReviewerPolicy(AdoPolicyConfiguration policy) =>
        policy.IsEnabled && policy.IsBlocking && !policy.IsDeleted
        && MinimumApprovers(policy) >= 1
        && CreatorVoteCounts(policy) == false;

    private static int MinimumApprovers(AdoPolicyConfiguration policy) =>
        policy.Settings.ValueKind == JsonValueKind.Object
        && policy.Settings.TryGetProperty("minimumApproverCount", out var count)
        && count.TryGetInt32(out var value) ? value : 0;

    /// <summary>True, false, or null when the setting is missing (treated as not proven safe).</summary>
    private static bool? CreatorVoteCounts(AdoPolicyConfiguration policy) =>
        policy.Settings.ValueKind == JsonValueKind.Object && policy.Settings.TryGetProperty("creatorVoteCounts", out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static void SkipRest(List<AzureDevOpsCheck> results, string reason)
    {
        results.Add(Skip("Work item query", reason));
        results.Add(Skip("Azure DevOps repository", reason));
        SkipRepositoryChecks(results, reason);
    }

    private static void SkipRepositoryChecks(List<AzureDevOpsCheck> results, string reason)
    {
        results.Add(Skip("Agent identity rights on the repository", reason));
        results.Add(Skip("Target branch needs a human approval", reason));
    }

    private static AzureDevOpsCheck Ok(string name, string detail) => new(AzureDevOpsCheckStatus.Ok, name, detail);

    private static AzureDevOpsCheck Fail(string name, string detail, string fix) => new(AzureDevOpsCheckStatus.Fail, name, detail, fix);

    private static AzureDevOpsCheck Skip(string name, string detail) => new(AzureDevOpsCheckStatus.Skip, name, detail);
}
