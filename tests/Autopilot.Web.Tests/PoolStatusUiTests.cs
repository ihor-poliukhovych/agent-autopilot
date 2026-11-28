namespace Autopilot.Web.Tests;

public sealed class PoolStatusUiTests
{
    private static string Wwwroot(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "Autopilot.Web", "wwwroot", relative);
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative + " not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Stepper_lists_the_six_stages_in_order()
    {
        var js = Wwwroot("js/pool.js");
        var order = new[] { "'Planned'", "'Queued'", "'Running'", "'Reviewing'", "'Ready for a person'", "'Done'" };
        var at = js.IndexOf("export const STAGES = [", StringComparison.Ordinal);
        Assert.True(at >= 0);
        foreach (var stage in order)
        {
            var next = js.IndexOf(stage, at, StringComparison.Ordinal);
            Assert.True(next >= at, stage);
            at = next;
        }
        Assert.Contains("status?.status", js);
        Assert.Contains("status?.subStatus", js);
        Assert.Contains("'None'", js);
        Assert.Contains("sub-dots", js);
    }

    [Fact]
    public void Strip_queue_and_timeline_use_the_status_fields()
    {
        var js = Wwwroot("js/pool.js");
        Assert.Contains("p.done", js);
        Assert.Contains("p.parked", js);
        Assert.Contains("'Readiness'", js);
        Assert.Contains("status?.queue", js);
        Assert.Contains("status?.timeline", js);
        Assert.Contains("e.time", js);
        Assert.Contains("e.event", js);
        Assert.Contains("e.detail", js);
        Assert.Contains("/pool/status", js);
    }

    [Fact]
    public void Status_is_read_again_by_the_existing_poll()
    {
        var js = Wwwroot("js/pool.js");
        Assert.Contains("POLL_MS = 5000", js);
        var refresh = js[js.IndexOf("async function refresh", StringComparison.Ordinal)..];
        Assert.Contains("/pool/status", refresh[..refresh.IndexOf("async function load", StringComparison.Ordinal)]);
    }

    [Fact]
    public void Settings_sit_in_the_left_column_and_status_and_queue_in_the_right()
    {
        var js = Wwwroot("js/pool.js");
        var css = Wwwroot("app.css");
        var unattended = Wwwroot("js/poolunattended.js");
        Assert.Contains("app-left pool-settings stack' }, extra.element, runNow", js);
        Assert.Contains("app-right open stack' }, statusCard, queue, extra.right", js);
        Assert.Matches(@"\.pool-layout\s*\{[^}]*var\(--left-col\)", css);
        // Left: settings card, run summary, integration branch, schedule. Right: parked tasks and decisions.
        Assert.Matches(@"place\(holder,\s*settingsCard\([^)]*\),\s*summary,\s*integrationCard\([^)]*\),\s*scheduleCard", unattended);
        Assert.Contains("'Run summary'", unattended);
        Assert.Contains("place(live, parkedCard(key, status, reload), decisionsCard(key, status, reload))", unattended);
        Assert.Contains("right: live", unattended);
        // Right: stepper and progress strip with events (status card), then the queue with readiness.
        Assert.Contains("put(statusCard, h('h2', {}, 'Status'), stepper(status), progressStrip(status?.progress), h('h3', {}, 'Events')", js);
    }
}
