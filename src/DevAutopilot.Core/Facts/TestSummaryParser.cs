using System.Text.RegularExpressions;

namespace DevAutopilot.Core.Facts;

/// <summary>Test counts read from the output of a test command.</summary>
public sealed record TestSummary(bool Found, int Total, int Failed, int Passed, int Skipped)
{
    public static TestSummary None { get; } = new(false, 0, 0, 0, 0);
}

/// <summary>
/// Reads <c>dotnet test</c> summaries. Supports the VSTest line, one per test project
/// (<c>Passed!  - Failed: 0, Passed: 12, Skipped: 1, Total: 13</c>, also <c>Failed!</c>), and the
/// Microsoft.Testing.Platform line (<c>Test summary: total: 13, failed: 0, succeeded: 12, skipped: 1</c>).
/// Lines of one format are summed; when both formats appear the VSTest lines are used.
/// </summary>
public static partial class TestSummaryParser
{
    public static TestSummary Parse(string output)
    {
        var text = AnsiEscape().Replace(output ?? string.Empty, string.Empty);

        var vstest = VsTestLine().Matches(text);
        if (vstest.Count > 0)
        {
            int failed = 0, passed = 0, skipped = 0, total = 0;
            foreach (Match match in vstest)
            {
                failed += int.Parse(match.Groups["failed"].Value);
                passed += int.Parse(match.Groups["passed"].Value);
                skipped += int.Parse(match.Groups["skipped"].Value);
                total += int.Parse(match.Groups["total"].Value);
            }

            return new TestSummary(true, total, failed, passed, skipped);
        }

        var platform = PlatformLine().Matches(text);
        if (platform.Count > 0)
        {
            int failed = 0, passed = 0, skipped = 0, total = 0;
            foreach (Match match in platform)
            {
                total += int.Parse(match.Groups["total"].Value);
                failed += int.Parse(match.Groups["failed"].Value);
                passed += int.Parse(match.Groups["succeeded"].Value);
                skipped += int.Parse(match.Groups["skipped"].Value);
            }

            return new TestSummary(true, total, failed, passed, skipped);
        }

        return TestSummary.None;
    }

    [GeneratedRegex(@"(?:Passed|Failed)!\s*-\s*Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+),\s*Skipped:\s*(?<skipped>\d+),\s*Total:\s*(?<total>\d+)")]
    private static partial Regex VsTestLine();

    [GeneratedRegex(@"Test summary:\s*total:\s*(?<total>\d+),\s*failed:\s*(?<failed>\d+),\s*succeeded:\s*(?<succeeded>\d+),\s*skipped:\s*(?<skipped>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PlatformLine();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiEscape();
}
