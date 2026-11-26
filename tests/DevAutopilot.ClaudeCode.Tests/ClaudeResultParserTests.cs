namespace DevAutopilot.ClaudeCode.Tests;

/// <summary>Fixtures are real outputs of Claude Code 2.1.332, with ids and local paths anonymised.</summary>
public class ClaudeResultParserTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(4);

    [Fact]
    public void Success_fixture()
    {
        var result = ClaudeResultParser.Parse(TestData.Fixture("result-success.json"), "", 0, Duration, expectStructuredOutput: false);

        Assert.True(result.Succeeded, result.Error);
        Assert.Null(result.Error);
        Assert.Equal("OK", result.ResultText);
        Assert.Equal(1, result.Turns);
        Assert.Equal(new DevAutopilot.Core.TokenUsage(2, 4, 30062, 8926), result.Usage);
        Assert.Equal("claude-haiku-5-5", result.Model);
        Assert.Equal(Duration, result.Duration);
        Assert.Null(result.StructuredOutput);
    }

    [Fact]
    public void Structured_output_fixture()
    {
        var result = ClaudeResultParser.Parse(TestData.Fixture("result-structured.json"), "", 0, Duration, expectStructuredOutput: true);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.Turns);
        Assert.NotNull(result.StructuredOutput);
        Assert.Equal("agent-ready", result.StructuredOutput!.Value.GetProperty("label").GetString());
        Assert.Equal("tiny", result.StructuredOutput!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public void Error_fixture_is_not_a_success_even_though_subtype_is_success()
    {
        var result = ClaudeResultParser.Parse(TestData.Fixture("result-error-model.json"), "", 1, Duration, expectStructuredOutput: false);

        Assert.False(result.Succeeded);
        Assert.Contains("api_error", result.Error);
        Assert.Contains("claude-no-such-model-1", result.Error);
        Assert.Null(result.Model);
        Assert.Equal(DevAutopilot.Core.TokenUsage.Zero, result.Usage);
    }

    [Fact]
    public void Hook_denied_fixture_still_succeeds_as_a_run()
    {
        var result = ClaudeResultParser.Parse(TestData.Fixture("result-hook-denied.json"), "", 0, Duration, expectStructuredOutput: false);

        Assert.True(result.Succeeded, result.Error);
        Assert.StartsWith("DENIED", result.ResultText);
    }

    [Fact]
    public void Missing_structured_output_fails_when_a_schema_was_given()
    {
        var result = ClaudeResultParser.Parse(TestData.Fixture("result-success.json"), "", 0, Duration, expectStructuredOutput: true);

        Assert.False(result.Succeeded);
        Assert.Contains("structured_output", result.Error);
    }

    [Fact]
    public void Non_success_subtype_fails()
    {
        const string json = """{"type":"result","subtype":"error_max_turns","is_error":false,"num_turns":5,"result":""}""";
        var result = ClaudeResultParser.Parse(json, "", 0, Duration, false);

        Assert.False(result.Succeeded);
        Assert.Contains("error_max_turns", result.Error);
        Assert.Equal(5, result.Turns);
    }

    [Fact]
    public void Non_zero_exit_with_success_json_fails()
    {
        var result = ClaudeResultParser.Parse(TestData.Fixture("result-success.json"), "boom", 3, Duration, false);

        Assert.False(result.Succeeded);
        Assert.Contains("code 3", result.Error);
    }

    [Fact]
    public void Unparsable_output_fails_with_the_first_2000_characters()
    {
        string garbage = "not json " + new string('x', 5000);
        var result = ClaudeResultParser.Parse(garbage, "", 1, Duration, false);

        Assert.False(result.Succeeded);
        Assert.Contains("not a Claude Code JSON result", result.Error);
        Assert.Contains(garbage[..2000], result.Error);
        Assert.DoesNotContain(garbage[..2001], result.Error);
    }

    [Fact]
    public void Empty_output_reports_stderr()
    {
        var result = ClaudeResultParser.Parse("", "error: unknown option '--x'", 1, Duration, false);

        Assert.False(result.Succeeded);
        Assert.Contains("no output", result.Error);
        Assert.Contains("unknown option", result.Error);
    }

    [Fact]
    public void Stray_lines_around_the_result_are_tolerated()
    {
        string stdout = "Warning: something\n" + TestData.Fixture("result-success.json");
        var result = ClaudeResultParser.Parse(stdout, "", 0, Duration, false);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("OK", result.ResultText);
    }

    [Fact]
    public void Top_level_model_wins_over_model_usage()
    {
        const string json = """{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"x","model":"m1","modelUsage":{"m2":{"outputTokens":9}}}""";
        Assert.Equal("m1", ClaudeResultParser.Parse(json, "", 0, Duration, false).Model);
    }

    [Fact]
    public void Model_with_most_output_tokens_is_reported()
    {
        const string json = """{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"x","modelUsage":{"small":{"outputTokens":3},"main":{"outputTokens":90}}}""";
        Assert.Equal("main", ClaudeResultParser.Parse(json, "", 0, Duration, false).Model);
    }
}
