using Autopilot.Core.Config;
using Autopilot.Core.Gate;

namespace Autopilot.Core.Tests.Gate;

public class MergeGateTests
{
    private static readonly AutopilotConfig Config = new();

    private static FileChange Source(string path = "src/App/Service.cs", int added = 10, int removed = 2) =>
        new(path, FileChangeKind.Modified, added, removed);

    private static FileChange Test(string path = "tests/App.Tests/ServiceTests.cs", int added = 20, int removed = 0) =>
        new(path, FileChangeKind.Modified, added, removed);

    private static TestFacts GoodTests() => new(true, true, 10, 11, 0, "ok");

    private static Evidence GoodEvidence() => new(
        "42",
        new TriageResult(TriageLabel.AgentReady, "clear", ["works"], RiskLevel.Low),
        new SelfReview("did it", ["ServiceTests.Works"], []),
        new ReviewResult(ReviewVerdict.Approve, []));

    private static GateInput Clean(
        IReadOnlyList<FileChange>? changes = null,
        TestFacts? tests = null,
        Evidence? evidence = null) =>
        new(changes ?? [Source(), Test()], tests ?? GoodTests(), evidence ?? GoodEvidence());

    private static GateDecision Run(GateInput input, AutopilotConfig? config = null) =>
        MergeGate.Evaluate(input, config ?? Config);

    private static IReadOnlyList<string> Rules(GateDecision decision) =>
        decision.Findings.Select(f => f.RuleId).ToList();

    private static GateFinding Single(GateDecision decision, string ruleId)
    {
        Assert.Equal(GateOutcome.Rejected, decision.Outcome);
        return Assert.Single(decision.Findings, f => f.RuleId == ruleId);
    }

    [Fact]
    public void Clean_change_passes()
    {
        var decision = Run(Clean());

        Assert.Equal(GateOutcome.Passed, decision.Outcome);
        Assert.Empty(decision.Findings);
        Assert.False(decision.HighRisk);
    }

    [Fact]
    public void Protected_paths_cover_autopilot_folder()
    {
        Assert.Contains(".autopilot/**", MergeGate.ProtectedPaths);
    }

    // ---------- scope.files ----------

    [Fact]
    public void Too_many_files_is_rejected()
    {
        var changes = Enumerable.Range(1, 6).Select(i => Source($"src/F{i}.cs", 1, 0)).Append(Test()).ToList();

        var finding = Single(Run(Clean(changes)), "scope.files");
        Assert.Contains("7", finding.Message);
        Assert.Contains("6", finding.Message);
    }

    [Fact]
    public void Files_at_the_limit_pass()
    {
        var changes = Enumerable.Range(1, 5).Select(i => Source($"src/F{i}.cs", 1, 0)).Append(Test()).ToList();

        Assert.DoesNotContain("scope.files", Rules(Run(Clean(changes))));
    }

    // ---------- scope.source-lines / scope.test-lines ----------

    [Fact]
    public void Too_many_source_lines_is_rejected()
    {
        var finding = Single(Run(Clean([Source(added: 100, removed: 51), Test()])), "scope.source-lines");
        Assert.Contains("151", finding.Message);
        Assert.Contains("150", finding.Message);
    }

    [Fact]
    public void Source_lines_at_the_limit_pass_and_test_lines_do_not_count()
    {
        var decision = Run(Clean([Source(added: 100, removed: 50), Test(added: 290)]));

        Assert.Equal(GateOutcome.Passed, decision.Outcome);
    }

    [Fact]
    public void Too_many_test_lines_is_rejected()
    {
        var finding = Single(Run(Clean([Source(), Test(added: 200, removed: 101)])), "scope.test-lines");
        Assert.Contains("301", finding.Message);
        Assert.Contains("300", finding.Message);
    }

    [Fact]
    public void Test_paths_from_config_decide_what_a_test_is()
    {
        var config = Config with { Gate = Config.Gate with { TestPaths = ["specs/**"], MaxSourceLinesChanged = 5 } };
        var changes = new[] { Source(added: 1, removed: 0), new FileChange("tests/X.cs", FileChangeKind.Modified, 50, 0) };

        var decision = MergeGate.Evaluate(Clean(changes), config);

        Assert.Contains("scope.source-lines", Rules(decision));
        Assert.Contains("evidence.test-change", Rules(decision));
    }

