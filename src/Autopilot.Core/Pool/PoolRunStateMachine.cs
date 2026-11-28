namespace Autopilot.Core.Pool;

/// <summary>The status of a pool run.</summary>
public enum PoolRunStatus
{
    Planned,
    Queued,
    Running,
    Reviewing,
    ReadyForPerson,
    NeedsPerson,
    Failed,
    Cancelled,
    Done,
}

/// <summary>The sub-status of a run. <see cref="None"/> for statuses that have none.</summary>
public enum PoolRunSubStatus
{
    None,

    // Running
    Working,
    WaitingForSlot,
    PausedInfrastructure,
    PausedBudget,
    ParkedTasksWaiting,

    // Reviewing
    PrVerify,
    Attack,
    Judge,
    Fixing,
    ReVerify,
}

/// <summary>A pool run state: a status and, for Running and Reviewing, a sub-status.</summary>
public readonly record struct PoolRunState(PoolRunStatus Status, PoolRunSubStatus Sub = PoolRunSubStatus.None)
{
    public static readonly PoolRunState Planned = new(PoolRunStatus.Planned);
    public static readonly PoolRunState Queued = new(PoolRunStatus.Queued);
    public static readonly PoolRunState ReadyForPerson = new(PoolRunStatus.ReadyForPerson);
    public static readonly PoolRunState NeedsPerson = new(PoolRunStatus.NeedsPerson);
    public static readonly PoolRunState Failed = new(PoolRunStatus.Failed);
    public static readonly PoolRunState Cancelled = new(PoolRunStatus.Cancelled);
    public static readonly PoolRunState Done = new(PoolRunStatus.Done);

    public static PoolRunState Running(PoolRunSubStatus sub) => new(PoolRunStatus.Running, sub);

    public static PoolRunState Reviewing(PoolRunSubStatus sub) => new(PoolRunStatus.Reviewing, sub);

    /// <summary>True if the state is valid: a sub-status only where it belongs, and always there for Running and Reviewing.</summary>
    public bool IsValid => Status switch
    {
        PoolRunStatus.Running => Sub is PoolRunSubStatus.Working or PoolRunSubStatus.WaitingForSlot
            or PoolRunSubStatus.PausedInfrastructure or PoolRunSubStatus.PausedBudget or PoolRunSubStatus.ParkedTasksWaiting,
        PoolRunStatus.Reviewing => Sub is PoolRunSubStatus.PrVerify or PoolRunSubStatus.Attack or PoolRunSubStatus.Judge
            or PoolRunSubStatus.Fixing or PoolRunSubStatus.ReVerify,
        _ => Sub == PoolRunSubStatus.None,
    };

    /// <summary>True for Done, Cancelled and Failed.</summary>
    public bool IsTerminal => Status is PoolRunStatus.Done or PoolRunStatus.Cancelled or PoolRunStatus.Failed;
}

/// <summary>Something that happens to a pool run.</summary>
public enum PoolRunEvent
{
    Enqueue,
    Start,
    SlotUnavailable,
    SlotGranted,
    PauseInfrastructure,
    PauseBudget,
    Resume,
    ParkTasks,
    ParkedTasksResolved,
    WorkFinished,
    VerifyPassed,
    VerifyFailed,
    AttackPassed,
    AttackFoundIssues,
    JudgePassed,
    JudgeFoundIssues,
    FixDone,
    ReVerifyPassed,
    ReVerifyFailed,
    NeedPerson,
    PersonAnswered,
    PersonApproved,
    PersonRejected,
    Fail,
    Cancel,
    Retry,
}

/// <summary>The result of a transition: allowed with the new state, or refused with a reason.</summary>
public readonly record struct PoolRunTransition(bool Allowed, PoolRunState State, string? RefusedReason)
{
    public static PoolRunTransition Allow(PoolRunState next) => new(true, next, null);

    public static PoolRunTransition Refuse(PoolRunState current, string reason) => new(false, current, reason);
}

