using Autopilot.Core.Runs;
using Autopilot.Core.Tasks;

namespace Autopilot.Web;

/// <summary>The state of one dot on the step strip of a Pipeline card.</summary>
public static class DotState
{
    public const string Done = "done";
    public const string Pending = "pending";
    public const string Current = "current";
    public const string Problem = "problem";
}

/// <summary>What the dot-state function needs to know about one work item. Plain values, so it is easy to test.</summary>
public sealed record PipelineInput(
    string? TrackerState = null,
    IReadOnlyList<string>? Labels = null,
    string? TriageLabel = null,
    bool HasRun = false,
    string? RunOutcome = null,
    string? GateVerdict = null,
    string? HumanDecision = null,
    bool JobActive = false,
    bool InPool = false);

/// <summary>One dot: the stage name and its state.</summary>
public sealed record PipelineDot(string Step, string State);

/// <summary>What the Pipeline card needs: the dots, the outside-autopilot flag, the release and the action-due flag.</summary>
public sealed record PipelineCard(IReadOnlyList<PipelineDot> Dots, bool OutsideAutopilot, string? Release, bool ActionDue, string? ActionLabel, IReadOnlyList<string> Labels, IReadOnlyList<PipelineStage>? Stages = null, PipelineDetail? Detail = null);

/// <summary>One field of the detail meta line: its text and an optional link.</summary>
public sealed record PipelineMetaPart(string Text, string? Href = null);

/// <summary>The meta line, Result and Note of the detail panel. Result and Note are a dash when there is no data.</summary>
public sealed record PipelineDetail(IReadOnlyList<PipelineMetaPart> Meta, string Result, string Note, IReadOnlyList<PipelineLogGroup>? Log = null);

/// <summary>One log line. A field with no data is null; a link is set only when one is known.</summary>
public sealed record PipelineLogLine(string? Time, string? Actor, string? Step, string? State, string? Message, string? Href = null);

/// <summary>The log lines of one stage with the stage's time range (null parts when unknown).</summary>
public sealed record PipelineLogGroup(string Stage, string? From, string? To, IReadOnlyList<PipelineLogLine> Lines);

/// <summary>One sub-step of a stage with its state (<c>pending</c> when the app has no data for it).</summary>
public sealed record PipelineSubStep(string Name, string State);

/// <summary>One stage node of the detail stepper: its state follows the dot, plus its sub-steps.</summary>
public sealed record PipelineStage(string Step, string State, IReadOnlyList<PipelineSubStep> SubSteps);

public static class PipelineSteps
{
    /// <summary>The sub-steps under each stage; Start and Done have none.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SubStepNames = new Dictionary<string, string[]>
    {
        ["Qualify"] = ["Read Task", "Decide", "Comment Task"],
        ["Implement"] = ["Branch", "Red Test", "Fix", "Green Test", "Draft Pull Request", "Self-review", "Publish Pull Request"],
        ["Merge"] = ["Read Pull Request", "Check", "Attack", "Judge", "Resolve comments", "Verdict", "Merge"],
        ["Verify dev"] = ["Wait for deployment", "Test", "Comment Task"],
        ["Verify prod"] = ["Wait for deployment", "Test", "Comment Task"],
    };

    /// <summary>
    /// Maps the dots to the stage list of the detail stepper. A stage keeps its dot state. The app records no
    /// per-sub-step data yet, so every sub-step is pending.
    /// </summary>
    public static IReadOnlyList<PipelineStage> Stages(IReadOnlyList<PipelineDot> dots) =>
        Steps.Select(step => new PipelineStage(
            step,
            dots.FirstOrDefault(d => d.Step == step)?.State ?? DotState.Pending,
            (SubStepNames.TryGetValue(step, out var names) ? names : [])
                .Select(n => new PipelineSubStep(n, DotState.Pending)).ToList())).ToList();

    public static readonly string[] Steps = ["Start", "Qualify", "Implement", "Merge", "Verify dev", "Verify prod", "Done"];

    public const string ReleasePrefix = "release:";
    public const string VerifiedDev = "verified:dev";
    public const string VerifiedProd = "verified:prod";

    /// <summary>The card of one list entry; <paramref name="jobActive"/> says whether an implement job is running for it.</summary>
    public static PipelineCard CardOf(WorkItemListEntry item, bool jobActive, int eventCount = 0)
    {
        var run = item.LastRun;
        var input = new PipelineInput(
            item.State, item.Labels, item.Triage?.Label, run is not null, run?.Outcome, run?.GateVerdict,
            run?.HumanDecision?.ToString().ToLowerInvariant(), jobActive, item.InPool);
        var dots = Dots(input);
        var (due, label) = ActionDue(dots);
        return new PipelineCard(dots, IsOutsideAutopilot(input), ReleaseOf(item.Labels), due, label, item.Labels ?? [], Stages(dots), DetailOf(item, eventCount));
    }

