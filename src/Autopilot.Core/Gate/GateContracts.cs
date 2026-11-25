namespace Autopilot.Core.Gate;

public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
    Renamed,
}

/// <summary>
/// One file in the diff between the base commit and the agent branch. <paramref name="Binary"/> is true
/// when git counts no lines for it (numstat shows "-"), so the line limits cannot see its size.
/// </summary>
public sealed record FileChange(string Path, FileChangeKind Kind, int AddedLines, int RemovedLines, string? OldPath = null, bool Binary = false);

/// <summary>Facts the gate collected itself by running build and tests on a clean checkout.</summary>
public sealed record TestFacts(
    bool BuildSucceeded,
    bool TestsSucceeded,
    int TestCountBefore,
    int TestCountAfter,
    /// <summary>Tests newly marked as skipped by the change (for example a new Skip = "...").</summary>
    int SkipsAdded,
    string Log,
    /// <summary>False when the test counts could not be read from TRX for the base or the branch run.</summary>
    bool TestCountsKnown = true);

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
