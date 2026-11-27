using Autopilot.Core.Gate;

namespace Autopilot.Core.Pipeline;

public sealed record TriageItemResult(string WorkItemId, string Title, TriageResult? Triage, string? Error)
{
    public bool Succeeded => Triage is not null;
}

public enum ImplementOutcome
{
    /// <summary>The gate passed and the publisher made a draft. Waiting for a person.</summary>
    Published,

    /// <summary>The gate or the harness rejected the change. The branch is kept for a person.</summary>
    Rejected,

    /// <summary>The item has no triage, or triage said needs-human.</summary>
    NotReady,

    NotFound,

    /// <summary>Something broke (git, an agent run, the publisher). The message says what.</summary>
    Failed,
}

public sealed record ImplementResult(
    string WorkItemId,
    ImplementOutcome Outcome,
    string Message,
    string? BranchName = null,
    string? Location = null,
    GateDecision? Decision = null);

public sealed record RunSummary(
    IReadOnlyList<TriageItemResult> Triage,
    IReadOnlyList<ImplementResult> Implemented,
    IReadOnlyList<string> Skipped);