    /// <summary>
    /// The meta line, Result and Note from data the app has. A field with no data is left out. The tracker name and a
    /// pull request link are not in the list data, so the link is the task's own URL.
    /// </summary>
    public static PipelineDetail DetailOf(WorkItemListEntry item, int eventCount)
    {
        var run = item.LastRun;
        var meta = new List<PipelineMetaPart>();
        if (!string.IsNullOrWhiteSpace(item.State)) meta.Add(new PipelineMetaPart(item.State!));
        if (!string.IsNullOrWhiteSpace(item.AssignedTo)) meta.Add(new PipelineMetaPart(item.AssignedTo!));
        if (!string.IsNullOrWhiteSpace(item.Url)) meta.Add(new PipelineMetaPart("Open task", item.Url));
        if (eventCount > 0) meta.Add(new PipelineMetaPart($"{eventCount} event{(eventCount == 1 ? "" : "s")}"));
        if (run is not null) meta.Add(new PipelineMetaPart($"last activity {run.FinishedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC"));

        var result = run is null ? "" : string.Join(" · ", new[]
        {
            run.Outcome,
            string.IsNullOrEmpty(run.GateVerdict) ? null : $"gate {run.GateVerdict}",
            run.HumanDecision is { } d ? $"person {d.ToString().ToLowerInvariant()}" : null,
        }.Where(x => !string.IsNullOrEmpty(x)));
        var note = !string.IsNullOrWhiteSpace(item.Triage?.Reason) ? item.Triage!.Reason : run?.Message ?? "";
        return new PipelineDetail(meta, result.Length > 0 ? result : "–", string.IsNullOrWhiteSpace(note) ? "–" : note, LogOf(item));
    }

    static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

    /// <summary>
    /// The log by stage, built only from the last run: Implement holds the run outcome and the gate verdict, Merge
    /// holds the person's decision. A group with no lines is left out; no run gives an empty log.
    /// </summary>
    public static IReadOnlyList<PipelineLogGroup> LogOf(WorkItemListEntry item)
    {
        var run = item.LastRun;
        if (run is null) return [];
        var groups = new List<PipelineLogGroup>();

        var implement = new List<PipelineLogLine>();
        if (!string.IsNullOrWhiteSpace(run.Outcome))
            implement.Add(new PipelineLogLine(Stamp(run.StartedAt), "agent", "Implement", run.Outcome,
                string.IsNullOrWhiteSpace(run.Message) ? null : run.Message));
        if (!string.IsNullOrWhiteSpace(run.GateVerdict))
            implement.Add(new PipelineLogLine(Stamp(run.FinishedAt), "gate", "Gate", run.GateVerdict, null));
        if (implement.Count > 0) groups.Add(new PipelineLogGroup("Implement", Stamp(run.StartedAt), Stamp(run.FinishedAt), implement));

        if (run.HumanDecision is { } d)
        {
            var at = run.HumanDecidedAt is { } t ? Stamp(t) : null;
            groups.Add(new PipelineLogGroup("Merge", at, at,
                [new PipelineLogLine(at, "person", "Decision", d.ToString().ToLowerInvariant(), null)]));
        }
        return groups;
    }

    /// <summary>
    /// An item that never went through the tool: no run and no triage. A job that is running or a pool entry
    /// also counts as going through the tool.
    /// </summary>
    public static bool IsOutsideAutopilot(PipelineInput i) =>
        !i.HasRun && string.IsNullOrEmpty(i.TriageLabel) && !i.JobActive && !i.InPool;