    // ---------- paths.allowed ----------

    [Fact]
    public void Change_outside_writable_paths_is_rejected()
    {
        var finding = Single(Run(Clean([Source(), Test(), new("docs/notes.md", FileChangeKind.Added, 3, 0)])), "paths.allowed");
        Assert.Contains("docs/notes.md", finding.Message);
    }

    [Fact]
    public void Windows_style_paths_inside_writable_paths_pass()
    {
        var decision = Run(Clean([Source(@"src\App\Service.cs"), Test(@".\tests\App.Tests\ServiceTests.cs")]));

        Assert.Equal(GateOutcome.Passed, decision.Outcome);
    }

    [Fact]
    public void Rename_from_outside_writable_paths_is_rejected()
    {
        var rename = new FileChange("src/Moved.cs", FileChangeKind.Renamed, 0, 0, OldPath: "tools/Original.cs");

        var finding = Single(Run(Clean([rename, Test()])), "paths.allowed");
        Assert.Contains("tools/Original.cs", finding.Message);
        Assert.DoesNotContain("src/Moved.cs", finding.Message);
    }

    [Fact]
    public void Rename_inside_writable_paths_passes()
    {
        var rename = new FileChange("src/Moved.cs", FileChangeKind.Renamed, 0, 0, OldPath: "src/Original.cs");

        Assert.Equal(GateOutcome.Passed, Run(Clean([rename, Test()])).Outcome);
    }

    [Fact]
    public void Deleting_a_file_outside_writable_paths_is_rejected()
    {
        var decision = Run(Clean([Source(), Test(), new("build.ps1", FileChangeKind.Deleted, 0, 10)]));

        Assert.Contains("build.ps1", Single(decision, "paths.allowed").Message);
    }

    // ---------- paths.protected ----------

    [Fact]
    public void Change_to_autopilot_folder_is_rejected_even_when_writable()
    {
        var config = Config with { WritablePaths = ["**"] };
        var change = new FileChange(".autopilot/autopilot.json", FileChangeKind.Modified, 1, 1);

        var decision = MergeGate.Evaluate(Clean([Source(), Test(), change]), config);

        Assert.Contains(".autopilot/autopilot.json", Single(decision, "paths.protected").Message);
        Assert.DoesNotContain("paths.allowed", Rules(decision));
    }

    [Fact]
    public void Rename_out_of_autopilot_folder_is_rejected()
    {
        var config = Config with { WritablePaths = ["**"] };
        var rename = new FileChange("src/rules.json", FileChangeKind.Renamed, 0, 0, OldPath: @".autopilot\autopilot.json");

        var decision = MergeGate.Evaluate(Clean([rename, Test()]), config);

        Assert.Contains("paths.protected", Rules(decision));
    }

    [Fact]
    public void Protected_check_is_case_insensitive()
    {
        var config = Config with { WritablePaths = ["**"] };
        var decision = MergeGate.Evaluate(Clean([Source(), Test(), new(".AUTOPILOT/tasks/1.md", FileChangeKind.Added, 1, 0)]), config);

        Assert.Contains("paths.protected", Rules(decision));
    }

    // ---------- paths.risk ----------

    [Fact]
    public void Risk_path_is_rejected_as_high_risk()
    {
        var decision = Run(Clean([Source("src/App/App.csproj", 1, 0), Test()]));

        var finding = Single(decision, "paths.risk");
        Assert.True(finding.HighRisk);
        Assert.True(decision.HighRisk);
        Assert.Contains("src/App/App.csproj", finding.Message);
    }

    [Fact]
    public void Risk_path_at_root_is_rejected()
    {
        var decision = Run(Clean([Source(), Test()]), Config with { WritablePaths = ["**"] });
        Assert.Equal(GateOutcome.Passed, decision.Outcome);

        var risky = Run(Clean([Source(), Test(), new("appsettings.json", FileChangeKind.Modified, 1, 1)]),
            Config with { WritablePaths = ["**"] });
        Assert.True(Single(risky, "paths.risk").HighRisk);
    }

