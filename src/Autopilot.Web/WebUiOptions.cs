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

    /// <summary>Port to listen on. 0 picks a free one.</summary>
    public int Port { get; init; } = 5178;

    public IAzureDevOpsProbe? AzureDevOps { get; init; }

    public IJobLauncher Launcher { get; init; } = new DetachedProcessLauncher();

    public IProcessKiller Killer { get; init; } = new ProcessTreeKiller();

    public IScheduler? Scheduler { get; init; }

    public Func<int, bool>? IsProcessAlive { get; init; }

    /// <summary>Looks up an environment variable (the wizard only reports whether a named variable is set).</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Address to listen on. Loopback unless the person asks otherwise; anything else makes the key mandatory.</summary>
    public System.Net.IPAddress BindAddress { get; init; } = System.Net.IPAddress.Loopback;

    /// <summary>True or false forces the key on or off; null reads the workspace setting <c>ui.requireKey</c>. Not loopback means always on.</summary>
    public bool? RequireKey { get; init; }

    /// <summary>Fixed key, for tests. Null means the persistent key of the workspace.</summary>
    public string? SessionKey { get; init; }

    /// <summary>How long a tracker list is reused. Zero turns the cache off.</summary>
    public TimeSpan TrackerCacheTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets one line per request (method, path, status, milliseconds). Null logs nothing.</summary>
    public Action<string>? RequestLog { get; init; }

    internal WorkItemCache ListCache => _cache.Value;

    private readonly Lazy<WorkItemCache> _cache;

    public WebUiOptions()
    {
        _cache = new Lazy<WorkItemCache>(() => new WorkItemCache(TrackerCacheTtl), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Version { get; init; } = "0.1.0";
}
