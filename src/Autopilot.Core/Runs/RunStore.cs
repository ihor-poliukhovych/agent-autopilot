using Autopilot.Core.Gate;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Runs;

public enum HumanDecision
{
    Merged,
    Rejected,
}

/// <summary>
/// One implement run. <see cref="GateVerdict"/> is "passed" or "rejected", or null when the run never
/// reached the gate. <see cref="WouldMerge"/> is true when the gate passed: the run is a draft a person
/// can merge. <see cref="HumanDecision"/> is what the person did later.
/// </summary>
public sealed record RunRecord(
    long Id,
    string ProjectKey,
    string WorkItemId,
    string? JobId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Outcome,
    string? GateVerdict,
    bool HighRisk,
    bool WouldMerge,
    HumanDecision? HumanDecision,
    DateTimeOffset? HumanDecidedAt,
    string? Branch,
    string? Message);

/// <summary>
/// Counts for trust levels. <see cref="Agreed"/> of <see cref="Decided"/>: how often the person did what the
/// gate would have done. <see cref="GateTooLenient"/> counts drafts the gate passed and the person rejected;
/// <see cref="GateTooStrict"/> counts drafts the gate rejected and the person merged anyway.
/// </summary>
public sealed record RunStats(int TotalRuns, int GatePassed, int GateRejected, int Decided, int Agreed, int GateTooLenient, int GateTooStrict)
{
    /// <summary>Agreed divided by decided, or null before any decision.</summary>
    public double? AgreementRate => Decided == 0 ? null : (double)Agreed / Decided;
}

/// <summary>Run history of a project, with the gate's verdict and the person's decision side by side.</summary>
public sealed class RunStore(Workspace.Workspace workspace, string projectKey)
{
    private const string Columns = "id, project_key, work_item_id, job_id, started_at, finished_at, outcome, gate_verdict, high_risk, would_merge, human_decision, human_decided_at, branch, message";

    public RunRecord Record(ImplementResult result, DateTimeOffset startedAt, string? jobId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var verdict = result.Decision is null ? null : result.Decision.Outcome == GateOutcome.Passed ? "passed" : "rejected";
        using var connection = workspace.Open();
        Db.Execute(connection,
            "INSERT INTO runs (project_key, work_item_id, job_id, started_at, finished_at, outcome, gate_verdict, high_risk, would_merge, branch, message) " +
            "VALUES ($k, $i, $j, $s, $f, $o, $v, $h, $w, $b, $m)",
            ("$k", projectKey), ("$i", result.WorkItemId), ("$j", jobId), ("$s", Db.Stamp(startedAt)), ("$f", Db.Stamp(DateTimeOffset.UtcNow)),
            ("$o", result.Outcome.ToString().ToLowerInvariant()), ("$v", verdict), ("$h", result.Decision?.HighRisk == true ? 1 : 0),
            ("$w", verdict == "passed" ? 1 : 0), ("$b", result.BranchName), ("$m", result.Message));
        using var last = Db.Command(connection, "SELECT last_insert_rowid()");
        return Get(Convert.ToInt64(last.ExecuteScalar()))!;
    }

    public RunRecord? Get(long id)
    {
        using var connection = workspace.Open();
        return Db.Query(connection, Read, $"SELECT {Columns} FROM runs WHERE project_key = $k AND id = $i", ("$k", projectKey), ("$i", id)).FirstOrDefault();
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RunRecord> List(string? workItemId = null, int? limit = null)
    {
        using var connection = workspace.Open();
        return Db.Query(connection, Read,
            $"SELECT {Columns} FROM runs WHERE project_key = $k AND ($i IS NULL OR work_item_id = $i) ORDER BY id DESC LIMIT $l",
            ("$k", projectKey), ("$i", workItemId), ("$l", limit ?? -1));
    }

    /// <summary>The newest run of each work item, by work item id.</summary>
    public IReadOnlyDictionary<string, RunRecord> LastRuns()
    {
        using var connection = workspace.Open();
        var rows = Db.Query(connection, Read,
            $"SELECT {Columns} FROM runs WHERE project_key = $k AND id IN (SELECT MAX(id) FROM runs WHERE project_key = $k GROUP BY work_item_id)",
            ("$k", projectKey));
        return rows.ToDictionary(r => r.WorkItemId, StringComparer.Ordinal);
    }

    /// <summary>Records what a person did with the run's draft. Returns false when there is no such run.</summary>
    public bool RecordDecision(long runId, HumanDecision decision)
    {
        using var connection = workspace.Open();
        return Db.Execute(connection, "UPDATE runs SET human_decision = $d, human_decided_at = $t WHERE project_key = $k AND id = $i",
            ("$d", decision.ToString().ToLowerInvariant()), ("$t", Db.Stamp(DateTimeOffset.UtcNow)), ("$k", projectKey), ("$i", runId)) > 0;
    }

    /// <summary>A run <i>agrees</i> when the gate would have merged it and the person merged it, or the gate would not and the person did not.</summary>
    public RunStats Stats()
    {
        var runs = List();
        var decided = runs.Where(r => r.HumanDecision is not null).ToList();
        return new RunStats(
            runs.Count,
            runs.Count(r => r.GateVerdict == "passed"),
            runs.Count(r => r.GateVerdict == "rejected"),
            decided.Count,
            decided.Count(r => r.WouldMerge == (r.HumanDecision == Runs.HumanDecision.Merged)),
            decided.Count(r => r.WouldMerge && r.HumanDecision == Runs.HumanDecision.Rejected),
            decided.Count(r => !r.WouldMerge && r.HumanDecision == Runs.HumanDecision.Merged));
    }

    private static RunRecord Read(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), Db.StringOrNull(r, 3), Db.ParseStamp(r.GetString(4)), Db.ParseStamp(r.GetString(5)),
        r.GetString(6), Db.StringOrNull(r, 7), r.GetInt32(8) != 0, r.GetInt32(9) != 0,
        Db.StringOrNull(r, 10) is { } d ? Enum.Parse<HumanDecision>(d, ignoreCase: true) : null,
        Db.ParseStampOrNull(r, 11), Db.StringOrNull(r, 12), Db.StringOrNull(r, 13));
}
