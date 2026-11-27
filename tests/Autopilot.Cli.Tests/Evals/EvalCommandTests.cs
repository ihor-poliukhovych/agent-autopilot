using System.Text.Json;
using Autopilot.Core;
using Autopilot.Core.Config;

namespace Autopilot.Cli.Tests.Evals;

public sealed class EvalCommandTests
{
    private static CliContext Context(TestRepo repo, IAgentRunner runner) => new(TextReader.Null, new StringWriter(), new StringWriter())
    {
        RunnerFactory = () => runner,
        FactsCollectorFactory = _ => new EvalTestProject.PassingFacts(),
        EvaluateGate = EvalTestProject.Gate,
        WorktreeRoot = repo.WorktreeRoot,
    };

    [Fact]
    public async Task Eval_runs_the_cases_writes_the_report_and_exits_0_even_with_failures()
    {
        using var repo = await EvalTestProject.CreateAsync(new Dictionary<string, string>
        {
            [".autopilot/evals/e04-wrong/case.json"] = """{ "workItem": "003", "expect": { "triage": "agent-ready", "outcome": ["published"] } }""",
            [".autopilot/evals/e04-wrong/hidden/tests/acceptance.txt"] = "x\n",
        });
        var context = Context(repo, EvalTestProject.ScriptedRunner());

        var exit = await CliApp.RunAsync(["eval", "--runs", "1", "--project", repo.Root], context);

        var output = context.Out.ToString()!;
        Assert.True(exit == 0, output + context.Error);
        Assert.Contains("Eval: 4 case(s), 1 run(s) each", output);
        Assert.Contains("pass@1 75%", output);
        var folder = Assert.Single(Directory.GetDirectories(Path.Combine(repo.Root, ".autopilot", "out", "evals")));
        var markdown = File.ReadAllText(Path.Combine(folder, "report.md"));
        Assert.Contains("| e01-add | 1/1 | 100% | yes |", markdown);
        Assert.Contains("| e04-wrong | 0/1 | 0% | no |", markdown);
        Assert.Contains("### e04-wrong, run 1", markdown);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "report.json")));
        Assert.Equal(4, json.RootElement.GetProperty("cases").GetArrayLength());
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".autopilot", "out", "runs")));
    }

    [Fact]
    public async Task Eval_of_one_case_uses_the_case_option()
    {
        using var repo = await EvalTestProject.CreateAsync();
        var runner = EvalTestProject.ScriptedRunner();
        var context = Context(repo, runner);

        var exit = await CliApp.RunAsync(["eval", "--case", "e02-ask", "--runs", "2", "--project", repo.Root], context);

        Assert.Equal(0, exit);
        Assert.Equal(2, runner.Requests.Count);
        Assert.All(runner.Requests, r => Assert.Equal(AgentRole.Triage, r.Role));
        Assert.Contains("e02-ask", context.Out.ToString());
    }

    [Fact]
    public async Task Eval_exits_1_when_it_stops_on_an_infrastructure_error()
    {
        using var repo = await EvalTestProject.CreateAsync();
        var runner = EvalTestProject.ScriptedRunner(_ => new AgentRunResult(false, string.Empty, null, 0, TokenUsage.Zero, TimeSpan.Zero, null, "Claude AI usage limit reached|1764000000"));
        var context = Context(repo, runner);

        var exit = await CliApp.RunAsync(["eval", "--project", repo.Root], context);

        Assert.Equal(1, exit);
        Assert.Single(runner.Requests);
        Assert.Contains("Stopped early", context.Out.ToString());
        Assert.Contains("usage limit", context.Out.ToString());
    }

    [Theory]
    [InlineData("--case", "nope")]
    [InlineData("--runs", "0")]
    [InlineData("--runs", "abc")]
    public async Task Bad_options_are_usage_errors(string option, string value)
    {
        using var repo = await EvalTestProject.CreateAsync();
        var context = Context(repo, EvalTestProject.ScriptedRunner());

        var exit = await CliApp.RunAsync(["eval", option, value, "--project", repo.Root], context);

        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task Broken_cases_stop_the_eval_before_any_run()
    {
        using var repo = await EvalTestProject.CreateAsync(new Dictionary<string, string>
        {
            [".autopilot/evals/x01-bad/case.json"] = """{ "workItem": "404", "expect": { "triage": "agent-ready", "outcome": ["rejected"] } }""",
        });
        var runner = EvalTestProject.ScriptedRunner();
        var context = Context(repo, runner);

        var exit = await CliApp.RunAsync(["eval", "--project", repo.Root], context);
        var onlyGood = await CliApp.RunAsync(["eval", "--case", "e02-ask", "--runs", "1", "--project", repo.Root], Context(repo, runner));

        Assert.Equal(1, exit);
        Assert.Contains("x01-bad: Work item \"404\" is unknown", context.Error.ToString());
        Assert.Equal(0, onlyGood);
        Assert.Single(runner.Requests);
    }
}
