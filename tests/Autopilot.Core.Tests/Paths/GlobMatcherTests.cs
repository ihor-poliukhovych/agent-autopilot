using Autopilot.Core.Paths;

namespace Autopilot.Core.Tests.Paths;

public class GlobMatcherTests
{
    [Theory]
    [InlineData(@"src\App\Program.cs", "src/App/Program.cs")]
    [InlineData("./src/a.cs", "src/a.cs")]
    [InlineData("/src/a.cs", "src/a.cs")]
    [InlineData(@".\src\a.cs", "src/a.cs")]
    [InlineData("src//App///a.cs", "src/App/a.cs")]
    [InlineData(@"src\\App\a.cs", "src/App/a.cs")]
    [InlineData("././/src/a.cs", "src/a.cs")]
    [InlineData("README.md", "README.md")]
    [InlineData("", "")]
    public void Normalize_produces_forward_slash_project_relative_path(string input, string expected)
    {
        Assert.Equal(expected, GlobMatcher.Normalize(input));
    }

    [Theory]
    [InlineData("src/App/Program.cs", "src/**", true)]
    [InlineData(@"src\App\Program.cs", "src/**", true)]
    [InlineData("SRC/App/Program.cs", "src/**", true)]
    [InlineData("docs/readme.md", "src/**", false)]
    [InlineData("srcx/a.cs", "src/**", false)]
    [InlineData("appsettings.json", "**/appsettings*.json", true)]
    [InlineData("src/Api/appsettings.Development.json", "**/appsettings*.json", true)]
    [InlineData("Directory.Build.props", "**/Directory.Build.*", true)]
    [InlineData("src/AuthService/Login.cs", "**/*Auth*/**", true)]
    [InlineData("src/Users/UserAuthHandler.cs", "**/*Auth*.cs", true)]
    [InlineData("src/Users/User.cs", "**/*Auth*.cs", false)]
    [InlineData(".autopilot/autopilot.json", ".autopilot/**", true)]
    [InlineData("./.autopilot/tasks/1.md", ".autopilot/**", true)]
    [InlineData("tests/Foo/BarTests.cs", "**/*Tests.cs", true)]
    [InlineData("FooTests.cs", "**/*Tests.cs", true)]
    public void IsMatch_matches_globs_case_insensitively(string path, string pattern, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(path, [pattern]));
    }

    [Fact]
    public void IsMatch_is_true_when_any_pattern_matches()
    {
        Assert.True(GlobMatcher.IsMatch("tests/a.cs", ["src/**", "tests/**"]));
    }

    [Fact]
    public void IsMatch_is_false_for_no_patterns_or_empty_path()
    {
        Assert.False(GlobMatcher.IsMatch("src/a.cs", []));
        Assert.False(GlobMatcher.IsMatch("", ["**"]));
        Assert.False(GlobMatcher.IsMatch("src/a.cs", ["", "  "]));
    }

    [Fact]
    public void IsMatch_accepts_patterns_written_with_backslashes()
    {
        Assert.True(GlobMatcher.IsMatch("src/a.cs", [@"src\**"]));
    }
}
