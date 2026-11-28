using Autopilot.Core.Config;

namespace Autopilot.Core.Gate;

/// <summary>Optional pool-level overrides of the three gate size limits. Null means "use the project value".</summary>
public sealed record PoolGateLimits(int? MaxFilesChanged = null, int? MaxSourceLinesChanged = null, int? MaxTestLinesChanged = null);

/// <summary>The effective gate size limits and which of them came from the pool.</summary>
public sealed record EffectiveGateLimits(
    int MaxFilesChanged,
    int MaxSourceLinesChanged,
    int MaxTestLinesChanged,
    bool MaxFilesChangedFromPool,
    bool MaxSourceLinesChangedFromPool,
    bool MaxTestLinesChangedFromPool);

public static class GateLimitResolver
{
    /// <summary>
    /// Resolves each limit on its own: a positive pool value wins, else a positive project value,
    /// else the built-in default. Non-positive pool values are not valid overrides and are ignored.
    /// </summary>
    public static EffectiveGateLimits Resolve(PoolGateLimits? pool, GateConfig? project)
    {
        var defaults = new GateConfig();
        var (files, filesPool) = Pick(pool?.MaxFilesChanged, project?.MaxFilesChanged, defaults.MaxFilesChanged);
        var (source, sourcePool) = Pick(pool?.MaxSourceLinesChanged, project?.MaxSourceLinesChanged, defaults.MaxSourceLinesChanged);
        var (test, testPool) = Pick(pool?.MaxTestLinesChanged, project?.MaxTestLinesChanged, defaults.MaxTestLinesChanged);
        return new EffectiveGateLimits(files, source, test, filesPool, sourcePool, testPool);
    }

    private static (int Value, bool FromPool) Pick(int? pool, int? project, int fallback)
    {
        if (pool is > 0)
            return (pool.Value, true);
        if (project is > 0)
            return (project.Value, false);
        return (fallback, false);
    }
}
