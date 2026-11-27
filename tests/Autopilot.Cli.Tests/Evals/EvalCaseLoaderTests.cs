using Autopilot.Core;
using Autopilot.Core.Evals;
using Autopilot.Core.Local;

namespace Autopilot.Cli.Tests.Evals;

public sealed class EvalCaseLoaderTests
{
    [Fact]
    public async Task Loads_valid_cases_with_the_default_of_three_runs()
    {
        using var repo = await EvalTestProject.CreateAsync();

        var set = await EvalTestProject.LoadCasesAsync(repo.Root);

        Assert.Empty(set.Problems);
        Assert.Equal(3, set.DefaultRuns);
        Assert.Equal(["e01-add", "e02-ask", "e03-trap"], set.Cases.Select(c => c.Id));
        var first = set.Cases[0];
        Assert.Equal("001", first.WorkItem);
        Assert.Equal("Add numbers", first.Description);
        Assert.Equal(TriageLabel.AgentReady, first.Expect.Triage);
        Assert.Equal([EvalOutcome.Published], first.Expect.Outcomes);
        Assert.True(Directory.Exists(first.HiddenFolder));
    }

    [Fact]
    public async Task Case_set_file_sets_the_default_runs()
    {
        using var repo = await EvalTestProject.CreateAsync(new Dictionary<string, string> { [".autopilot/evals/evals.json"] = "{ \"runs\": 5 }" });

        var set = await EvalTestProject.LoadCasesAsync(repo.Root);

        Assert.Equal(5, set.DefaultRuns);
        Assert.Empty(set.Problems);
    }

    [Theory]
    [InlineData("""{ "workItem": "404", "expect": { "triage": "agent-ready", "outcome": ["rejected"] } }""", "Work item \"404\" is unknown")]
    [InlineData("""{ "workItem": "001", "expect": { "triage": "maybe", "outcome": ["rejected"] } }""", "\"expect.triage\" is \"maybe\"")]
    [InlineData("""{ "workItem": "001", "expect": { "triage": "agent-ready", "outcome": ["merged"] } }""", "\"expect.outcome\" has \"merged\"")]
    [InlineData("""{ "workItem": "001", "expect": { "triage": "agent-ready", "outcome": [] } }""", "\"expect.outcome\" is missing or empty")]
    [InlineData("""{ "workItem": "001", "expect": { "triage": "agent-ready", "outcome": ["published"] } }""", "needs a hidden/ folder")]
    [InlineData("""{ "workItem": "002", "expect": { "triage": "needs-human", "outcome": ["rejected"] } }""", "only possible outcome is \"needs-human\"")]
    [InlineData("""{ "workItem": "001", "expect": { "triage": "agent-ready", "outcome": ["needs-human"] } }""", "cannot be \"needs-human\"")]
    [InlineData("""{ "id": "other", "workItem": "001", "expect": { "triage": "agent-ready", "outcome": ["rejected"] } }""", "must be the same")]
    [InlineData("""{ "expect": { "triage": "agent-ready", "outcome": ["rejected"] } }""", "\"workItem\" is missing")]
    [InlineData("""{ "workItem": "001" }""", "\"expect\" is missing")]
    [InlineData("""{ not json""", "not valid JSON")]
    public async Task Bad_cases_are_reported_and_left_out(string json, string expected)
    {
        using var repo = await EvalTestProject.CreateAsync(new Dictionary<string, string> { [".autopilot/evals/x01-bad/case.json"] = json });

        var set = await EvalTestProject.LoadCasesAsync(repo.Root);

        var problem = Assert.Single(set.Problems);
        Assert.Equal("x01-bad", problem.CaseId);
        Assert.Contains(expected, problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(set.Cases, c => c.Id == "x01-bad");
        Assert.Equal(3, set.Cases.Count);
    }

    [Fact]
    public async Task Folder_without_case_json_is_a_problem()
    {
        using var repo = await EvalTestProject.CreateAsync(new Dictionary<string, string> { [".autopilot/evals/x02-empty/readme.txt"] = "nothing" });

        var set = await EvalTestProject.LoadCasesAsync(repo.Root);

        Assert.Contains(set.Problems, p => p.CaseId == "x02-empty" && p.Message.Contains("no case.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_evals_folder_means_no_cases()
    {
        using var temp = new TempFolder();

        var set = await EvalCaseLoader.LoadAsync(temp.Path, new LocalWorkItemSource(temp.Path), CancellationToken.None);

        Assert.Empty(set.Cases);
        Assert.Empty(set.Problems);
    }
}
