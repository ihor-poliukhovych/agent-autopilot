using Autopilot.Core.Runs;
using Autopilot.Core.Tasks;

namespace Autopilot.Web.Tests;

public sealed class PipelineStepsTests
{
    private static string[] States(PipelineInput i) => PipelineSteps.Dots(i).Select(d => d.State).ToArray();

    [Fact]
    public void There_are_seven_dots_in_order()
    {
        Assert.Equal(["Start", "Qualify", "Implement", "Merge", "Verify dev", "Verify prod", "Done"],
            PipelineSteps.Dots(new PipelineInput()).Select(d => d.Step).ToArray());
    }

    [Fact]
    public void Item_with_no_run_and_no_triage_is_outside_autopilot_and_starts_at_Start()
    {
        var input = new PipelineInput(TrackerState: "Open");
        Assert.True(PipelineSteps.IsOutsideAutopilot(input));
        Assert.Equal(["current", "pending", "pending", "pending", "pending", "pending", "pending"], States(input));
    }

    [Fact]
    public void Triaged_ready_item_waits_to_be_implemented()
    {
        var input = new PipelineInput(TriageLabel: "agent-ready", InPool: true);
        Assert.False(PipelineSteps.IsOutsideAutopilot(input));
        Assert.Equal(["done", "done", "current", "pending", "pending", "pending", "pending"], States(input));
    }

    [Fact]
    public void Needs_human_triage_is_a_problem_on_Qualify_with_an_action()
    {
        var dots = PipelineSteps.Dots(new PipelineInput(TriageLabel: "needs-human"));
        Assert.Equal("problem", dots[1].State);
        Assert.True(PipelineSteps.ActionDue(dots).Due);
    }

    [Fact]
    public void Running_job_makes_Implement_current()
    {
        var input = new PipelineInput(TriageLabel: "agent-ready", JobActive: true);
        Assert.Equal("current", States(input)[2]);
        Assert.False(PipelineSteps.ActionDue(PipelineSteps.Dots(input)).Due);
    }

    [Fact]
    public void Gate_rejected_run_is_a_problem_on_Implement()
    {
        var dots = PipelineSteps.Dots(new PipelineInput(TriageLabel: "agent-ready", HasRun: true, GateVerdict: "rejected"));
        Assert.Equal("problem", dots[2].State);
        Assert.True(PipelineSteps.ActionDue(dots).Due);
    }

    [Fact]
    public void Draft_waiting_for_a_person_makes_Merge_current_with_an_action()
    {
        var dots = PipelineSteps.Dots(new PipelineInput(TriageLabel: "agent-ready", HasRun: true, GateVerdict: "passed"));
        Assert.Equal(["done", "done", "done", "current", "pending", "pending", "pending"], dots.Select(d => d.State).ToArray());
        Assert.Equal((true, "Review draft"), PipelineSteps.ActionDue(dots));
    }

    [Fact]
    public void Rejected_by_person_is_a_problem_on_Merge()
    {
        var dots = PipelineSteps.Dots(new PipelineInput(TriageLabel: "agent-ready", HasRun: true, GateVerdict: "passed", HumanDecision: "rejected"));
        Assert.Equal("problem", dots[3].State);
    }

    [Fact]
    public void Merged_run_makes_Verify_dev_current()
    {
        var input = new PipelineInput(TriageLabel: "agent-ready", HasRun: true, GateVerdict: "passed", HumanDecision: "merged");
        Assert.Equal(["done", "done", "done", "done", "current", "pending", "pending"], States(input));
    }

    [Fact]
    public void Label_verified_dev_completes_Verify_dev_and_makes_Verify_prod_current()
    {
        var input = new PipelineInput(Labels: ["release:1.4", "Verified:Dev"], HasRun: true, HumanDecision: "merged", TriageLabel: "agent-ready");
        Assert.Equal(["done", "done", "done", "done", "done", "current", "pending"], States(input));
    }

    [Fact]
    public void Label_verified_prod_completes_everything()
    {
        var input = new PipelineInput(Labels: ["verified:prod"], TriageLabel: "agent-ready", HasRun: true, HumanDecision: "merged");
        Assert.Equal(["done", "done", "done", "done", "done", "done", "done"], States(input));
    }

    [Fact]
    public void Verified_label_on_an_outside_item_implies_it_was_merged()
    {
        var input = new PipelineInput(Labels: ["verified:dev"]);
        Assert.Equal("done", States(input)[3]);
        Assert.Equal("current", States(input)[5]);
        Assert.True(PipelineSteps.IsOutsideAutopilot(input));
    }

