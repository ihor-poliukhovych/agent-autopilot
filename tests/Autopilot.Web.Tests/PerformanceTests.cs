using System.Diagnostics;
using System.Net;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Local;
using Autopilot.Core.Tasks;
using Autopilot.Core.Workspace;

namespace Autopilot.Web.Tests;

/// <summary>A tracker stand-in that counts its calls and takes a fixed time per list, like a slow Azure DevOps query.</summary>
public sealed class SlowTracker(TimeSpan delay) : IWorkItemSourceFactory
{
    public int ListCalls;

    public IWorkItemSource Create(string repositoryFolder, AutopilotConfig config, string dataFolder, LocalTaskService? tasks) => new Source(this);

    private sealed class Source(SlowTracker owner) : IWorkItemSource
    {
        public async Task<IReadOnlyList<WorkItem>> ListOpenAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref owner.ListCalls);
            await Task.Delay(owner.Delay, ct);
            return [new WorkItem("101", "From the tracker", "Body", [], null, "Active")];
        }

        public Task<WorkItem?> GetAsync(string id, CancellationToken ct) => Task.FromResult<WorkItem?>(null);

        public Task RecordTriageAsync(string id, TriageResult result, CancellationToken ct) => Task.CompletedTask;
    }

    private TimeSpan Delay { get; } = delay;
}

public sealed class PerformanceTests
{
    private const int Delay = 400;

    private static readonly string[] PageCalls = ["tasks", "needs-human", "pool", "runs"];

    private static async Task<long> LoadPageAsync(UiFixture ui)
    {
        var watch = Stopwatch.StartNew();
        // What the pipeline page and its tab badges ask for at once.
        var calls = PageCalls.Select(p => ui.GetAsync($"/api/projects/azure/{p}")).ToArray();
        await Task.WhenAll(calls);
        foreach (var call in calls)
        {
            Assert.Equal(HttpStatusCode.OK, call.Result.Status);
        }

        return watch.ElapsedMilliseconds;
    }

    private static async Task AddTrackerProjectAsync(UiFixture ui)
    {
        var (status, body) = await ui.PostAsync("/api/projects", ui.Draft("azure", "azure-devops"));
        Assert.True(status == HttpStatusCode.Created, body.ToString());
    }

    [Fact]
    public async Task One_page_load_calls_the_tracker_once_and_the_next_load_not_at_all()
    {
        var tracker = new SlowTracker(TimeSpan.FromMilliseconds(Delay));
        await using var ui = new UiFixture(sources: tracker, withAdo: true);
        await AddTrackerProjectAsync(ui);
        tracker.ListCalls = 0;

        var first = await LoadPageAsync(ui);
        Assert.Equal(1, tracker.ListCalls);
        Assert.InRange(first, Delay - 100, Delay * 3);

        var second = await LoadPageAsync(ui);
        Assert.Equal(1, tracker.ListCalls);
        Assert.True(second < 300, $"A cached page took {second} ms.");
    }

    [Fact]
    public async Task Without_the_cache_every_ask_calls_the_tracker()
    {
        var tracker = new SlowTracker(TimeSpan.FromMilliseconds(Delay));
        await using var ui = new UiFixture(sources: tracker, cacheTtl: TimeSpan.Zero);
        await AddTrackerProjectAsync(ui);
        tracker.ListCalls = 0;

        var one = await LoadPageAsync(ui);
        var two = await LoadPageAsync(ui);
        Assert.Equal(4, tracker.ListCalls); // tasks and needs-human, twice (the pool asks only when it holds something)
    }

    [Fact]
    public async Task A_local_project_is_never_cached_so_a_new_task_shows_at_once()
    {
        await using var ui = new UiFixture();
        await ui.CreateProjectAsync();
        await ui.PostAsync("/api/projects/demo/tasks", new { title = "One" });
        Assert.Single((await ui.GetAsync("/api/projects/demo/tasks")).Body.GetProperty("items").EnumerateArray());
        await ui.PostAsync("/api/projects/demo/tasks", new { title = "Two" });
        Assert.Equal(2, (await ui.GetAsync("/api/projects/demo/tasks")).Body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_stale_list_is_returned_at_once_while_one_refresh_runs()
    {
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        var cache = new WorkItemCache(TimeSpan.FromSeconds(30), () => now);
        Task<IReadOnlyList<WorkItem>> Fetch(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<WorkItem>>([new WorkItem("1", "v" + n, "", [], null, "Active")]);
        }

        Assert.Equal("v1", (await cache.GetAsync("p", Fetch, default))[0].Title);
        Assert.Equal("v1", (await cache.GetAsync("p", Fetch, default))[0].Title);
        Assert.Equal(1, calls);

        now = now.AddSeconds(31);
        Assert.Equal("v1", (await cache.GetAsync("p", Fetch, default))[0].Title); // stale, served at once
        await Eventually(() => calls == 2);
        await Eventually(() => cache.GetAsync("p", Fetch, default).Result[0].Title == "v2");
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_failed_fetch_is_not_remembered()
    {
        var cache = new WorkItemCache(TimeSpan.FromSeconds(30));
        var attempts = 0;
        Task<IReadOnlyList<WorkItem>> Fetch(CancellationToken ct) =>
            ++attempts == 1 ? throw new InvalidOperationException("tracker down") : Task.FromResult<IReadOnlyList<WorkItem>>([]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync("p", Fetch, default));
        await Eventually(() => true);
        Assert.Empty(await cache.GetAsync("p", Fetch, default));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Static_files_carry_an_etag_and_answer_304_when_unchanged()
    {
        await using var ui = new UiFixture();
        var first = await ui.Client.GetAsync("/js/app.js");
        var etag = first.Headers.ETag!.Tag;
        using var again = new HttpRequestMessage(HttpMethod.Get, "/js/app.js");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await ui.Client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Every_response_carries_its_server_time_and_the_log_gets_a_line()
    {
        var lines = new List<string>();
        await using var ui = new UiFixture(requestLog: lines.Add);
        var response = await ui.Client.GetAsync("/api/state");
        Assert.Contains("dur=", response.Headers.GetValues("Server-Timing").Single(), StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("GET /api/state 200 ", StringComparison.Ordinal) && l.EndsWith(" ms", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_page_has_a_footer_line_for_timings()
    {
        await using var ui = new UiFixture();
        Assert.Contains("id=\"timing\"", await ui.Client.GetStringAsync("/"), StringComparison.Ordinal);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}
