using Autopilot.Core.Pool;
using E = Autopilot.Core.Pool.PoolRunEvent;
using S = Autopilot.Core.Pool.PoolRunState;
using Sub = Autopilot.Core.Pool.PoolRunSubStatus;

namespace Autopilot.Core.Tests;

public sealed class PoolRunStateMachineTests
{
    private static S Run(Sub sub) => S.Running(sub);

    private static S Rev(Sub sub) => S.Reviewing(sub);

    /// <summary>Written out by hand, independent of the table in the code.</summary>
    private static readonly (S From, E Event, S To)[] Allowed =
    [
        (S.Planned, E.Enqueue, S.Queued),
        (S.Planned, E.Cancel, S.Cancelled),
        (S.Queued, E.Start, Run(Sub.Working)),
        (S.Queued, E.SlotUnavailable, Run(Sub.WaitingForSlot)),
        (S.Queued, E.Cancel, S.Cancelled),

        (Run(Sub.Working), E.SlotUnavailable, Run(Sub.WaitingForSlot)),
        (Run(Sub.WaitingForSlot), E.SlotGranted, Run(Sub.Working)),
        (Run(Sub.Working), E.PauseInfrastructure, Run(Sub.PausedInfrastructure)),
        (Run(Sub.WaitingForSlot), E.PauseInfrastructure, Run(Sub.PausedInfrastructure)),
        (Run(Sub.ParkedTasksWaiting), E.PauseInfrastructure, Run(Sub.PausedInfrastructure)),
        (Run(Sub.Working), E.PauseBudget, Run(Sub.PausedBudget)),
        (Run(Sub.WaitingForSlot), E.PauseBudget, Run(Sub.PausedBudget)),
        (Run(Sub.ParkedTasksWaiting), E.PauseBudget, Run(Sub.PausedBudget)),
        (Run(Sub.PausedInfrastructure), E.Resume, Run(Sub.Working)),
        (Run(Sub.PausedBudget), E.Resume, Run(Sub.Working)),
        (Run(Sub.Working), E.ParkTasks, Run(Sub.ParkedTasksWaiting)),
        (Run(Sub.ParkedTasksWaiting), E.ParkedTasksResolved, Run(Sub.Working)),
        (Run(Sub.Working), E.WorkFinished, Rev(Sub.PrVerify)),

        (Rev(Sub.PrVerify), E.VerifyPassed, Rev(Sub.Attack)),
        (Rev(Sub.PrVerify), E.VerifyFailed, Rev(Sub.Fixing)),
        (Rev(Sub.Attack), E.AttackPassed, Rev(Sub.Judge)),
        (Rev(Sub.Attack), E.AttackFoundIssues, Rev(Sub.Fixing)),
        (Rev(Sub.Judge), E.JudgePassed, S.ReadyForPerson),
        (Rev(Sub.Judge), E.JudgeFoundIssues, Rev(Sub.Fixing)),
        (Rev(Sub.Fixing), E.FixDone, Rev(Sub.ReVerify)),
        (Rev(Sub.ReVerify), E.ReVerifyPassed, Rev(Sub.Attack)),
        (Rev(Sub.ReVerify), E.ReVerifyFailed, Rev(Sub.Fixing)),

        (S.ReadyForPerson, E.PersonApproved, S.Done),
        (S.ReadyForPerson, E.PersonRejected, Rev(Sub.Fixing)),
        (S.ReadyForPerson, E.NeedPerson, S.NeedsPerson),
        (S.ReadyForPerson, E.Cancel, S.Cancelled),
        (S.NeedsPerson, E.PersonAnswered, S.Queued),
        (S.NeedsPerson, E.Cancel, S.Cancelled),
        (S.NeedsPerson, E.Fail, S.Failed),
        (S.Failed, E.Retry, S.Queued),

        (Run(Sub.Working), E.NeedPerson, S.NeedsPerson),
        (Run(Sub.WaitingForSlot), E.NeedPerson, S.NeedsPerson),
        (Run(Sub.PausedInfrastructure), E.NeedPerson, S.NeedsPerson),
        (Run(Sub.PausedBudget), E.NeedPerson, S.NeedsPerson),
        (Run(Sub.ParkedTasksWaiting), E.NeedPerson, S.NeedsPerson),
        (Run(Sub.Working), E.Fail, S.Failed),
        (Run(Sub.WaitingForSlot), E.Fail, S.Failed),
        (Run(Sub.PausedInfrastructure), E.Fail, S.Failed),
        (Run(Sub.PausedBudget), E.Fail, S.Failed),
        (Run(Sub.ParkedTasksWaiting), E.Fail, S.Failed),
        (Run(Sub.Working), E.Cancel, S.Cancelled),
        (Run(Sub.WaitingForSlot), E.Cancel, S.Cancelled),
        (Run(Sub.PausedInfrastructure), E.Cancel, S.Cancelled),
        (Run(Sub.PausedBudget), E.Cancel, S.Cancelled),
        (Run(Sub.ParkedTasksWaiting), E.Cancel, S.Cancelled),
        (Rev(Sub.PrVerify), E.NeedPerson, S.NeedsPerson),
        (Rev(Sub.Attack), E.NeedPerson, S.NeedsPerson),
        (Rev(Sub.Judge), E.NeedPerson, S.NeedsPerson),
        (Rev(Sub.Fixing), E.NeedPerson, S.NeedsPerson),
        (Rev(Sub.ReVerify), E.NeedPerson, S.NeedsPerson),
        (Rev(Sub.PrVerify), E.Fail, S.Failed),
        (Rev(Sub.Attack), E.Fail, S.Failed),
        (Rev(Sub.Judge), E.Fail, S.Failed),
        (Rev(Sub.Fixing), E.Fail, S.Failed),
        (Rev(Sub.ReVerify), E.Fail, S.Failed),
        (Rev(Sub.PrVerify), E.Cancel, S.Cancelled),
        (Rev(Sub.Attack), E.Cancel, S.Cancelled),
        (Rev(Sub.Judge), E.Cancel, S.Cancelled),
        (Rev(Sub.Fixing), E.Cancel, S.Cancelled),
        (Rev(Sub.ReVerify), E.Cancel, S.Cancelled),
    ];

