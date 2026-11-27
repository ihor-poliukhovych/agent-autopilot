using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Autopilot.Core;

namespace Autopilot.AzureDevOps;

/// <summary>
/// Writes the agent's results back to a work item: tags, state, assignee, comments and the link to the draft
/// pull request. Every call first looks at what is already there, so running it again changes nothing.
/// Comments carry a visible reference line (<c>ref:kind:key</c>) that later calls search for.
/// </summary>
public sealed class AzureDevOpsWriteback(AzureDevOpsClient client, AzureDevOpsSettings settings) : IAzureDevOpsWriteback
{
    public const string CommentMarker = AzureDevOpsWorkItemSource.CommentMarker;

    /// <summary>States an item is never moved out of.</summary>
    public static IReadOnlyList<string> FinishedStates { get; } = ["Closed", "Done", "Removed", "Completed"];

    private const int CommentsToSearch = 200;
    private const int MaxAttempts = 2;

    public async Task<WriteBackResult> RecordTriageAsync(int id, TriageResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        var workflowEvent = result.Label == TriageLabel.AgentReady ? WorkflowEvent.AgentReady : WorkflowEvent.NeedsHuman;
        var patched = await PatchAsync(id, (item, changed, unchanged) =>
        {
            var ops = new List<WorkItemPatchOp>();
            var current = item.GetString(WorkItemFields.Tags);
            var tags = AzureDevOpsWorkItemSource.ReplaceTriageTag(current, settings.TagPrefix, result.Label);
            if (!string.Equals(string.Join("; ", AzureDevOpsWorkItemSource.SplitTags(current)), tags, StringComparison.Ordinal))
            {
                ops.Add(new WorkItemPatchOp("add", "/fields/" + WorkItemFields.Tags, tags));
                changed.Add("triage tag");
            }
            else
            {
                unchanged.Add("triage tag already set");
            }

            AddStateOp(ops, item, workflowEvent, changed, unchanged);
            return ops;
        }, ct).ConfigureAwait(false);

        var comment = await PostCommentAsync(id, new AutopilotComment(CommentKind.Triage, AzureDevOpsWorkItemSource.TriageComment(result)), ct).ConfigureAwait(false);
        return new WriteBackResult([.. patched.Changed, .. comment.Changed], [.. patched.Unchanged, .. comment.Unchanged]);
    }

