using System.Globalization;
using System.Text.Json;
using Autopilot.Core;

namespace Autopilot.AzureDevOps;

/// <summary>Reads work items: a preview of what a query would read (for a wizard) and the full detail of one item.</summary>
public sealed class AzureDevOpsReader(AzureDevOpsClient client, AzureDevOpsSettings settings) : IAzureDevOpsReader
{
    /// <summary>The most comments loaded for one item.</summary>
    public const int MaxComments = 100;

    public async Task<WorkItemPreviewPage> PreviewAsync(WorkItemPreviewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = request.Query ?? settings.Query;
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("The query is empty. Write a WIQL query such as SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project.", nameof(request));
        }

        if (query.Length > WorkItemPreviewRequest.MaxQueryLength)
        {
            throw new ArgumentException($"The query is longer than {WorkItemPreviewRequest.MaxQueryLength} characters, the most Azure DevOps accepts.", nameof(request));
        }

        var skip = Math.Max(0, request.Skip);
        var take = Math.Clamp(request.Take, 1, WorkItemPreviewRequest.MaxTake);

        // Ask for one more than the cap so we know whether the cap cut the list.
        var ids = await client.QueryWorkItemIdsAsync(query, WorkItemPreviewRequest.MaxTotal + 1, ct).ConfigureAwait(false);
        var truncated = ids.Count > WorkItemPreviewRequest.MaxTotal;
        if (truncated)
        {
            ids = ids.Take(WorkItemPreviewRequest.MaxTotal).ToList();
        }

        var pageIds = ids.Skip(skip).Take(take).ToList();
        var items = pageIds.Count == 0
            ? []
            : (await client.GetWorkItemsAsync(pageIds, ct).ConfigureAwait(false)).Select(ToPreview).ToList();
        return new WorkItemPreviewPage(items, ids.Count, skip, take, skip + take < ids.Count, truncated);
    }

    public async Task<WorkItemDetail?> LoadAsync(int id, CancellationToken ct)
    {
        var item = (await client.GetWorkItemsAsync([id], ct).ConfigureAwait(false)).FirstOrDefault();
        if (item is null)
        {
            return null;
        }

        var comments = await client.GetCommentsAsync(id, MaxComments, ct).ConfigureAwait(false);
        var relations = await client.GetRelationsAsync(id, ct).ConfigureAwait(false);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in item.Fields)
        {
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Object when value.TryGetProperty("displayName", out var display) => display.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                _ => null,
            };
            if (text is not null)
            {
                fields[name] = text;
            }
        }

        return new WorkItemDetail(
            AzureDevOpsWorkItemSource.ToWorkItem(item, settings),
            item.Rev,
            item.GetString(WorkItemFields.WorkItemType) ?? "Work item",
            fields,
            comments.Select(c => new WorkItemComment(
                c.Id,
                HtmlText.ToPlainText(c.Html),
                c.Author,
                c.CreatedAt,
                c.Html.Contains(AzureDevOpsWriteback.CommentMarker, StringComparison.Ordinal))).ToList(),
            relations.Select(ToLink).ToList());
    }

    private WorkItemPreview ToPreview(AdoWorkItem item) => new(
        item.Id,
        item.GetString(WorkItemFields.Title) ?? $"Work item {item.Id}",
        item.GetString(WorkItemFields.WorkItemType) ?? "Work item",
        item.GetString(WorkItemFields.State),
        item.GetIdentityName(WorkItemFields.AssignedTo),
        AzureDevOpsWorkItemSource.SplitTags(item.GetString(WorkItemFields.Tags)),
        AzureDevOpsWorkItemSource.WebUrl(settings, item.Id));

    internal static WorkItemLink ToLink(AdoRelation relation)
    {
        var rel = relation.Rel;
        var kind = rel switch
        {
            "System.LinkTypes.Hierarchy-Reverse" => LinkKind.Parent,
            "System.LinkTypes.Hierarchy-Forward" => LinkKind.Child,
            "System.LinkTypes.Related" => LinkKind.Related,
            "ArtifactLink" when relation.Url.Contains("/PullRequestId/", StringComparison.OrdinalIgnoreCase) => LinkKind.PullRequest,
            "ArtifactLink" when relation.Url.Contains("/Commit/", StringComparison.OrdinalIgnoreCase) => LinkKind.Commit,
            "ArtifactLink" when relation.Url.Contains("/Ref/", StringComparison.OrdinalIgnoreCase) => LinkKind.Branch,
            _ => LinkKind.Other,
        };

        int? workItemId = null;
        if (kind is LinkKind.Parent or LinkKind.Child or LinkKind.Related || rel.StartsWith("System.LinkTypes.", StringComparison.Ordinal))
        {
            var last = relation.Url.TrimEnd('/').Split('/')[^1];
            if (int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                workItemId = number;
            }
        }

        return new WorkItemLink(kind, rel, relation.Url, workItemId, relation.Name);
    }
}
