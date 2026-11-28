namespace Autopilot.Core.Pool;

/// <summary>How far a pool run is: tasks merged, tasks parked, and the distinct tasks the run took.</summary>
public readonly record struct PoolRunProgress(int Done, int Total, int Parked);

/// <summary>The state of a pool run and its progress, both derived from the stored events.</summary>
public readonly record struct PoolRunStatusSnapshot(PoolRunState State, PoolRunProgress Progress);

/// <summary>
/// Folds the events of one pool run into its state and progress. Pure: no I/O, no clock. An event the transition
/// table refuses is skipped and leaves the state as it was.
/// </summary>
public static class PoolRunStatusFolder
{
    public static PoolRunStatusSnapshot Fold(IEnumerable<PoolRunEventRecord> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var state = PoolRunState.Planned;
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var merged = new HashSet<string>(StringComparer.Ordinal);
        var parked = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in events)
        {
            if (record.TaskId is { Length: > 0 } task)
            {
                switch (record.Event)
                {
                    case PoolRunEvent.SlotGranted:
                        taken.Add(task);
                        break;
                    case PoolRunEvent.TaskMerged:
                        taken.Add(task);
                        merged.Add(task);
                        break;
                    case PoolRunEvent.TaskParked:
                        taken.Add(task);
                        parked.Add(task);
                        break;
                }
            }

            var transition = PoolRunStateMachine.Transition(state, record.Event);
            if (transition.Allowed)
            {
                state = transition.State;
            }
        }

        return new PoolRunStatusSnapshot(state, new PoolRunProgress(merged.Count, taken.Count, parked.Count));
    }
}
