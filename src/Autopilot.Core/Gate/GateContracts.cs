namespace Autopilot.Core.Gate;

public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
}

/// <summary>One file in the diff between the base commit and the agent branch.</summary>
public sealed record FileChange(string Path, FileChangeKind Kind, int AddedLines, int RemovedLines, string? OldPath = null);

/// <summary>Facts the gate collected itself by running build and tests on a clean checkout.</summary>
public sealed record TestFacts(
    bool BuildSucceeded,
    bool TestsSucceeded,
    int TestCountBefore,
    int TestCountAfter,
    /// <summary>Tests newly marked as skipped by the change (for example a new Skip = "...").</summary>
    int SkipsAdded,
    string Log);

public sealed record GateInput(
    IReadOnlyList<FileChange> Changes,
    TestFacts Tests,
    Evidence Evidence);

public enum GateOutcome
{
    Passed,
    Rejected,
}

public sealed record GateFinding(string RuleId, string Message, bool HighRisk = false);

public sealed record GateDecision(GateOutcome Outcome, IReadOnlyList<GateFinding> Findings)
{
    public bool HighRisk => Findings.Any(f => f.HighRisk);
}
