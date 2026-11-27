using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Jobs;
using Autopilot.Core.Scheduling;

namespace Autopilot.Cli;

/// <summary>Streams and replaceable parts for one CLI invocation. Tests swap the agent runner and the gate.</summary>
public sealed record CliContext(TextReader In, TextWriter Out, TextWriter Error)
{
    /// <summary>Builds the agent runner. Null means Claude Code headless.</summary>
    public Func<IAgentRunner>? RunnerFactory { get; init; }

    /// <summary>The gate. Null means <see cref="MergeGate.Evaluate"/>.</summary>
    public Func<GateInput, AutopilotConfig, GateDecision>? EvaluateGate { get; init; }

    /// <summary>Gate facts collector. Null means <see cref="GateFactsCollector"/>.</summary>
    public Func<AutopilotConfig, IGateFactsCollector>? FactsCollectorFactory { get; init; }

    /// <summary>Folder for worktrees. Null means the per-user default outside the project.</summary>
    public string? WorktreeRoot { get; init; }

    /// <summary>The operating system's scheduler for <c>schedule</c>. Null means Task Scheduler on Windows, crontab elsewhere.</summary>
    public IScheduler? Scheduler { get; init; }

    /// <summary>Starts detached jobs. Null means a real detached process.</summary>
    public IJobLauncher? JobLauncher { get; init; }

    /// <summary>Stops a job's process. Null means a real process-tree kill.</summary>
    public IProcessKiller? ProcessKiller { get; init; }

    /// <summary>Tells whether a process id is alive (stale locks, dead jobs). Null means ask the operating system.</summary>
    public Func<int, bool>? IsProcessAlive { get; init; }

    /// <summary>The program that scheduled and detached runs start, as file and leading arguments. Null means this program.</summary>
    public (string File, IReadOnlyList<string> Arguments)? Program { get; init; }

    /// <summary>False skips the dotnet, git and claude version checks in doctor (tests never start claude).</summary>
    public bool DoctorChecksTools { get; init; } = true;

    public static CliContext FromConsole() => new(Console.In, Console.Out, Console.Error);
}

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Usage = 2;
}
