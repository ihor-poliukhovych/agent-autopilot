using Autopilot.Core.Config;
using Autopilot.Core.Evals;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Pool;
using Autopilot.Core.Runs;
using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;
using Ws = Autopilot.Core.Workspace.Workspace;

namespace Autopilot.Core.Tests;

public sealed class UsageLimitTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);
    private readonly Ws _workspace;
    private readonly PoolService _pool;
    private readonly PoolStateStore _state;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 28, 1, 0, 0, TimeSpan.Zero));
    private readonly PoolConfig _config = new() { Unattended = true };

    public UsageLimitTests()
    {
        _workspace = new Ws(Path.Combine(_temp, "ws"));
        _pool = new PoolService(_workspace, "demo");
        _state = new PoolStateStore(_workspace, "demo");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    [Theory]
    [InlineData("You have hit your limit")]
    [InlineData("Claude usage limit reached")]
    [InlineData("Your limit will reset at 5pm")]
    public void The_known_phrases_are_a_usage_limit(string message) => Assert.True(UsageLimit.IsUsageLimit(message));

    [Theory]
    [InlineData("rate limit exceeded")]
    [InlineData("not logged in")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_errors_are_not_a_usage_limit(string? message) => Assert.False(UsageLimit.IsUsageLimit(message));

    [Theory]
    [InlineData("You've hit your limit. Your limit will reset at 5pm", 2026, 11, 28, 17, 0)]
    [InlineData("You've hit your limit - resets 3:30pm", 2026, 11, 28, 15, 30)]
    [InlineData("usage limit reached, resets at 17:45", 2026, 11, 28, 17, 45)]
    [InlineData("You've hit your limit. Your limit will reset at 12am", 2026, 11, 29, 0, 0)]
    [InlineData("You've hit your limit. Your limit will reset at 12pm UTC", 2026, 11, 28, 12, 0)]
    [InlineData("You've hit your limit, resets at 0:30", 2026, 11, 29, 0, 30)]
    [InlineData("usage limit reached, limit will reset at 2026-11-28 05:00 UTC", 2026, 11, 28, 5, 0)]
    [InlineData("hit your limit, resets 2026-11-30T08:15:00Z", 2026, 11, 30, 8, 15)]
    public void A_reset_time_is_read_from_several_formats(string message, int y, int mo, int d, int h, int mi)
    {
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero), UsageLimit.ParseResetTime(message, _clock));
    }

    [Fact]
    public void A_reset_time_with_a_zone_name_is_converted_to_utc()
    {
        var result = UsageLimit.ParseResetTime("You've hit your limit - resets 5pm (UTC)", _clock);
        Assert.Equal(new DateTimeOffset(2026, 11, 28, 17, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void A_unix_time_after_the_pipe_is_a_reset_time()
    {
        var at = _clock.GetUtcNow().AddHours(3);
        var result = UsageLimit.ParseResetTime($"Claude AI usage limit reached|{at.ToUnixTimeSeconds()}", _clock);
        Assert.Equal(at, result);
    }

    [Theory]
    [InlineData("You have hit your limit")]
    [InlineData("usage limit reached, resets soon")]
    [InlineData("limit will reset in 5 minutes")]
    [InlineData("resets at 25pm")]
    [InlineData("")]
    [InlineData(null)]
    public void A_missing_or_unreadable_reset_time_is_null(string? message) => Assert.Null(UsageLimit.ParseResetTime(message, _clock));

    private PoolRunner Runner(Func<string, Task<ImplementResult>> implement) =>
        new(_pool, new RunStore(_workspace, "demo"), new ProjectRunLock(_workspace, _ => false), "demo", (id, _) => implement(id),
            new PoolRunOptions
            {
                Config = () => _config,
                Tasks = new FakeTasks(),
                State = _state,
                TaskLock = new TaskRunLock(_workspace, _ => false),
                Clock = _clock,
                PollInterval = TimeSpan.FromMilliseconds(20),
            });

    private Task<PoolRunResult> RunAsync(Func<string, Task<ImplementResult>> implement) =>
        Runner(implement).RunAsync(null, Path.Combine(_temp, "report"), CancellationToken.None);

    private static ImplementResult Result(string id, ImplementOutcome outcome, string message = "ok") =>
        new(id, outcome, message, "autopilot/" + id);

    [Fact]
    public async Task A_limit_with_a_reset_time_pauses_until_then_without_parking_or_a_retry_and_the_pool_resumes()
    {
        _pool.Add("1");
        _pool.Add("2");
        var calls = 0;
        Task<ImplementResult> Implement(string id)
        {
            calls++;
            return Task.FromResult(calls == 1
                ? Result(id, ImplementOutcome.Failed, "You've hit your limit. Your limit will reset at 5pm")
                : Result(id, ImplementOutcome.Published));
        }

        var first = await RunAsync(Implement);
        var resetAt = new DateTimeOffset(2026, 11, 28, 17, 0, 0, TimeSpan.Zero);
        var pause = _state.CurrentPause();
        Assert.NotNull(pause);
        Assert.Contains("usage limit", pause!.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(resetAt, pause.RetryAt);
        Assert.Empty(_state.Parked());
        Assert.Empty(first.Parked);
        Assert.Equal(1, calls);
        Assert.Equal(["1", "2"], _pool.List().Select(e => e.WorkItemId));
        Assert.Contains("usage limit", first.StoppedBecause, StringComparison.OrdinalIgnoreCase);

        // Before the reset time nothing starts.
        _clock.Advance(TimeSpan.FromHours(10));
        var waiting = await RunAsync(Implement);
        Assert.Equal(1, calls);
        Assert.Contains("paused until", waiting.StoppedBecause, StringComparison.Ordinal);

        // After it the pool clears the pause and finishes both tasks; the first one was not retried as a failure.
        _clock.Advance(TimeSpan.FromHours(7));
        var resumed = await RunAsync(Implement);
        Assert.Equal(2, resumed.Done.Count);
        Assert.Null(_state.CurrentPause());
        Assert.Empty(_state.Parked());
        Assert.Empty(_pool.List());
    }

    [Fact]
    public async Task A_limit_without_a_reset_time_pauses_for_the_configured_minutes()
    {
        _pool.Add("1");

        await RunAsync(id => Task.FromResult(Result(id, ImplementOutcome.Failed, "You have hit your limit")));

        var pause = _state.CurrentPause();
        Assert.NotNull(pause);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(_config.OnFailure.PauseMinutes), pause!.RetryAt);
        Assert.Empty(_state.Parked());
        Assert.Single(_pool.List());
    }
}