    [Fact]
    public void Closed_tracker_state_makes_Done_done()
    {
        Assert.Equal("done", States(new PipelineInput(TrackerState: "Closed"))[6]);
        Assert.Equal("pending", States(new PipelineInput(TrackerState: "Active"))[6]);
    }

    [Fact]
    public void Release_comes_from_the_release_label()
    {
        Assert.Equal("1.4", PipelineSteps.ReleaseOf(["P1", "release:1.4"]));
        Assert.Null(PipelineSteps.ReleaseOf(["P1"]));
        Assert.Null(PipelineSteps.ReleaseOf(null));
    }

    [Fact]
    public void Stages_list_seven_nodes_with_the_sub_steps_of_each()
    {
        var stages = PipelineSteps.Stages(PipelineSteps.Dots(new PipelineInput(TriageLabel: "agent-ready", InPool: true)));
        Assert.Equal(["Start", "Qualify", "Implement", "Merge", "Verify dev", "Verify prod", "Done"], stages.Select(s => s.Step).ToArray());
        Assert.Equal([0, 3, 7, 7, 3, 3, 0], stages.Select(s => s.SubSteps.Count).ToArray());
        Assert.Equal(["Read Task", "Decide", "Comment Task"], stages[1].SubSteps.Select(x => x.Name).ToArray());
        Assert.Equal("Publish Pull Request", stages[2].SubSteps[6].Name);
        Assert.Equal(["Wait for deployment", "Test", "Comment Task"], stages[5].SubSteps.Select(x => x.Name).ToArray());
        Assert.Equal(["done", "done", "current", "pending", "pending", "pending", "pending"], stages.Select(s => s.State).ToArray());
    }

    [Fact]
    public void Stages_with_no_data_are_all_pending()
    {
        var stages = PipelineSteps.Stages([]);
        Assert.Equal(7, stages.Count);
        Assert.All(stages, s => Assert.Equal("pending", s.State));
        Assert.All(stages.SelectMany(s => s.SubSteps), x => Assert.Equal("pending", x.State));
    }

    [Fact]
    public void Card_of_an_entry_carries_dots_flag_release_and_action()
    {
        var entry = new WorkItemListEntry("7", "T", null, "Active", null, null, null, false, null, ["release:2.0"]);
        var card = PipelineSteps.CardOf(entry, false);
        Assert.True(card.OutsideAutopilot);
        Assert.Equal("2.0", card.Release);
        Assert.False(card.ActionDue);
        Assert.Equal(7, card.Dots.Count);
        Assert.NotNull(card.Stages);
        Assert.Equal(card.Dots.Select(d => d.State), card.Stages!.Select(s => s.State));
        Assert.Equal([0, 3, 7, 7, 3, 3, 0], card.Stages!.Select(s => s.SubSteps.Count));
        Assert.NotNull(card.Detail);
    }

    [Fact]
    public void Detail_with_no_data_has_an_empty_meta_line_and_dashes()
    {
        var detail = PipelineSteps.DetailOf(new WorkItemListEntry("7", "T", null, null, null, null, null, false, null), 0);
        Assert.Empty(detail.Meta);
        Assert.Equal("–", detail.Result);
        Assert.Equal("–", detail.Note);
    }

    [Fact]
    public void Detail_meta_lists_state_assignee_link_events_and_last_activity_and_skips_the_rest()
    {
        var run = new RunRecord(1, "p", "7", null, DateTimeOffset.Parse("2026-01-02T10:00:00Z"), DateTimeOffset.Parse("2026-01-02T10:05:00Z"),
            "succeeded", "passed", false, true, HumanDecision.Merged, null, null, "all good");
        var triage = new TriageSummary("agent-ready", "low", "Small and clear", DateTimeOffset.UtcNow, false);
        var detail = PipelineSteps.DetailOf(new WorkItemListEntry("7", "T", "http://x/7", "Active", null, triage, run, false, null), 2);
        Assert.Equal(["Active", "Open task", "2 events", "last activity 2026-01-02 10:05 UTC"], detail.Meta.Select(m => m.Text));
        Assert.Equal("http://x/7", detail.Meta[1].Href);
        Assert.Equal("succeeded · gate passed · person merged", detail.Result);
        Assert.Equal("Small and clear", detail.Note);
    }

    [Fact]
    public void Detail_note_falls_back_to_the_run_message()
    {
        var run = new RunRecord(1, "p", "7", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "failed", null, false, false, null, null, null, "build broke");
        var detail = PipelineSteps.DetailOf(new WorkItemListEntry("7", "T", null, null, null, null, run, false, null), 1);
        Assert.Equal("build broke", detail.Note);
        Assert.Contains("1 event", detail.Meta.Select(m => m.Text));
    }
}