    public async Task<WriteBackResult> AddTagAsync(int id, string tag, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (tag.Contains(';', StringComparison.Ordinal))
        {
            throw new ArgumentException("A tag cannot contain ';'.", nameof(tag));
        }

        return await PatchAsync(id, (item, changed, unchanged) =>
        {
            var tags = AzureDevOpsWorkItemSource.SplitTags(item.GetString(WorkItemFields.Tags));
            if (tags.Contains(tag.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                unchanged.Add($"tag \"{tag.Trim()}\" already set");
                return [];
            }

            changed.Add($"tag \"{tag.Trim()}\"");
            return [new WorkItemPatchOp("add", "/fields/" + WorkItemFields.Tags, string.Join("; ", [.. tags, tag.Trim()]))];
        }, ct).ConfigureAwait(false);
    }

    public async Task<WriteBackResult> PostCommentAsync(int id, AutopilotComment comment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentException.ThrowIfNullOrWhiteSpace(comment.BodyHtml);
        var reference = Reference(comment);
        var existing = await client.GetCommentsAsync(id, CommentsToSearch, ct).ConfigureAwait(false);
        if (existing.Any(c => c.Html.Contains(reference, StringComparison.Ordinal)))
        {
            return new WriteBackResult([], [$"{KindName(comment.Kind)} comment already posted"]);
        }

        var body = new StringBuilder();
        if (!comment.BodyHtml.Contains(CommentMarker, StringComparison.Ordinal))
        {
            body.Append("<p><b>").Append(WebUtility.HtmlEncode(CommentMarker)).Append("</b></p>");
        }

        body.Append(comment.BodyHtml).Append("<p><small>").Append(WebUtility.HtmlEncode(reference)).Append("</small></p>");
        await client.AddCommentAsync(id, body.ToString(), ct).ConfigureAwait(false);
        return new WriteBackResult([$"{KindName(comment.Kind)} comment"], []);
    }

    public async Task<WriteBackResult> TransitionAsync(int id, WorkflowEvent workflowEvent, CancellationToken ct)
    {
        if (!settings.StateMap.ContainsKey(workflowEvent))
        {
            return new WriteBackResult([], [$"no state is mapped for \"{WorkflowEvents.Name(workflowEvent)}\""]);
        }

        return await PatchAsync(id, (item, changed, unchanged) =>
        {
            var ops = new List<WorkItemPatchOp>();
            AddStateOp(ops, item, workflowEvent, changed, unchanged);
            return ops;
        }, ct).ConfigureAwait(false);
    }

    public async Task<WriteBackResult> AssignAsync(int id, string? assignee, CancellationToken ct)
    {
        var wanted = assignee?.Trim() ?? string.Empty;
        return await PatchAsync(id, (item, changed, unchanged) =>
        {
            var names = IdentityNames(item);
            var same = wanted.Length == 0
                ? names.Count == 0
                : names.Any(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
            if (same)
            {
                unchanged.Add(wanted.Length == 0 ? "already unassigned" : "already assigned");
                return [];
            }

            changed.Add(wanted.Length == 0 ? "assignee cleared" : "assignee");
            return [new WorkItemPatchOp("add", "/fields/" + WorkItemFields.AssignedTo, wanted)];
        }, ct).ConfigureAwait(false);
    }

    public async Task<WriteBackResult> LinkPullRequestAsync(int id, string repositoryId, int pullRequestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        var repository = await client.GetRepositoryAsync(repositoryId, ct).ConfigureAwait(false);
        var projectId = string.IsNullOrEmpty(repository.ProjectId) ? (await client.GetProjectAsync(ct).ConfigureAwait(false)).Id : repository.ProjectId;
        var target = $"{projectId}/{repository.Id}/{pullRequestId.ToString(CultureInfo.InvariantCulture)}";
        var relations = await client.GetRelationsAsync(id, ct).ConfigureAwait(false);
        if (relations.Any(r => r.Rel == "ArtifactLink"
            && Uri.UnescapeDataString(r.Url).Contains("/PullRequestId/" + target, StringComparison.OrdinalIgnoreCase)))
        {
            return new WriteBackResult([], ["pull request already linked"]);
        }

        var url = "vstfs:///Git/PullRequestId/" + Uri.EscapeDataString(projectId) + "%2F" + Uri.EscapeDataString(repository.Id) + "%2F" + pullRequestId.ToString(CultureInfo.InvariantCulture);
        var op = new WorkItemPatchOp("add", "/relations/-", new { rel = "ArtifactLink", url, attributes = new { name = "Pull Request" } });
        await client.UpdateWorkItemAsync(id, null, [op], false, ct).ConfigureAwait(false);
        return new WriteBackResult(["pull request link"], []);
    }

    // ---------- Comment text builders ----------

    /// <summary>The comment for questions a person has to answer before an agent can work on the item.</summary>
    public static AutopilotComment NeedsHumanComment(IReadOnlyList<string> questions, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var text = new StringBuilder();
        text.Append("<p><b>").Append(Encode(CommentMarker)).Append("</b>: a person needs to answer before an agent works on this item</p><ol>");
        foreach (var question in questions)
        {
            text.Append("<li>").Append(Encode(question)).Append("</li>");
        }

        text.Append("</ol>");
        return new AutopilotComment(CommentKind.NeedsHuman, text.ToString(), key);
    }

    /// <summary>The comment that tells what a run did. <paramref name="runId"/> keeps one comment per run.</summary>
    public static AutopilotComment RunSummaryComment(string runId, string outcome, IReadOnlyList<string> lines, string? pullRequestUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        var text = new StringBuilder();
        text.Append("<p><b>").Append(Encode(CommentMarker)).Append("</b>: run summary, <b>").Append(Encode(outcome)).Append("</b></p>");
        if (lines.Count > 0)
        {
            text.Append("<ul>");
            foreach (var line in lines)
            {
                text.Append("<li>").Append(Encode(line)).Append("</li>");
            }

            text.Append("</ul>");
        }

        if (pullRequestUrl is not null)
        {
            text.Append("<p>Draft pull request: <a href=\"").Append(Encode(pullRequestUrl)).Append("\">").Append(Encode(pullRequestUrl)).Append("</a></p>");
        }

        return new AutopilotComment(CommentKind.RunSummary, text.ToString(), runId);
    }

    /// <summary>The comment that announces the draft pull request.</summary>
    public static AutopilotComment PullRequestComment(int pullRequestId, string url) =>
        new(CommentKind.PullRequest,
            $"<p><b>{Encode(CommentMarker)}</b>: opened draft pull request <a href=\"{Encode(url)}\">!{pullRequestId.ToString(CultureInfo.InvariantCulture)}</a>. A person reviews and merges it.</p>",
            pullRequestId.ToString(CultureInfo.InvariantCulture));

    // ---------- Helpers ----------

    internal static string Reference(AutopilotComment comment)
    {
        var key = comment.Key is null
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(comment.BodyHtml)))[..12].ToLowerInvariant()
            : new string([.. comment.Key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        return $"ref:{KindName(comment.Kind)}:{key}";
    }

    private static string KindName(CommentKind kind) => kind switch
    {
        CommentKind.Triage => "triage",
        CommentKind.NeedsHuman => "needs-human",
        CommentKind.RunSummary => "run-summary",
        _ => "pull-request",
    };

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private void AddStateOp(List<WorkItemPatchOp> ops, AdoWorkItem item, WorkflowEvent workflowEvent, List<string> changed, List<string> unchanged)
    {
        if (!settings.StateMap.TryGetValue(workflowEvent, out var target))
        {
            return;
        }

        var state = item.GetString(WorkItemFields.State);
        if (string.Equals(state, target, StringComparison.OrdinalIgnoreCase))
        {
            unchanged.Add($"state already \"{target}\"");
        }
        else if (state is not null && FinishedStates.Contains(state, StringComparer.OrdinalIgnoreCase))
        {
            unchanged.Add($"state \"{state}\" is finished, left alone");
        }
        else
        {
            ops.Add(new WorkItemPatchOp("add", "/fields/" + WorkItemFields.State, target));
            changed.Add($"state to \"{target}\"");
        }
    }

    private static List<string> IdentityNames(AdoWorkItem item)
    {
        var names = new List<string>();
        if (item.Fields.TryGetValue(WorkItemFields.AssignedTo, out var value))
        {
            if (value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var key in new[] { "displayName", "uniqueName" })
                {
                    if (value.TryGetProperty(key, out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String && name.GetString() is { Length: > 0 } text)
                    {
                        names.Add(text);
                    }
                }
            }
            else if (value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() is { Length: > 0 } plain)
            {
                names.Add(plain);
            }
        }

        return names;
    }

    /// <summary>
    /// Reads the item, builds the patch from what it finds and sends it guarded by the revision. When someone else
    /// changed the item in between, it reads again once and rebuilds the patch.
    /// </summary>
    private async Task<WriteBackResult> PatchAsync(int id, Func<AdoWorkItem, List<string>, List<string>, List<WorkItemPatchOp>> build, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var item = (await client.GetWorkItemsAsync([id], ct).ConfigureAwait(false)).FirstOrDefault()
                ?? throw new AzureDevOpsException($"Work item {id} was not found in project \"{settings.Project}\".", HttpStatusCode.NotFound);
            var changed = new List<string>();
            var unchanged = new List<string>();
            var ops = build(item, changed, unchanged);
            if (ops.Count == 0)
            {
                return new WriteBackResult(changed, unchanged);
            }

            try
            {
                await client.UpdateWorkItemAsync(id, item.Rev, ops, false, ct).ConfigureAwait(false);
                return new WriteBackResult(changed, unchanged);
            }
            catch (AzureDevOpsException ex) when (attempt < MaxAttempts && ex.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.BadRequest)
            {
                // The revision test failed (or another change got in first): read again and rebuild.
            }
        }
    }
}