    /// <summary>The release of an item: the name in its <c>release:&lt;name&gt;</c> label, or null.</summary>
    public static string? ReleaseOf(IReadOnlyList<string>? labels) =>
        (labels ?? []).Where(l => l.StartsWith(ReleasePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(l => l[ReleasePrefix.Length..].Trim()).FirstOrDefault(n => n.Length > 0);

    /// <summary>
    /// Maps an item to its seven dots (Start, Qualify, Implement, Merge, Verify dev, Verify prod, Done).
    /// Each dot is <c>done</c>, <c>pending</c>, <c>current</c> or <c>problem</c>.
    /// <para>
    /// Label convention (compared without regard to case), because the tracker has no fields for these steps:
    /// <c>release:&lt;name&gt;</c> names the release the item ships in; <c>verified:dev</c> says a person checked it on
    /// the dev environment; <c>verified:prod</c> says a person checked it in production. <c>verified:prod</c>
    /// implies <c>verified:dev</c>, and either implies the item was merged.
    /// </para>
    /// <para>
    /// Rules: Start is done once the item has a triage, a run, a pool entry or a running job, otherwise current.
    /// Qualify is done for agent-ready (or an item that already ran), problem for needs-human, otherwise pending.
    /// Implement is current while a job runs or the item waits to be implemented, problem when the run failed or
    /// the gate rejected it, done once a run produced a draft. Merge is current while a draft waits for a person,
    /// problem when the person rejected it, done when merged, when the tracker state is closed, or when a
    /// verified label exists. Verify dev is current once merged and not yet labelled <c>verified:dev</c>; Verify prod
    /// follows the same way after <c>verified:dev</c>. Done is done when the tracker state is closed
    /// (done, closed, resolved, completed) or <c>verified:prod</c> is set.
    /// </para>
    /// <para>An item with no run and no triage is outside autopilot; see <see cref="IsOutsideAutopilot"/>.</para>
    /// </summary>
    public static IReadOnlyList<PipelineDot> Dots(PipelineInput i)
    {
        var labels = i.Labels ?? [];
        bool Has(string l) => labels.Any(x => string.Equals(x.Trim(), l, StringComparison.OrdinalIgnoreCase));
        var prod = Has(VerifiedProd);
        var dev = Has(VerifiedDev) || prod;
        var closed = i.TrackerState is { } s && new[] { "done", "closed", "resolved", "completed" }.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase);
        var ready = string.Equals(i.TriageLabel, "agent-ready", StringComparison.OrdinalIgnoreCase);
        var needsHuman = string.Equals(i.TriageLabel, "needs-human", StringComparison.OrdinalIgnoreCase);
        var merged = string.Equals(i.HumanDecision, "merged", StringComparison.OrdinalIgnoreCase) || closed || dev;
        var rejectedByPerson = string.Equals(i.HumanDecision, "rejected", StringComparison.OrdinalIgnoreCase) && !merged;
        var gateRejected = string.Equals(i.GateVerdict, "rejected", StringComparison.OrdinalIgnoreCase);
        var failed = string.Equals(i.RunOutcome, "failed", StringComparison.OrdinalIgnoreCase);
        var draft = i.HasRun && !gateRejected && !failed && !merged && !rejectedByPerson;

        var started = !string.IsNullOrEmpty(i.TriageLabel) || i.HasRun || i.InPool || i.JobActive || merged;
        var start = started ? DotState.Done : DotState.Current;
        var qualify = needsHuman && !i.HasRun && !merged ? DotState.Problem
            : ready || i.HasRun || merged ? DotState.Done
            : started ? DotState.Current : DotState.Pending;

        string implement;
        if (merged) implement = DotState.Done;
        else if (i.JobActive) implement = DotState.Current;
        else if (gateRejected || failed || rejectedByPerson) implement = DotState.Problem;
        else if (i.HasRun) implement = DotState.Done;
        else if (ready) implement = DotState.Current;
        else implement = DotState.Pending;

        var merge = merged ? DotState.Done : rejectedByPerson ? DotState.Problem : draft ? DotState.Current : DotState.Pending;
        var verifyDev = dev ? DotState.Done : merged ? DotState.Current : DotState.Pending;
        var verifyProd = prod ? DotState.Done : dev ? DotState.Current : DotState.Pending;
        var done = closed || prod ? DotState.Done : DotState.Pending;

        string[] states = [start, qualify, implement, merge, verifyDev, verifyProd, done];
        return Steps.Select((step, n) => new PipelineDot(step, states[n])).ToList();
    }

    /// <summary>A human step is due when a person must answer, decide, retry or verify: any problem dot, or a current Merge or Verify dot.</summary>
    public static (bool Due, string? Label) ActionDue(IReadOnlyList<PipelineDot> dots)
    {
        string StateOf(string step) => dots.First(d => d.Step == step).State;
        if (StateOf("Qualify") == DotState.Problem) return (true, "Answer");
        if (StateOf("Implement") == DotState.Problem || StateOf("Merge") == DotState.Problem) return (true, "Review");
        if (StateOf("Merge") == DotState.Current) return (true, "Review draft");
        if (StateOf("Verify dev") == DotState.Current) return (true, "Verify dev");
        if (StateOf("Verify prod") == DotState.Current) return (true, "Verify prod");
        return (false, null);
    }
}