/// <summary>
/// The transition table of a pool run. Pure: no I/O, no clock, no randomness, no mutation of anything but its own
/// one-time table. The table mirrors the flow of the pool code: a run is queued, runs (waiting for a slot, paused when the
/// infrastructure or the budget stops, parking tasks), is reviewed (verify, attack, judge, fix, re-verify) and ends
/// ready for a person, who closes it as done.
/// </summary>
public static class PoolRunStateMachine
{
    private static readonly PoolRunState Working = PoolRunState.Running(PoolRunSubStatus.Working);
    private static readonly PoolRunState WaitingForSlot = PoolRunState.Running(PoolRunSubStatus.WaitingForSlot);
    private static readonly PoolRunState PausedInfra = PoolRunState.Running(PoolRunSubStatus.PausedInfrastructure);
    private static readonly PoolRunState PausedBudget = PoolRunState.Running(PoolRunSubStatus.PausedBudget);
    private static readonly PoolRunState ParkedWaiting = PoolRunState.Running(PoolRunSubStatus.ParkedTasksWaiting);
    private static readonly PoolRunState PrVerify = PoolRunState.Reviewing(PoolRunSubStatus.PrVerify);
    private static readonly PoolRunState Attack = PoolRunState.Reviewing(PoolRunSubStatus.Attack);
    private static readonly PoolRunState Judge = PoolRunState.Reviewing(PoolRunSubStatus.Judge);
    private static readonly PoolRunState Fixing = PoolRunState.Reviewing(PoolRunSubStatus.Fixing);
    private static readonly PoolRunState ReVerify = PoolRunState.Reviewing(PoolRunSubStatus.ReVerify);

    /// <summary>Every state that can be left by Cancel, Fail or NeedPerson, in the order of the flow.</summary>
    private static readonly PoolRunState[] RunningStates = [Working, WaitingForSlot, PausedInfra, PausedBudget, ParkedWaiting];

    private static readonly PoolRunState[] ReviewingStates = [PrVerify, Attack, Judge, Fixing, ReVerify];

    /// <summary>All valid states, for callers that need to enumerate them.</summary>
    public static IReadOnlyList<PoolRunState> AllStates { get; } =
    [
        PoolRunState.Planned, PoolRunState.Queued,
        Working, WaitingForSlot, PausedInfra, PausedBudget, ParkedWaiting,
        PrVerify, Attack, Judge, Fixing, ReVerify,
        PoolRunState.ReadyForPerson, PoolRunState.NeedsPerson,
        PoolRunState.Failed, PoolRunState.Cancelled, PoolRunState.Done,
    ];

    /// <summary>The one explicit table of allowed transitions: (from, event) to the new state.</summary>
    public static IReadOnlyDictionary<(PoolRunState From, PoolRunEvent Event), PoolRunState> Table { get; } = BuildTable();

    /// <summary>Returns the result of <paramref name="evt"/> in <paramref name="state"/>: allowed with the new state, or refused.</summary>
    public static PoolRunTransition Transition(PoolRunState state, PoolRunEvent evt)
    {
        if (!state.IsValid)
        {
            return PoolRunTransition.Refuse(state, $"{state.Status}/{state.Sub} is not a valid run state.");
        }

        return Table.TryGetValue((state, evt), out var next)
            ? PoolRunTransition.Allow(next)
            : PoolRunTransition.Refuse(state, $"{evt} is not allowed in {state.Status}" + (state.Sub == PoolRunSubStatus.None ? "." : $" ({state.Sub})."));
    }

