using System.Text.Json;
using Autopilot.Core.Config;

namespace Autopilot.Core;

// ---------- Work items ----------

public sealed record WorkItem(string Id, string Title, string Body, IReadOnlyList<string> Labels);

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
    string RunDirectory);

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
    Evidence Evidence);

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
