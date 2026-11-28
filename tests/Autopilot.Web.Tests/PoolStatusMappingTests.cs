using System.Diagnostics;
using System.Text.Json;

namespace Autopilot.Web.Tests;

/// <summary>
/// Runs the real status mapping of the Pool page (js/poolstatus.js, a module without DOM code) in Node, with
/// the status JSON the API sends. The tests do nothing on a machine without Node.
/// </summary>
public sealed class PoolStatusMappingTests
{
    private static JsonElement? Eval(string expression)
    {
        var module = new Uri(WwwrootFiles.FullPath("js/poolstatus.js")).AbsoluteUri;
        var script = $"import * as m from '{module}'; console.log(JSON.stringify({expression}));";
        ProcessStartInfo info = new("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("--input-type=module");
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add(script);
        Process? started;
        try { started = Process.Start(info); }
        catch (System.ComponentModel.Win32Exception) { return null; }

        using (var process = started ?? throw new InvalidOperationException("node did not start"))
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            return JsonDocument.Parse(output).RootElement.Clone();
        }
    }

    private static string States(JsonElement steps) => string.Join(",", steps.EnumerateArray().Select(s => s.GetProperty("state").GetString()));

    [Theory]
    [InlineData("Planned", "None", "current,todo,todo,todo,todo")]
    [InlineData("Queued", "None", "current,todo,todo,todo,todo")]
    [InlineData("Running", "Working", "done,current,todo,todo,todo")]
    [InlineData("Running", "WaitingForSlot", "done,current,todo,todo,todo")]
    [InlineData("Running", "PausedBudget", "done,current,todo,todo,todo")]
    [InlineData("Running", "ParkedTasksWaiting", "done,current,todo,todo,todo")]
    [InlineData("Reviewing", "Attack", "done,done,done,current,todo")]
    [InlineData("ReadyForPerson", "None", "done,done,done,current,todo")]
    [InlineData("NeedsPerson", "None", "done,done,current,todo,todo")]
    [InlineData("Done", "None", "done,done,done,done,done")]
    [InlineData("Failed", "None", "done,todo,todo,todo,todo")]
    [InlineData("Cancelled", "None", "done,todo,todo,todo,todo")]
    public void Each_backend_state_lands_on_one_of_the_five_steps(string status, string sub, string expected)
    {
        var steps = Eval($"m.stepper({{ status: '{status}', subStatus: '{sub}', parked: [], queue: [], slots: [] }})");
        if (steps is null) return;
        Assert.Equal(expected, States(steps.Value));
        Assert.Equal(["Planned", "Running", "Waiting for you", "Final PR", "Done"], steps.Value.EnumerateArray().Select(s => s.GetProperty("name").GetString()!));
    }

    [Fact]
    public void Waiting_for_you_is_amber_with_a_count_only_when_something_waits()
    {
        var some = Eval("m.stepper({ status: 'Running', subStatus: 'Working', parked: [{}, {}] })");
        var none = Eval("m.stepper({ status: 'Running', subStatus: 'Working', parked: [] })");
        if (some is null || none is null) return;
        var waiting = some.Value.EnumerateArray().ElementAt(2);
        Assert.Equal("need", waiting.GetProperty("state").GetString());
        Assert.Equal(2, waiting.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, none.Value.EnumerateArray().ElementAt(2).GetProperty("count").ValueKind);
        Assert.Equal("todo", none.Value.EnumerateArray().ElementAt(2).GetProperty("state").GetString());
    }

    [Fact]
    public void Sub_statuses_become_small_chips()
    {
        var chips = Eval("m.subChips({ status: 'Running', subStatus: 'Working', slots: [{ state: 'running' }, { state: 'running' }, { state: 'waiting' }], queue: [] }).chips.map(c => c.text)");
        var paused = Eval("m.subChips({ status: 'Running', subStatus: 'PausedBudget', slots: [], queue: [] }).chips.map(c => c.text)");
        var review = Eval("m.subChips({ status: 'Reviewing', subStatus: 'Judge', slots: [], queue: [] }).chips.map(c => c.text)");
        if (chips is null || paused is null || review is null) return;
        Assert.Equal(["2 working", "1 waiting for a slot"], chips.Value.EnumerateArray().Select(c => c.GetString()!));
        Assert.Equal(["paused: usage limit reached"], paused.Value.EnumerateArray().Select(c => c.GetString()!));
        Assert.Equal(["judging"], review.Value.EnumerateArray().Select(c => c.GetString()!));
    }

    [Fact]
    public void The_pill_says_not_running_when_no_run_holds_the_project()
    {
        var idle = Eval("m.statePill({ status: 'Running' }, false).text");
        var live = Eval("m.statePill({ status: 'Running' }, true).text");
        var fresh = Eval("m.statePill({ status: 'Planned' }, false).text");
        if (idle is null || live is null || fresh is null) return;
        Assert.Equal("Not running", idle.Value.GetString());
        Assert.Equal("Running", live.Value.GetString());
        Assert.Equal("Not started", fresh.Value.GetString());
    }

