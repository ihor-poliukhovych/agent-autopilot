using DevAutopilot.Core;

namespace DevAutopilot.AzureDevOps;

// The module's own public service types. They are plain interfaces and records so a UI or a CLI can call
// them directly; docs/AZURE-DEVOPS.md shows how they map to the tracker-neutral interface.

/// <summary>A moment in the work of the agent at which a work item may change state (see the <c>stateMap</c> setting).</summary>
public enum WorkflowEvent
{
    /// <summary>Triage decided the item is ready for an agent.</summary>
    AgentReady,

    /// <summary>Triage decided a person must decide first.</summary>
    NeedsHuman,

    /// <summary>An agent started implementing the item.</summary>
    InProgress,

    /// <summary>A draft pull request was opened for the item.</summary>
    DraftPullRequest,
}

public static class WorkflowEvents
{
    public static IReadOnlyList<string> Names { get; } = ["agent-ready", "needs-human", "in-progress", "draft-pr"];

    public static string Name(WorkflowEvent workflowEvent) => Names[(int)workflowEvent];

    public static bool TryParse(string text, out WorkflowEvent workflowEvent)
    {
        var index = Names.ToList().FindIndex(n => string.Equals(n, text?.Trim(), StringComparison.OrdinalIgnoreCase));
        workflowEvent = index < 0 ? default : (WorkflowEvent)index;
        return index >= 0;
    }
}

// ---------- Reading ----------

/// <summary>A page of the items a query would read. <see cref="Query"/> null means the configured query.</summary>
public sealed record WorkItemPreviewRequest(string? Query = null, int Skip = 0, int Take = WorkItemPreviewRequest.DefaultTake)
{
    public const int DefaultTake = 25;
    public const int MaxTake = 100;

    /// <summary>A query never reads more than this many items for a preview.</summary>
    public const int MaxTotal = 200;

    public const int MaxQueryLength = 32000;
}

public sealed record WorkItemPreview(int Id, string Title, string Type, string? State, string? AssignedTo, IReadOnlyList<string> Tags, string Url);

/// <summary><see cref="Total"/> counts the matching items up to <see cref="WorkItemPreviewRequest.MaxTotal"/>; <see cref="Truncated"/> says there may be more.</summary>
public sealed record WorkItemPreviewPage(IReadOnlyList<WorkItemPreview> Items, int Total, int Skip, int Take, bool HasMore, bool Truncated);

public enum LinkKind
{
    Parent,
    Child,
    Related,
    PullRequest,
    Commit,
    Branch,
    Other,
}

public sealed record WorkItemLink(LinkKind Kind, string Rel, string Url, int? WorkItemId, string? Name);

public sealed record WorkItemComment(int Id, string Text, string? Author, DateTimeOffset? CreatedAt, bool WrittenByAutopilot);

/// <summary>A work item with everything the agent may need: text, tags, state, comments and links.</summary>
public sealed record WorkItemDetail(
    WorkItem Item,
    int Revision,
    string Type,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<WorkItemComment> Comments,
    IReadOnlyList<WorkItemLink> Links);

public interface IAzureDevOpsReader
{
    /// <summary>Runs the query (the configured one unless the request carries another) and returns one page of its items.</summary>
    Task<WorkItemPreviewPage> PreviewAsync(WorkItemPreviewRequest request, CancellationToken ct);

    /// <summary>Loads one work item with comments and links, or null when it does not exist.</summary>
    Task<WorkItemDetail?> LoadAsync(int id, CancellationToken ct);
}

// ---------- Writing back ----------

public enum CommentKind
{
    Triage,
    NeedsHuman,
    RunSummary,
    PullRequest,
}

/// <summary>
/// A comment the module writes. <see cref="Key"/> makes it unique: the same kind and key is posted once
/// (null key: the key is a hash of the text, so only identical text is skipped).
/// </summary>
public sealed record AutopilotComment(CommentKind Kind, string BodyHtml, string? Key = null);