    public static IEnumerable<object[]> AllowedCases() => Allowed.Select(a => new object[] { a.From, a.Event, a.To });

    public static IEnumerable<object[]> RefusedCases() =>
        PoolRunStateMachine.AllStates
            .SelectMany(s => Enum.GetValues<E>().Select(e => (s, e)))
            .Where(p => !Allowed.Any(a => a.From == p.s && a.Event == p.e))
            .Select(p => new object[] { p.s, p.e });

    [Fact]
    public void All_statuses_and_sub_statuses_are_in_the_state_list()
    {
        Assert.Equal(17, PoolRunStateMachine.AllStates.Count);
        Assert.Equal(PoolRunStateMachine.AllStates.Count, PoolRunStateMachine.AllStates.Distinct().Count());
        Assert.All(PoolRunStateMachine.AllStates, s => Assert.True(s.IsValid));
        foreach (var status in Enum.GetValues<PoolRunStatus>())
        {
            Assert.Contains(PoolRunStateMachine.AllStates, s => s.Status == status);
        }

        foreach (var sub in Enum.GetValues<Sub>().Where(x => x != Sub.None))
        {
            Assert.Contains(PoolRunStateMachine.AllStates, s => s.Sub == sub);
        }
    }

    [Fact]
    public void The_table_has_exactly_the_allowed_transitions()
    {
        Assert.Equal(Allowed.Length, PoolRunStateMachine.Table.Count);
    }

    [Theory]
    [MemberData(nameof(AllowedCases))]
    public void Allowed_transition_returns_the_new_state(S from, E evt, S to)
    {
        var result = PoolRunStateMachine.Transition(from, evt);

        Assert.True(result.Allowed);
        Assert.Equal(to, result.State);
        Assert.Null(result.RefusedReason);
    }

    [Theory]
    [MemberData(nameof(RefusedCases))]
    public void Refused_transition_returns_refused_and_keeps_the_state(S from, E evt)
    {
        var result = PoolRunStateMachine.Transition(from, evt);

        Assert.False(result.Allowed);
        Assert.Equal(from, result.State);
        Assert.False(string.IsNullOrWhiteSpace(result.RefusedReason));
    }

    [Fact]
    public void Done_and_Cancelled_accept_no_event()
    {
        foreach (var state in new[] { S.Done, S.Cancelled })
        {
            foreach (var evt in Enum.GetValues<E>())
            {
                Assert.False(PoolRunStateMachine.Transition(state, evt).Allowed, $"{state.Status} accepted {evt}");
            }
        }
    }

    [Fact]
    public void Failed_accepts_only_Retry()
    {
        foreach (var evt in Enum.GetValues<E>())
        {
            Assert.Equal(evt == E.Retry, PoolRunStateMachine.Transition(S.Failed, evt).Allowed);
        }
    }

    [Fact]
    public void Invalid_states_are_refused()
    {
        Assert.False(PoolRunStateMachine.Transition(new S(PoolRunStatus.Running), E.Resume).Allowed);
        Assert.False(PoolRunStateMachine.Transition(new S(PoolRunStatus.Queued, Sub.Working), E.Start).Allowed);
        Assert.False(PoolRunStateMachine.Transition(new S(PoolRunStatus.Reviewing, Sub.Working), E.Fail).Allowed);
    }

    [Fact]
    public void Same_input_gives_equal_results()
    {
        foreach (var state in PoolRunStateMachine.AllStates)
        {
            foreach (var evt in Enum.GetValues<E>())
            {
                Assert.Equal(PoolRunStateMachine.Transition(state, evt), PoolRunStateMachine.Transition(state, evt));
            }
        }
    }
}