    [Fact]
    public void Non_risk_findings_are_not_high_risk()
    {
        var decision = Run(Clean(tests: GoodTests() with { BuildSucceeded = false }));

        Assert.False(decision.HighRisk);
    }

    // ---------- tests.* ----------

    [Fact]
    public void Failed_build_is_rejected()
    {
        Single(Run(Clean(tests: GoodTests() with { BuildSucceeded = false })), "tests.build");
    }

    [Fact]
    public void Failed_tests_are_rejected()
    {
        Single(Run(Clean(tests: GoodTests() with { TestsSucceeded = false })), "tests.pass");
    }

    [Fact]
    public void Dropping_test_count_is_rejected()
    {
        var finding = Single(Run(Clean(tests: GoodTests() with { TestCountBefore = 12, TestCountAfter = 11 })), "tests.count");
        Assert.Contains("12", finding.Message);
        Assert.Contains("11", finding.Message);
    }

    [Fact]
    public void Same_test_count_passes()
    {
        Assert.Equal(GateOutcome.Passed, Run(Clean(tests: GoodTests() with { TestCountBefore = 11, TestCountAfter = 11 })).Outcome);
    }

    [Fact]
    public void Added_skips_are_rejected()
    {
        Assert.Contains("2", Single(Run(Clean(tests: GoodTests() with { SkipsAdded = 2 })), "tests.skips").Message);
    }

    [Fact]
    public void Deleted_test_file_is_rejected()
    {
        var deleted = new FileChange("tests/App.Tests/OldTests.cs", FileChangeKind.Deleted, 0, 40);

        var finding = Single(Run(Clean([Source(), Test(), deleted])), "tests.deleted");
        Assert.Contains("tests/App.Tests/OldTests.cs", finding.Message);
    }

    [Fact]
    public void Deleted_source_file_is_not_a_deleted_test()
    {
        var deleted = new FileChange("src/App/Old.cs", FileChangeKind.Deleted, 0, 10);

        Assert.DoesNotContain("tests.deleted", Rules(Run(Clean([Source(), Test(), deleted]))));
    }

    [Fact]
    public void Renaming_a_test_file_out_of_test_paths_counts_as_deleted()
    {
        var rename = new FileChange("src/App/Hidden.cs", FileChangeKind.Renamed, 0, 0, OldPath: "tests/App.Tests/HiddenTests.cs");

        Assert.Contains("tests.deleted", Rules(Run(Clean([rename, Test()]))));
    }

    [Fact]
    public void Renaming_a_test_file_within_test_paths_is_fine()
    {
        var rename = new FileChange("tests/App.Tests/NewTests.cs", FileChangeKind.Renamed, 2, 0, OldPath: "tests/App.Tests/OldTests.cs");
        var pureRename = rename with { AddedLines = 0 };

        Assert.Equal(GateOutcome.Passed, Run(Clean([Source(), rename])).Outcome);

        // A rename with no added line is not a test change (evidence.test-change), but never a deletion.
        Assert.Equal(["evidence.test-change"], Rules(Run(Clean([Source(), pureRename]))));
    }

    // ---------- evidence.* ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_work_item_is_rejected(string id)
    {
        Single(Run(Clean(evidence: GoodEvidence() with { WorkItemId = id })), "evidence.work-item");
    }

    [Fact]
    public void Missing_triage_is_rejected()
    {
        Single(Run(Clean(evidence: GoodEvidence() with { Triage = null })), "evidence.triage");
    }

    [Fact]
    public void Triage_needs_human_is_rejected()
    {
        var triage = new TriageResult(TriageLabel.NeedsHuman, "unclear", [], RiskLevel.Medium);

        Assert.Contains("NeedsHuman", Single(Run(Clean(evidence: GoodEvidence() with { Triage = triage })), "evidence.triage").Message);
    }

    [Fact]
    public void Missing_self_review_is_rejected()
    {
        Single(Run(Clean(evidence: GoodEvidence() with { SelfReview = null })), "evidence.self-review");
    }

    [Fact]
    public void Missing_review_is_rejected()
    {
        Single(Run(Clean(evidence: GoodEvidence() with { Review = null })), "evidence.review");
    }

