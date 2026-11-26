using DevAutopilot.Core.Config;
using DevAutopilot.Core.Gate;

namespace DevAutopilot.Core.Tests.Config;

public class ContextPathsTests
{
    [Theory]
    [InlineData("docs/agent-rules.md", "docs/agent-rules.md")]
    [InlineData("./docs/decisions/", "docs/decisions")]
    [InlineData("docs\\decisions\\", "docs/decisions")]
    public void Normalize_gives_forward_slashes_without_dots_or_trailing_slash(string input, string expected)
    {
        Assert.Equal(expected, ContextPaths.Normalize(input));
    }

    [Theory]
    [InlineData("docs/agent-rules.md")]
    [InlineData("docs/decisions/")]
    [InlineData(".autopilot/rules.md")]
    public void Plain_relative_paths_are_fine(string path)
    {
        Assert.Null(ContextPaths.Problem(path));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("/etc/rules.md", "absolute")]
    [InlineData("C:/rules.md", "absolute")]
    [InlineData("../shared/rules.md", "'..'")]
    [InlineData("docs/*.md", "wildcard")]
    [InlineData(".", "whole project")]
    public void Bad_entries_are_explained(string path, string expected)
    {
        Assert.Contains(expected, ContextPaths.Problem(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_lists_context_problems()
    {
        var problems = ConfigLoader.Validate(new AutopilotConfig { Context = ["docs/ok.md", "../x.md"] });

        Assert.Single(problems, p => p.Contains("context entry \"../x.md\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Context_defaults_to_empty_and_is_read_from_json()
    {
        Assert.Empty(new AutopilotConfig().Context);
        var config = ConfigLoader.Parse("""{ "context": ["docs/agent-rules.md", "docs/decisions/"] }""");
        Assert.Equal(["docs/agent-rules.md", "docs/decisions/"], config.Context);
    }

    [Fact]
    public void Protected_patterns_cover_the_file_and_everything_under_a_folder()
    {
        var patterns = ContextPaths.ProtectedPatterns(["docs/agent-rules.md", "docs/decisions/", "../bad"]);

        Assert.Equal(["docs/agent-rules.md", "docs/agent-rules.md/**", "docs/decisions", "docs/decisions/**"], patterns);
        Assert.True(DevAutopilot.Core.Paths.GlobMatcher.IsMatch("docs/decisions/0001-layers.md", patterns));
        Assert.True(DevAutopilot.Core.Paths.GlobMatcher.IsMatch("docs/agent-rules.md", patterns));
        Assert.False(DevAutopilot.Core.Paths.GlobMatcher.IsMatch("docs/other.md", patterns));
    }

    [Fact]
    public void Empty_writable_paths_say_what_to_fill_in()
    {
        var problem = Assert.Single(ConfigLoader.Validate(new AutopilotConfig { WritablePaths = [] }));

        Assert.Contains("writablePaths is empty. Fill in", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gate_rejects_a_change_to_a_context_file()
    {
        var config = new AutopilotConfig { WritablePaths = ["src/**", "docs/**", "tests/**"], RiskPaths = [], Context = ["docs/agent-rules.md"] };
        var evidence = new Evidence("42", new TriageResult(TriageLabel.AgentReady, "clear", ["works"], RiskLevel.Low),
            new SelfReview("did it", ["T.Works"], []), new ReviewResult(ReviewVerdict.Approve, []));
        var input = new GateInput(
            [new FileChange("docs/agent-rules.md", FileChangeKind.Modified, 1, 1), new FileChange("tests/T.cs", FileChangeKind.Modified, 2, 0)],
            new TestFacts(true, true, 10, 10, 0, "ok"),
            evidence);

        var decision = MergeGate.Evaluate(input, config);

        Assert.Equal(GateOutcome.Rejected, decision.Outcome);
        var finding = Assert.Single(decision.Findings, f => f.RuleId == "paths.protected");
        Assert.Contains("docs/agent-rules.md", finding.Message, StringComparison.Ordinal);
    }
}
