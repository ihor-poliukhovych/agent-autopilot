namespace Autopilot.Core.Evals;

/// <summary>Turns and tokens of one role in one eval run, read from <c>runs.jsonl</c>.</summary>
public sealed record EvalRoleUsage(string Role, int AgentRuns, int Turns, TokenUsage Usage, IReadOnlyList<string> Models);

/// <summary>One run of one case.</summary>
public sealed record EvalRunResult(
    int Run,
    bool Passed,
    string? TriageLabel,
    EvalOutcome Outcome,
    /// <summary>Why the run failed; empty when it passed.</summary>
    IReadOnlyList<string> Reasons,
    /// <summary>The pipeline's own message about the outcome.</summary>
    string Message,
    int Turns,
    TokenUsage Usage,
    IReadOnlyList<EvalRoleUsage> UsageByRole,
    TimeSpan WallTime,
    /// <summary>True, false, or null when the hidden tests were not run (the outcome was not published).</summary>
    bool? HiddenTestsPassed,
    /// <summary>Set when the run failed because of the machine, not the agent: Claude Code not signed in, usage limit, git missing.</summary>
    string? InfrastructureError = null,
    /// <summary>The temporary repository, when it was kept with --keep.</summary>
    string? KeptFolder = null);

/// <summary>All runs of one case, with the numbers the report shows.</summary>
public sealed record EvalCaseResult(EvalCase Case, int PlannedRuns, IReadOnlyList<EvalRunResult> Runs)
{
    /// <summary>Runs that finished without an infrastructure error; only these are graded.</summary>
    public IReadOnlyList<EvalRunResult> GradedRuns => Runs.Where(r => r.InfrastructureError is null).ToList();

    public int Passes => GradedRuns.Count(r => r.Passed);

    /// <summary>Passes divided by graded runs; null when no run was graded.</summary>
    public double? PassRate => GradedRuns.Count == 0 ? null : (double)Passes / GradedRuns.Count;

    /// <summary>
    /// pass^N: every one of the N planned runs passed. Null when fewer than N runs were graded
    /// (the eval stopped early), because then it is not known.
    /// </summary>
    public bool? PassAll => GradedRuns.Count < PlannedRuns ? null : Passes == PlannedRuns;

    public double? MeanTurns => Mean(r => r.Turns);

    public double? MeanInputTokens => Mean(r => r.Usage.InputTokens);

    public double? MeanOutputTokens => Mean(r => r.Usage.OutputTokens);

    public double? MeanCacheReadTokens => Mean(r => r.Usage.CacheReadInputTokens);

    public double? MeanCacheWriteTokens => Mean(r => r.Usage.CacheCreationInputTokens);

    public double? MeanWallSeconds => Mean(r => r.WallTime.TotalSeconds);

    private double? Mean(Func<EvalRunResult, double> value) => GradedRuns.Count == 0 ? null : GradedRuns.Average(value);
}

/// <summary>Everything one <c>autopilot eval</c> produced.</summary>
public sealed record EvalResult(
    DateTimeOffset StartedAt,
    TimeSpan WallTime,
    string ToolVersion,
    string SnapshotHash,
    string SnapshotSource,
    int SnapshotFiles,
    string SnapshotCommit,
    int RunsPerCase,
    /// <summary>Model per role: what the runs reported, or the configured name, or "default".</summary>
    IReadOnlyDictionary<string, string> Models,
    IReadOnlyList<EvalCaseResult> Cases,
    /// <summary>Set when the eval stopped before all runs because of an infrastructure error.</summary>
    string? StoppedEarly)
{
    /// <summary>pass@1: the mean pass rate over the cases that have at least one graded run.</summary>
    public double? PassAt1
    {
        get
        {
            var rates = Cases.Select(c => c.PassRate).OfType<double>().ToList();
            return rates.Count == 0 ? null : rates.Average();
        }
    }

    /// <summary>Share of cases where all N runs passed, over the cases where pass^N is known.</summary>
    public double? PassAllRate
    {
        get
        {
            var known = Cases.Select(c => c.PassAll).OfType<bool>().ToList();
            return known.Count == 0 ? null : (double)known.Count(b => b) / known.Count;
        }
    }

    public int CasesAllPassed => Cases.Count(c => c.PassAll == true);

    public int TotalTurns => Cases.SelectMany(c => c.Runs).Sum(r => r.Turns);

    public TokenUsage TotalUsage => Cases.SelectMany(c => c.Runs).Aggregate(TokenUsage.Zero, (sum, r) => sum.Add(r.Usage));

    public int TotalRuns => Cases.Sum(c => c.Runs.Count);
}
