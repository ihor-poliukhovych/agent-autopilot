using Autopilot.Core.Facts;

namespace Autopilot.Cli.Tests;

public sealed class TestSummaryParserTests
{
    [Fact]
    public void Sums_vstest_lines_over_projects_including_failed()
    {
        var output = """
            Build succeeded.
            Passed!  - Failed:     0, Passed:    12, Skipped:     1, Total:    13, Duration: 1 s - Library.Tests.dll (net10.0)
            Failed!  - Failed:     2, Passed:     3, Skipped:     0, Total:     5, Duration: 300 ms - Api.Tests.dll (net10.0)
            """;

        var summary = TestSummaryParser.Parse(output);

        Assert.True(summary.Found);
        Assert.Equal(18, summary.Total);
        Assert.Equal(2, summary.Failed);
        Assert.Equal(15, summary.Passed);
        Assert.Equal(1, summary.Skipped);
    }

    [Fact]
    public void Reads_testing_platform_summary()
    {
        var output = """
            Running tests from Library.Tests.dll
            Test summary: total: 13, failed: 0, succeeded: 12, skipped: 1, duration: 2.1s
            Test run succeeded.
            """;

        var summary = TestSummaryParser.Parse(output);

        Assert.True(summary.Found);
        Assert.Equal(13, summary.Total);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(12, summary.Passed);
        Assert.Equal(1, summary.Skipped);
    }

    [Fact]
    public void Ignores_ansi_colour_codes()
    {
        var summary = TestSummaryParser.Parse("\u001b[32;1mPassed!\u001b[0m  - Failed: 0, Passed: 4, Skipped: 0, Total: 4");
        Assert.Equal(4, summary.Total);
    }

    [Fact]
    public void Returns_none_when_there_is_no_summary()
    {
        var summary = TestSummaryParser.Parse("error CS1002: ; expected");
        Assert.False(summary.Found);
        Assert.Equal(0, summary.Total);
    }
}
