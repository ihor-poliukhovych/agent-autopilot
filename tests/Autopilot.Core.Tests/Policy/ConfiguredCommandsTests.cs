using System.Text.Json;
using Autopilot.Core.Config;
using Autopilot.Core.Policy;

namespace Autopilot.Core.Tests.Policy;

/// <summary>The hook always allows the project's own configured build and test command lines.</summary>
public class ConfiguredCommandsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "autopilot-policy-tests", "wt");

    private const string Build = "dotnet build \"Big Solution.sln\" -p:TreatWarningsAsErrors=true /clp:ErrorsOnly --property WarningLevel=4";
    private const string Test = "dotnet test \"Big Solution.sln\" --no-build -p:CollectCoverage=false";

    private static ToolPolicy Policy(IReadOnlyList<string>? configured, IReadOnlyList<string>? allowed = null) => new(
        AgentRole.Implementer,
        Root,
        ["Read", "Bash"],
        ["src/**"],
        [".autopilot/**"],
        allowed ?? ["dotnet build", "dotnet test", "git diff"],
        10,
        Path.Combine(Path.GetTempPath(), "autopilot-policy-tests", "counter"),
        configured);

    private static PolicyDecision Bash(string command, ToolPolicy policy) =>
        ToolPolicyEvaluator.Evaluate(new ToolCall("Bash", JsonSerializer.SerializeToElement(new { command })), policy, 1);

    [Theory]
    [InlineData(Build)]
    [InlineData(Test)]
    [InlineData("  " + Build + "  ")]
    public void The_exact_configured_line_is_allowed_even_with_quotes_and_property_options(string command)
    {
        var decision = Bash(command, Policy([Build, Test]));

        Assert.True(decision.Allowed, decision.Reason);
        Assert.Contains("configured build or test command", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_configured_list_the_same_line_is_denied()
    {
        Assert.False(Bash(Build, Policy(null)).Allowed);
        Assert.False(Bash(Build, Policy([])).Allowed);
    }

    [Theory]
    [InlineData(Test + " --filter Category=Loans", true)]
    [InlineData(Test + " --no-restore -v q", true)]
    [InlineData(Test + " --output bin2", false)]
    [InlineData(Test + " -p:Other=1", false)]
    [InlineData(Test + " --filter \"Name~Loans\"", false)]
    [InlineData(Test + " && echo hacked", false)]
    [InlineData(Test + "; echo hacked", false)]
    [InlineData(Test + " ../outside", false)]
    [InlineData(Test + " C:/elsewhere", false)]
    public void Extra_trailing_arguments_must_pass_the_normal_rules(string command, bool allowed)
    {
        Assert.Equal(allowed, Bash(command, Policy([Build, Test])).Allowed);
    }

    [Theory]
    [InlineData("dotnet build \"Other.sln\" -p:TreatWarningsAsErrors=true")]
    [InlineData("dotnet build \"Big Solution.sln\" -p:TreatWarningsAsErrors=false /clp:ErrorsOnly --property WarningLevel=4")]
    [InlineData("dotnet build \"Big Solution.sln\"")]
    [InlineData("dotnet build \"Big Solution.sln\" -p:TreatWarningsAsErrors=true /clp:ErrorsOnly --property WarningLevel=4x")]
    public void A_line_that_differs_from_the_configured_one_gets_the_normal_rules(string command)
    {
        Assert.False(Bash(command, Policy([Build, Test])).Allowed);
    }

    [Fact]
    public void Configured_commands_alone_turn_the_shell_on_for_exactly_them()
    {
        var policy = Policy([Build], allowed: []);

        Assert.True(Bash(Build, policy).Allowed);
        Assert.False(Bash("git status", policy).Allowed);
    }

    [Fact]
    public void A_configured_line_that_chains_commands_is_still_allowed_as_written()
    {
        // The project's reviewed config decides its own command lines; only exactly that line passes.
        const string chained = "dotnet build && dotnet test";
        var policy = Policy([chained]);

        Assert.True(Bash(chained, policy).Allowed);
        Assert.False(Bash("dotnet build && dotnet test && echo more", policy).Allowed);
    }
}