    [Fact]
    public void Review_requesting_changes_is_rejected()
    {
        var review = new ReviewResult(ReviewVerdict.RequestChanges, ["bug"]);

        Single(Run(Clean(evidence: GoodEvidence() with { Review = review })), "evidence.review");
    }

    [Fact]
    public void Source_change_without_test_change_is_rejected()
    {
        Single(Run(Clean([Source()])), "evidence.test-change");
    }

    [Fact]
    public void Source_change_with_only_a_deleted_test_does_not_count_as_test_change()
    {
        var decision = Run(Clean([Source(), new("tests/App.Tests/OldTests.cs", FileChangeKind.Deleted, 0, 5)]));

        Assert.Contains("evidence.test-change", Rules(decision));
        Assert.Contains("tests.deleted", Rules(decision));
    }

    [Fact]
    public void Test_only_change_passes()
    {
        Assert.Equal(GateOutcome.Passed, Run(Clean([Test()])).Outcome);
    }

    [Fact]
    public void Test_change_not_required_when_disabled()
    {
        var config = Config with { Gate = Config.Gate with { RequireTestChange = false } };

        Assert.Equal(GateOutcome.Passed, MergeGate.Evaluate(Clean([Source()]), config).Outcome);
    }

    // ---------- all findings ----------

    [Fact]
    public void All_findings_are_collected_at_once()
    {
        var changes = new List<FileChange>
        {
            new("src/App/App.csproj", FileChangeKind.Modified, 200, 0),
            new(".autopilot/autopilot.json", FileChangeKind.Modified, 1, 1),
            new("tests/App.Tests/ATests.cs", FileChangeKind.Deleted, 0, 400),
        };
        changes.AddRange(Enumerable.Range(1, 5).Select(i => new FileChange($"lib/F{i}.cs", FileChangeKind.Added, 1, 0)));
        var tests = new TestFacts(false, false, 10, 8, 1, "bad");
        var evidence = new Evidence("", null, null, null);

        var decision = Run(new GateInput(changes, tests, evidence));

        Assert.Equal(GateOutcome.Rejected, decision.Outcome);
        Assert.True(decision.HighRisk);
        string[] expected =
        [
            "scope.files", "scope.source-lines", "scope.test-lines",
            "paths.allowed", "paths.protected", "paths.risk",
            "tests.build", "tests.pass", "tests.count", "tests.skips", "tests.deleted",
            "evidence.work-item", "evidence.triage", "evidence.self-review", "evidence.review", "evidence.test-change",
        ];
        Assert.Equal(expected.Order(), Rules(decision).Order());
        Assert.All(decision.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Message)));
    }

    // ---------- new rules: binary files, TRX counts, added test lines, build files ----------

    [Fact]
    public void Binary_file_is_rejected_as_high_risk()
    {
        var decision = Run(Clean([Source(), Test(), new("src/App/logo.png", FileChangeKind.Added, 0, 0, Binary: true)]));

        var finding = Single(decision, "scope.binary");
        Assert.True(finding.HighRisk);
        Assert.Contains("src/App/logo.png", finding.Message);
    }

    [Fact]
    public void Unknown_test_counts_are_rejected()
    {
        var finding = Single(Run(Clean(tests: GoodTests() with { TestCountsKnown = false })), "tests.count");

        Assert.Contains("could not count tests", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test_change_with_only_removed_lines_does_not_count()
    {
        Single(Run(Clean([Source(), Test(added: 0, removed: 4)])), "evidence.test-change");
    }

    [Fact]
    public void Test_change_with_one_added_line_counts()
    {
        Assert.Equal(GateOutcome.Passed, Run(Clean([Source(), Test(added: 1, removed: 0)])).Outcome);
    }

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("src/App/Custom.targets")]
    [InlineData("build/deep/Shared.props")]
    public void Props_and_targets_files_are_risk_paths_by_default(string path)
    {
        var config = Config with { WritablePaths = ["**"] };
        var decision = Run(Clean([Source(), Test(), new(path, FileChangeKind.Modified, 1, 0)]), config);

        Assert.True(Single(decision, "paths.risk").HighRisk);
    }
}