    [Fact]
    public void Progress_segments_add_up_and_the_stat_line_has_the_agent_runs()
    {
        var status = "{ status: 'Running', progress: { done: 3, total: 4, parked: 1 }, agentRunsToday: 14, settings: { maxAgentRuns: 40 }, "
            + "slots: [{ state: 'running' }, { state: 'running' }], "
            + "queue: [{ readiness: 'ready' }, { readiness: 'ready' }, { readiness: 'ready' }, { readiness: 'parked' }, { readiness: 'not triaged' }] }";
        var seg = Eval($"m.progressSegments({status})");
        var line = Eval($"m.statLine({status})");
        if (seg is null || line is null) return;
        Assert.Equal(3, seg.Value.GetProperty("done").GetInt32());
        Assert.Equal(2, seg.Value.GetProperty("running").GetInt32());
        Assert.Equal(1, seg.Value.GetProperty("waiting").GetInt32());
        Assert.Equal(2, seg.Value.GetProperty("queued").GetInt32());
        Assert.Equal(8, seg.Value.GetProperty("total").GetInt32());
        Assert.Equal("14 of 40", line.Value.GetProperty("right")[0].GetString());
    }

    [Fact]
    public void An_idle_pool_shows_an_estimate_of_agent_runs()
    {
        var line = Eval("m.statLine({ status: 'Planned', progress: { done: 0, total: 0, parked: 0 }, agentRunsToday: 0, queue: Array.from({ length: 8 }, () => ({ readiness: 'ready' })) }).right.join('')");
        if (line is null) return;
        Assert.Equal("Estimate: about 16 agent runs", line.Value.GetString());
    }

    [Fact]
    public void Queue_shows_five_rows_and_the_rest_behind_Show_more()
    {
        var capped = Eval("(() => { const w = m.queueWindow(Array.from({ length: 8 }, (_, i) => i), false); return [w.visible.length, w.hidden, w.canCollapse]; })()");
        var open = Eval("(() => { const w = m.queueWindow(Array.from({ length: 8 }, (_, i) => i), true); return [w.visible.length, w.hidden, w.canCollapse]; })()");
        var exact = Eval("(() => { const w = m.queueWindow(Array.from({ length: 5 }, (_, i) => i), false); return [w.visible.length, w.hidden, w.canCollapse]; })()");
        if (capped is null || open is null || exact is null) return;
        Assert.Equal("5,3,False", string.Join(",", capped.Value.EnumerateArray().Select(e => e.ToString())));
        Assert.Equal("8,0,True", string.Join(",", open.Value.EnumerateArray().Select(e => e.ToString())));
        Assert.Equal("5,0,False", string.Join(",", exact.Value.EnumerateArray().Select(e => e.ToString())));
        Assert.Equal(5, Eval("m.QUEUE_CAP")!.Value.GetInt32());
    }

    [Fact]
    public void A_queue_row_shows_one_status_word_in_one_tone()
    {
        var pills = Eval("[m.rowPill({ readiness: 'ready' }), m.rowPill({ readiness: 'needs an answer' }), m.rowPill({ readiness: 'parked' }), m.rowPill({ readiness: 'waits for 4' }), m.rowPill({ readiness: 'ready' }, { state: 'running' })].map(p => p.text + ':' + p.tone)");
        if (pills is null) return;
        Assert.Equal(["Ready:ok", "Needs an answer:need", "Waiting for you:need", "Waits for #4:idle", "Running:run"], pills.Value.EnumerateArray().Select(e => e.GetString()!));
    }

    [Fact]
    public void Events_read_as_text_with_a_tone_newest_first()
    {
        var one = Eval("[m.eventView({ event: 'TaskMerged', taskId: '103', detail: 'merged' }), m.eventView({ event: 'TaskParked', taskId: '105', detail: 'rules' }), m.eventView({ event: 'Start' })].map(v => v.text + ':' + v.tone)");
        var order = Eval("m.eventWindow([{ event: 'a' }, { event: 'b' }, { event: 'c' }], false, 2)");
        if (one is null || order is null) return;
        Assert.Equal(["#103 merged into the integration branch:ok", "#105 needs you: rules:need", "The run started:run"], one.Value.EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(["c", "b"], order.Value.GetProperty("rows").EnumerateArray().Select(e => e.GetProperty("event").GetString()!));
        Assert.Equal(1, order.Value.GetProperty("hidden").GetInt32());
    }

    [Fact]
    public void Nothing_is_marked_as_changed_unless_the_settings_carry_their_defaults()
    {
        var none = Eval("m.overrides({ concurrency: 2, stopAtUsagePercent: 80 })");
        var some = Eval("m.overrides({ concurrency: 2, stopAtUsagePercent: 80, defaults: { concurrency: 3, stopAtUsagePercent: 80 } })");
        if (none is null || some is null) return;
        Assert.Empty(none.Value.EnumerateObject());
        Assert.Equal(3, some.Value.GetProperty("concurrency").GetInt32());
        Assert.False(some.Value.TryGetProperty("stopAtUsagePercent", out _));
    }
}
