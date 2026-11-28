using Autopilot.Core.Config;
using Autopilot.Core.Gate;

namespace Autopilot.Core.Tests.Gate;

public class GateLimitResolverTests
{
    private static readonly GateConfig Project = new() { MaxFilesChanged = 10, MaxSourceLinesChanged = 200, MaxTestLinesChanged = 400 };

    [Fact]
    public void Pool_files_value_wins()
    {
        var r = GateLimitResolver.Resolve(new PoolGateLimits(MaxFilesChanged: 3), Project);
        Assert.Equal(3, r.MaxFilesChanged);
        Assert.True(r.MaxFilesChangedFromPool);
    }

    [Fact]
    public void Pool_source_value_wins()
    {
        var r = GateLimitResolver.Resolve(new PoolGateLimits(MaxSourceLinesChanged: 50), Project);
        Assert.Equal(50, r.MaxSourceLinesChanged);
        Assert.True(r.MaxSourceLinesChangedFromPool);
    }

    [Fact]
    public void Pool_test_value_wins()
    {
        var r = GateLimitResolver.Resolve(new PoolGateLimits(MaxTestLinesChanged: 90), Project);
        Assert.Equal(90, r.MaxTestLinesChanged);
        Assert.True(r.MaxTestLinesChangedFromPool);
    }

    [Fact]
    public void Empty_pool_values_use_the_project()
    {
        var r = GateLimitResolver.Resolve(new PoolGateLimits(), Project);
        Assert.Equal((10, 200, 400), (r.MaxFilesChanged, r.MaxSourceLinesChanged, r.MaxTestLinesChanged));
        Assert.False(r.MaxFilesChangedFromPool || r.MaxSourceLinesChangedFromPool || r.MaxTestLinesChangedFromPool);
    }

    [Fact]
    public void Neither_set_uses_built_in_defaults()
    {
        var r = GateLimitResolver.Resolve(null, null);
        Assert.Equal((6, 150, 300), (r.MaxFilesChanged, r.MaxSourceLinesChanged, r.MaxTestLinesChanged));
        Assert.False(r.MaxFilesChangedFromPool);
    }

    [Fact]
    public void Mixed_limits_resolve_independently()
    {
        var r = GateLimitResolver.Resolve(new PoolGateLimits(MaxFilesChanged: 2, MaxTestLinesChanged: 5), Project);
        Assert.Equal((2, 200, 5), (r.MaxFilesChanged, r.MaxSourceLinesChanged, r.MaxTestLinesChanged));
        Assert.True(r.MaxFilesChangedFromPool);
        Assert.False(r.MaxSourceLinesChangedFromPool);
        Assert.True(r.MaxTestLinesChangedFromPool);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void Non_positive_pool_values_are_ignored_and_fall_back_to_the_project(int bad)
    {
        var r = GateLimitResolver.Resolve(new PoolGateLimits(bad, bad, bad), Project);
        Assert.Equal((10, 200, 400), (r.MaxFilesChanged, r.MaxSourceLinesChanged, r.MaxTestLinesChanged));
        Assert.False(r.MaxFilesChangedFromPool || r.MaxSourceLinesChangedFromPool || r.MaxTestLinesChangedFromPool);
    }
}
