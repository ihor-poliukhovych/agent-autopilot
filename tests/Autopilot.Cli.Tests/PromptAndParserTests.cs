using System.Text.Json;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Prompts;

namespace Autopilot.Cli.Tests;

public sealed class PromptAndParserTests
{
    private static AgentRunResult Text(string text) =>
        new(true, text, null, 1, TokenUsage.Zero, TimeSpan.Zero, null, null);

    [Theory]
    [InlineData(AgentRole.Triage, "needs-human")]
    [InlineData(AgentRole.Implementer, "never touch")]
    [InlineData(AgentRole.Reviewer, "request-changes")]
    public void System_prompts_and_schemas_are_embedded(AgentRole role, string expected)
    {
        Assert.Contains(expected, PromptLibrary.SystemPrompt(role), StringComparison.OrdinalIgnoreCase);
        using var schema = JsonDocument.Parse(PromptLibrary.OutputSchema(role));
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Implementer_prompt_lists_writable_paths_commands_and_findings()
    {
        var item = new WorkItem("001", "Fix", "Body text", []);
        var triage = new TriageResult(TriageLabel.AgentReady, "ok", ["returns 404"], RiskLevel.Low);

        var prompt = PromptLibrary.ImplementerPrompt(item, triage, ["src/**"], new CommandsConfig(), ["Missing test for 404"]);

        Assert.Contains("`src/**`", prompt);
        Assert.Contains("dotnet build", prompt);
        Assert.Contains("- returns 404", prompt);
        Assert.Contains("- Missing test for 404", prompt);
        Assert.Contains(".autopilot/", prompt);
    }

    [Fact]
    public void Reviewer_prompt_holds_item_and_diff_only()
    {
        var item = new WorkItem("001", "Fix", "Body text", []);
        var triage = new TriageResult(TriageLabel.AgentReady, "secret reason", ["returns 404"], RiskLevel.Low);

        var prompt = PromptLibrary.ReviewerPrompt(item, triage, "diff --git a/x b/x\n+new line\n");

        Assert.Contains("Body text", prompt);
        Assert.Contains("+new line", prompt);
        Assert.DoesNotContain("secret reason", prompt);
    }

    [Fact]
    public void Triage_from_structured_output()
    {
        var run = FakeAgentRunner.Answer(new { label = "agent-ready", reason = "Clear.", acceptanceCriteria = new[] { "a", " " }, risk = "medium" });

        Assert.True(OutputParser.TryParseTriage(run, out var triage, out _));
        Assert.Equal(TriageLabel.AgentReady, triage.Label);
        Assert.Equal("Clear.", triage.Reason);
        Assert.Equal(["a"], triage.AcceptanceCriteria);
        Assert.Equal(RiskLevel.Medium, triage.Risk);
    }

    [Fact]
    public void Agent_ready_without_criteria_becomes_needs_human()
    {
        var run = FakeAgentRunner.Answer(new { label = "agent-ready", reason = "Looks easy.", acceptanceCriteria = Array.Empty<string>(), risk = "low" });

        Assert.True(OutputParser.TryParseTriage(run, out var triage, out _));
        Assert.Equal(TriageLabel.NeedsHuman, triage.Label);
        Assert.Contains("no acceptance criteria", triage.Reason);
    }

    [Fact]
    public void Unknown_risk_is_high_and_bad_label_fails()
    {
        Assert.True(OutputParser.TryParseTriage(FakeAgentRunner.Answer(new { label = "needs-human", reason = "r", acceptanceCriteria = Array.Empty<string>(), risk = "?" }), out var triage, out _));
        Assert.Equal(RiskLevel.High, triage.Risk);

        Assert.False(OutputParser.TryParseTriage(FakeAgentRunner.Answer(new { label = "maybe" }), out _, out var error));
        Assert.Contains("maybe", error);
    }

    [Fact]
    public void Review_and_self_review_from_text_with_a_json_block()
    {
        Assert.True(OutputParser.TryParseReview(Text("Here you go:\n```json\n{\"verdict\": \"request-changes\", \"findings\": [\"src/A.cs: off by one\"]}\n```"), out var review, out _));
        Assert.Equal(ReviewVerdict.RequestChanges, review.Verdict);
        Assert.Equal(["src/A.cs: off by one"], review.Findings);

        Assert.True(OutputParser.TryParseSelfReview(Text("{\"summary\": \"Did it.\", \"testsThatProveIt\": [\"T1\"], \"doubts\": []}"), out var self, out _));
        Assert.Equal("Did it.", self.Summary);
        Assert.Equal(["T1"], self.TestsThatProveIt);

        Assert.False(OutputParser.TryParseReview(Text("no json here"), out _, out var error));
        Assert.Contains("no JSON", error);
    }
}