/// <summary>What a write-back call did. Nothing is ever hidden: a call that changed nothing lists why in <see cref="Unchanged"/>.</summary>
public sealed record WriteBackResult(IReadOnlyList<string> Changed, IReadOnlyList<string> Unchanged)
{
    public bool ChangedAnything => Changed.Count > 0;
}

public interface IAzureDevOpsWriteback
{
    /// <summary>Sets the triage tag (replacing other tags with the prefix), moves the state if the map says so, and posts the triage comment.</summary>
    Task<WriteBackResult> RecordTriageAsync(int id, TriageResult result, CancellationToken ct);

    /// <summary>Adds a tag when it is not there yet.</summary>
    Task<WriteBackResult> AddTagAsync(int id, string tag, CancellationToken ct);

    /// <summary>Posts a comment once; a comment with the same kind and key is not posted again.</summary>
    Task<WriteBackResult> PostCommentAsync(int id, AutopilotComment comment, CancellationToken ct);

    /// <summary>Moves the work item to the state the map gives for the event. No entry, or an item already finished: nothing changes.</summary>
    Task<WriteBackResult> TransitionAsync(int id, WorkflowEvent workflowEvent, CancellationToken ct);

    /// <summary>Sets the assignee (a display name or an email address); null or empty clears it.</summary>
    Task<WriteBackResult> AssignAsync(int id, string? assignee, CancellationToken ct);

    /// <summary>Adds a link to a pull request when the item has none to it yet.</summary>
    Task<WriteBackResult> LinkPullRequestAsync(int id, string repositoryId, int pullRequestId, CancellationToken ct);
}

// ---------- Pull requests ----------

public sealed record DraftPullRequestRequest(string SourceBranch, string TargetBranch, string Title, string Description, int? WorkItemId);

/// <summary><see cref="Created"/> false means a pull request for the branch was already open and was reused.</summary>
public sealed record DraftPullRequestResult(int PullRequestId, string WebUrl, bool Created, bool IsDraft);

public interface IAzureDevOpsPullRequests
{
    /// <summary>Opens a draft pull request, or returns the open one from the same branch. Never marks anything ready.</summary>
    Task<DraftPullRequestResult> EnsureDraftAsync(DraftPullRequestRequest request, CancellationToken ct);

    /// <summary>Replaces the description (and optionally the title). Nothing else about the pull request is changed.</summary>
    Task<DraftPullRequestResult> UpdateDescriptionAsync(int pullRequestId, string description, string? title, CancellationToken ct);
}

// ---------- Connection checks ----------

public enum CheckOutcome
{
    Ok,
    Warn,
    Fail,
    Skipped,
}

/// <summary>One connection check in plain words. <see cref="Fix"/> is set when the check did not pass.</summary>
public sealed record ConnectionCheckResult(string Id, string Title, CheckOutcome Outcome, string Message, string? Fix = null);

public sealed record BranchPolicyItem(string Name, bool Enabled, bool Required, string Detail);

/// <summary>What the policies on the base branch mean for the agent, in words.</summary>
public sealed record BranchPolicySummary(string Branch, IReadOnlyList<BranchPolicyItem> Policies, bool RequiresHumanApproval, string Summary);

public interface IAzureDevOpsConnectionChecker
{
    /// <summary>Every check in order; a failed sign-in makes the later ones skip instead of fail.</summary>
    Task<IReadOnlyList<ConnectionCheckResult>> CheckAllAsync(CancellationToken ct);

    Task<ConnectionCheckResult> CheckSignInAsync(CancellationToken ct);

    /// <summary>Runs the configured query (or <paramref name="query"/>) and checks it is valid.</summary>
    Task<ConnectionCheckResult> CheckQueryAsync(string? query, CancellationToken ct);

    Task<ConnectionCheckResult> CheckWorkItemReadAsync(CancellationToken ct);

    /// <summary>Asks Azure DevOps to validate a no-change update of one work item, which saves nothing.</summary>
    Task<ConnectionCheckResult> CheckWorkItemWriteAsync(CancellationToken ct);

    Task<ConnectionCheckResult> CheckGitPermissionsAsync(CancellationToken ct);

    Task<BranchPolicySummary> GetBranchPolicySummaryAsync(CancellationToken ct);
}
