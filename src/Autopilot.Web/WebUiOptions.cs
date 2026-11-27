using Autopilot.Core.Jobs;
using Autopilot.Core.Scheduling;
using Autopilot.Core.Workspace;

namespace Autopilot.Web;

/// <summary>Everything the web UI needs from outside. Tests replace the parts that start processes or call the network.</summary>
public sealed class WebUiOptions
{
    public required Workspace Workspace { get; init; }

    public required ISetupChecker Checker { get; init; }

    public required IWorkItemSourceFactory Sources { get; init; }

    public required ProgramInfo Program { get; init; }

    /// <summary>Port on 127.0.0.1. 0 picks a free one.</summary>
    public int Port { get; init; } = 5178;

    public IAzureDevOpsProbe? AzureDevOps { get; init; }

    public IJobLauncher Launcher { get; init; } = new DetachedProcessLauncher();

    public IProcessKiller Killer { get; init; } = new ProcessTreeKiller();

    public IScheduler? Scheduler { get; init; }

    public Func<int, bool>? IsProcessAlive { get; init; }

    /// <summary>Looks up an environment variable (the wizard only reports whether a named variable is set).</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Fixed one-time key, for tests. Null means a random key.</summary>
    public string? SessionKey { get; init; }

    public string Version { get; init; } = "0.1.0";
}
