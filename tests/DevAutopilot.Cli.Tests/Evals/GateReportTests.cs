using System.Text.Json;
using DevAutopilot.Core;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Gate;
using DevAutopilot.Core.Local;
using DevAutopilot.Core.Pipeline;

namespace DevAutopilot.Cli.Tests.Evals;

public sealed class GateReportTests
{
    private static readonly Evidence Evidence = new(
        "001",
        new TriageResult(TriageLabel.AgentReady, "Clear.", ["returns 404"], RiskLevel.Low),
        new SelfReview("Return NotFound.", ["BooksTests.Missing"], []),
        new ReviewResult(ReviewVerdict.Approve, []));

    private static GateInput Input() => new(
        [
            new FileChange("src/Books.cs", FileChangeKind.Modified, 5, 2),
            new FileChange("tests/BooksTests.cs", FileChangeKind.Added, 20, 0),
            new FileChange("docs/logo.png", FileChangeKind.Added, 0, 0, Binary: true),
            new FileChange("src/New.cs", FileChangeKind.Renamed, 1, 1, OldPath: "src/Old.cs"),
        ],
        new TestFacts(true, true, 12, 13, 1, "log", TestCountsKnown: true),
        Evidence);

    [Fact]
    public void Report_holds_the_facts_not_just_the_outcome()
    {
        var decision = new GateDecision(GateOutcome.Rejected, [new GateFinding("scope.binary", "Binary files changed.", HighRisk: true)]);

        var report = GateReport.Create(Input(), decision, new AutopilotConfig());
        using var json = JsonDocument.Parse(report.ToJson());
        var root = json.RootElement;

        Assert.Equal("rejected", root.GetProperty("outcome").GetString());
        Assert.True(root.GetProperty("highRisk").GetBoolean());
        Assert.Equal("scope.binary", root.GetProperty("findings")[0].GetProperty("ruleId").GetString());
        Assert.Equal(4, root.GetProperty("filesChanged").GetInt32());
        Assert.Equal(9, root.GetProperty("sourceLinesChanged").GetInt32());
        Assert.Equal(20, root.GetProperty("testLinesChanged").GetInt32());
        Assert.True(root.GetProperty("buildSucceeded").GetBoolean());
        Assert.True(root.GetProperty("testsSucceeded").GetBoolean());
        Assert.Equal(12, root.GetProperty("testCountBefore").GetInt32());
        Assert.Equal(13, root.GetProperty("testCountAfter").GetInt32());
        Assert.Equal(1, root.GetProperty("skipsAdded").GetInt32());
        Assert.True(root.GetProperty("testCountsKnown").GetBoolean());

        var files = root.GetProperty("files");
        Assert.Equal("tests/BooksTests.cs", files[1].GetProperty("path").GetString());
        Assert.Equal("added", files[1].GetProperty("kind").GetString());
        Assert.True(files[1].GetProperty("test").GetBoolean());
        Assert.Equal(20, files[1].GetProperty("added").GetInt32());
        Assert.True(files[2].GetProperty("binary").GetBoolean());
        Assert.Equal("src/Old.cs", files[3].GetProperty("oldPath").GetString());

        var evidence = root.GetProperty("evidence");
        Assert.Equal("agent-ready", evidence.GetProperty("triage").GetString());
        Assert.True(evidence.GetProperty("selfReviewPresent").GetBoolean());
        Assert.Equal("approve", evidence.GetProperty("reviewVerdict").GetString());
        Assert.Equal(20, evidence.GetProperty("testLinesAdded").GetInt32());

        var table = report.FactsTable();
        Assert.Contains("| Files changed | 4 (1 binary) |", table);
        Assert.Contains("| Test count (base -> branch) | 12 -> 13 |", table);
        Assert.Contains("| Tests newly skipped | 1 |", table);
        Assert.Contains("| `src/Old.cs -> src/New.cs` | renamed | 1 | 1 |", table);
        Assert.Contains("| `docs/logo.png` | added | binary | binary |", table);
    }

    [Fact]
    public void Unknown_test_counts_are_said_plainly()
    {
        var input = Input() with { Tests = new TestFacts(true, false, 0, 0, 0, "log", TestCountsKnown: false) };

        var table = GateReport.Create(input, new GateDecision(GateOutcome.Rejected, []), new AutopilotConfig()).FactsTable();

        Assert.Contains("| Test count (base -> branch) | could not be counted |", table);
        Assert.Contains("| Tests on a clean checkout | failed |", table);
    }

    [Fact]
    public async Task Publisher_writes_the_facts_to_gate_json_and_PULL_REQUEST_md()
    {
        using var temp = new TempFolder();
        var decision = new GateDecision(GateOutcome.Passed, []);
        var report = GateReport.Create(Input(), decision, new AutopilotConfig());

        var result = await new LocalPublisher(temp.Path).PublishAsync(
            new PublishRequest(new WorkItem("001", "Fix 404", "body", []), "autopilot/001", "diff", decision, Evidence, report), CancellationToken.None);

        using var gate = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.Location, "gate.json")));
        Assert.Equal("passed", gate.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(13, gate.RootElement.GetProperty("testCountAfter").GetInt32());
        var pr = File.ReadAllText(Path.Combine(result.Location, "PULL_REQUEST.md"));
        Assert.Contains("## Gate facts", pr);
        Assert.Contains("| Lines changed outside tests | 9 |", pr);
    }

    [Fact]
    public async Task Rejection_writes_gate_json_with_facts_and_a_table_in_REJECTED_md()
    {
        using var repo = await EvalTestProject.CreateAsync();
        var pipeline = new AutopilotPipeline(new AutopilotPipelineOptions
        {
            ProjectRoot = repo.Root,
            Config = EvalTestProject.Config,
            WorkItems = new LocalWorkItemSource(repo.Root),
            Runner = EvalTestProject.ScriptedRunner(),
            Publisher = new LocalPublisher(repo.Root),
            Git = repo.Git,
            Facts = new EvalTestProject.PassingFacts(),
            WorktreeRoot = repo.WorktreeRoot,
            EvaluateGate = (_, _) => new GateDecision(GateOutcome.Rejected, [new GateFinding("scope.files", "Too many files.")]),
        });

        await pipeline.TriageAsync("001", CancellationToken.None);
        var result = await pipeline.ImplementAsync("001", CancellationToken.None);

        Assert.Equal(ImplementOutcome.Rejected, result.Outcome);
        var folder = ProjectPaths.RunFolder(repo.Root, "001");
        using var gate = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "gate.json")));
        Assert.Equal("rejected", gate.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(2, gate.RootElement.GetProperty("filesChanged").GetInt32());
        Assert.Equal(4, gate.RootElement.GetProperty("testCountBefore").GetInt32());
        var rejected = File.ReadAllText(Path.Combine(folder, "REJECTED.md"));
        Assert.Contains("## Gate facts", rejected);
        Assert.Contains("| `tests/CalcTests.cs` | added, test | 1 | 0 |", rejected);
    }
}