    private static IReadOnlyDictionary<(PoolRunState, PoolRunEvent), PoolRunState> BuildTable()
    {
        var t = new Dictionary<(PoolRunState, PoolRunEvent), PoolRunState>
        {
            // Planned and queued.
            [(PoolRunState.Planned, PoolRunEvent.Enqueue)] = PoolRunState.Queued,
            [(PoolRunState.Planned, PoolRunEvent.Cancel)] = PoolRunState.Cancelled,
            [(PoolRunState.Queued, PoolRunEvent.Start)] = Working,
            [(PoolRunState.Queued, PoolRunEvent.SlotUnavailable)] = WaitingForSlot,
            [(PoolRunState.Queued, PoolRunEvent.Cancel)] = PoolRunState.Cancelled,

            // Running.
            [(Working, PoolRunEvent.SlotUnavailable)] = WaitingForSlot,
            [(WaitingForSlot, PoolRunEvent.SlotGranted)] = Working,
            [(Working, PoolRunEvent.PauseInfrastructure)] = PausedInfra,
            [(WaitingForSlot, PoolRunEvent.PauseInfrastructure)] = PausedInfra,
            [(ParkedWaiting, PoolRunEvent.PauseInfrastructure)] = PausedInfra,
            [(Working, PoolRunEvent.PauseBudget)] = PausedBudget,
            [(WaitingForSlot, PoolRunEvent.PauseBudget)] = PausedBudget,
            [(ParkedWaiting, PoolRunEvent.PauseBudget)] = PausedBudget,
            [(PausedInfra, PoolRunEvent.Resume)] = Working,
            [(PausedBudget, PoolRunEvent.Resume)] = Working,
            [(Working, PoolRunEvent.ParkTasks)] = ParkedWaiting,
            [(ParkedWaiting, PoolRunEvent.ParkedTasksResolved)] = Working,
            [(Working, PoolRunEvent.WorkFinished)] = PrVerify,

            // Reviewing.
            [(PrVerify, PoolRunEvent.VerifyPassed)] = Attack,
            [(PrVerify, PoolRunEvent.VerifyFailed)] = Fixing,
            [(Attack, PoolRunEvent.AttackPassed)] = Judge,
            [(Attack, PoolRunEvent.AttackFoundIssues)] = Fixing,
            [(Judge, PoolRunEvent.JudgePassed)] = PoolRunState.ReadyForPerson,
            [(Judge, PoolRunEvent.JudgeFoundIssues)] = Fixing,
            [(Fixing, PoolRunEvent.FixDone)] = ReVerify,
            [(ReVerify, PoolRunEvent.ReVerifyPassed)] = Attack,
            [(ReVerify, PoolRunEvent.ReVerifyFailed)] = Fixing,

            // A person.
            [(PoolRunState.NeedsPerson, PoolRunEvent.PersonAnswered)] = PoolRunState.Queued,
            [(PoolRunState.NeedsPerson, PoolRunEvent.Cancel)] = PoolRunState.Cancelled,
            [(PoolRunState.NeedsPerson, PoolRunEvent.Fail)] = PoolRunState.Failed,
            [(PoolRunState.ReadyForPerson, PoolRunEvent.PersonApproved)] = PoolRunState.Done,
            [(PoolRunState.ReadyForPerson, PoolRunEvent.PersonRejected)] = Fixing,
            [(PoolRunState.ReadyForPerson, PoolRunEvent.NeedPerson)] = PoolRunState.NeedsPerson,
            [(PoolRunState.ReadyForPerson, PoolRunEvent.Cancel)] = PoolRunState.Cancelled,

            // Failed is terminal, except that a person may retry it. Done and Cancelled accept nothing.
            [(PoolRunState.Failed, PoolRunEvent.Retry)] = PoolRunState.Queued,
        };

        foreach (var s in RunningStates.Concat(ReviewingStates))
        {
            t[(s, PoolRunEvent.NeedPerson)] = PoolRunState.NeedsPerson;
            t[(s, PoolRunEvent.Fail)] = PoolRunState.Failed;
            t[(s, PoolRunEvent.Cancel)] = PoolRunState.Cancelled;
        }

        return t;
    }
}
