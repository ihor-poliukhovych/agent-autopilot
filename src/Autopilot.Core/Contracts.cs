using System.Text.Json;
using Autopilot.Core.Config;

namespace Autopilot.Core;

// ---------- Work items ----------

/// <summary>A task. Url, State and AssignedTo are filled by sources that have them (a tracker), null otherwise.</summary>
public sealed record WorkItem(string Id, string Title, string Body, IReadOnlyList<string> Labels, string? Url = null, string? State = null, string? AssignedTo = null);

public enum TriageLabel
{
    AgentReady,
    NeedsHuman,
}

public enum RiskLevel
{
    Low,
    Medium,
    High,
}

public sealed record TriageResult(
    TriageLabel Label,
    string Reason,
    IReadOnlyList<string> AcceptanceCriteria,
    RiskLevel Risk);

public interface IWorkItemSource
{
    Task<IReadOnlyList<WorkItem>> ListOpenAsync(CancellationToken ct);
    Task<WorkItem?> GetAsync(string id, CancellationToken ct);

    /// <summary>Stores the triage outcome where a person can see it (a file, a tag, a comment).</summary>
    Task RecordTriageAsync(string id, TriageResult result, CancellationToken ct);
}

// ---------- Agent runs ----------

public sealed record AgentRunRequest(
    AgentRole Role,
    string WorkingDirectory,
    string SystemPrompt,
    string Prompt,
    RoleConfig RoleConfig,
    IReadOnlyList<string> WritablePaths,
    /// <summary>JSON schema text for structured output, or null for free text.</summary>
    string? OutputJsonSchema,
    /// <summary>Folder for this run's files: policy, settings, logs. Must be outside the worktree.</summary>
    string RunDirectory,
    /// <summary>
    /// Extra glob patterns the agent may never write, on top of <see cref="Gate.MergeGate.ProtectedPaths"/>.
    /// The pipeline passes the config's risk paths here, so build files, settings and auth code are read-only.
    /// </summary>
    IReadOnlyList<string>? ProtectedPaths = null,
    /// <summary>
    /// MCP servers for this run, by name. The runner writes them to the run's mcp.json (used with
    /// --strict-mcp-config). Null or empty means no MCP server, and every <c>mcp__*</c> tool is denied.
    /// </summary>
    IReadOnlyDictionary<string, McpServerSpec>? McpServers = null,
    /// <summary>
    /// The configured build and test command lines the hook allows exactly as written (see
    /// <see cref="Policy.ToolPolicy.ConfiguredCommands"/>). The pipeline sets them for the implementer only.
    /// </summary>
    IReadOnlyList<string>? ConfiguredCommands = null);

/// <summary>A stdio MCP server: the program and its arguments, one argv entry each.</summary>
public sealed record McpServerSpec(string Command, IReadOnlyList<string> Args);

public sealed record TokenUsage(long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheCreationInputTokens)
{
    public static TokenUsage Zero { get; } = new(0, 0, 0, 0);

    public TokenUsage Add(TokenUsage other) => new(
        InputTokens + other.InputTokens,
        OutputTokens + other.OutputTokens,
        CacheReadInputTokens + other.CacheReadInputTokens,
        CacheCreationInputTokens + other.CacheCreationInputTokens);
}

public sealed record AgentRunResult(
    bool Succeeded,
    string ResultText,
    JsonElement? StructuredOutput,
    int Turns,
    TokenUsage Usage,
    TimeSpan Duration,
    string? Model,
    string? Error);

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct);
}

// ---------- Publishing ----------

public sealed record PublishRequest(
    WorkItem WorkItem,
    string BranchName,
    string Diff,
    Gate.GateDecision Decision,
    Evidence Evidence,
    /// <summary>The decision with the facts behind it. Null when only the decision is known.</summary>
    Local.GateReport? Report = null,
    /// <summary>The pull request title (the commit subject). Null keeps the publisher's own wording.</summary>
    string? Title = null);

public sealed record PublishResult(bool Succeeded, string Location, string? Error);

public interface IPublisher
{
    Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct);
}

// ---------- Evidence ----------

public enum ReviewVerdict
{
    Approve,
    RequestChanges,
}

public sealed record ReviewResult(ReviewVerdict Verdict, IReadOnlyList<string> Findings);

public sealed record SelfReview(string Summary, IReadOnlyList<string> TestsThatProveIt, IReadOnlyList<string> Doubts);

public sealed record Evidence(
    string WorkItemId,
    TriageResult? Triage,
    SelfReview? SelfReview,
    ReviewResult? Review);

// ---------- Diagnostics ----------

public sealed record DiagnosticEvent(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Exception,
    string? Route,
    int? StatusCode,
    double? DurationMs);

public interface IDiagnosticsSource
{
    IAsyncEnumerable<DiagnosticEvent> ReadAsync(CancellationToken ct);
}
