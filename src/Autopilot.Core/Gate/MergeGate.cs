using Autopilot.Core.Config;
using Autopilot.Core.Paths;

namespace Autopilot.Core.Gate;

public static class MergeGate
{
    /// <summary>Paths no agent may change, whatever the config says.</summary>
    public static IReadOnlyList<string> ProtectedPaths { get; } = [".autopilot/**"];

    private const int MaxNamesInMessage = 10;

    public static GateDecision Evaluate(GateInput input, AutopilotConfig config)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(config);

        var gate = config.Gate;
        var changes = input.Changes ?? [];
        var findings = new List<GateFinding>();

        bool IsTest(string path) => GlobMatcher.IsMatch(path, gate.TestPaths);

        // ---------- Scope ----------

        if (changes.Count > gate.MaxFilesChanged)
        {
            findings.Add(new("scope.files",
                $"{changes.Count} files changed; the limit is {gate.MaxFilesChanged}. Split the work into smaller items."));
        }

        var sourceLines = changes.Where(c => !IsTest(c.Path)).Sum(c => c.AddedLines + c.RemovedLines);
        if (sourceLines > gate.MaxSourceLinesChanged)
        {
            findings.Add(new("scope.source-lines",
                $"{sourceLines} lines changed outside tests; the limit is {gate.MaxSourceLinesChanged}."));
        }

        var testLines = changes.Where(c => IsTest(c.Path)).Sum(c => c.AddedLines + c.RemovedLines);
        if (testLines > gate.MaxTestLinesChanged)
        {
            findings.Add(new("scope.test-lines",
                $"{testLines} lines changed in tests; the limit is {gate.MaxTestLinesChanged}."));
        }

        var binary = changes.Where(c => c.Binary).Select(c => GlobMatcher.Normalize(c.Path)).ToList();
        if (binary.Count > 0)
        {
            findings.Add(new("scope.binary",
                $"Binary files changed; the gate cannot count or read them, so a person must check: {Names(binary)}.", HighRisk: true));
        }

        // ---------- Paths ----------

        var touched = changes
            .SelectMany(c => c.OldPath is null ? new[] { c.Path } : new[] { c.Path, c.OldPath })
            .Select(GlobMatcher.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var outside = touched.Where(p => !GlobMatcher.IsMatch(p, config.WritablePaths)).ToList();
        if (outside.Count > 0)
        {
            findings.Add(new("paths.allowed",
                $"Changed outside the writable paths: {Names(outside)}."));
        }

        var protectedPatterns = ProtectedPaths.Concat(ContextPaths.ProtectedPatterns(config.Context)).ToList();
        var protectedHits = touched.Where(p => GlobMatcher.IsMatch(p, protectedPatterns)).ToList();
        if (protectedHits.Count > 0)
        {
            findings.Add(new("paths.protected",
                $"Changed protected files that no agent may touch: {Names(protectedHits)}."));
        }

        var riskHits = touched.Where(p => GlobMatcher.IsMatch(p, config.RiskPaths)).ToList();
        if (riskHits.Count > 0)
        {
            findings.Add(new("paths.risk",
                $"Changed high-risk files; a person must review: {Names(riskHits)}.", HighRisk: true));
        }

        // ---------- Tests ----------

        var tests = input.Tests;
        if (tests is null)
        {
            findings.Add(new("tests.build", "No build and test facts were collected."));
        }
        else
        {
            if (!tests.BuildSucceeded)
            {
                findings.Add(new("tests.build", "The build failed on a clean checkout of the branch. See the gate log."));
            }

            if (!tests.TestsSucceeded)
            {
                findings.Add(new("tests.pass", "Tests failed on a clean checkout of the branch. See the gate log."));
            }

            if (!tests.TestCountsKnown)
            {
                findings.Add(new("tests.count",
                    "Could not count tests: no TRX results were found for the base or the branch run. See the gate log."));
            }
            else if (tests.TestCountAfter < tests.TestCountBefore)
            {
                findings.Add(new("tests.count",
                    $"Test count dropped from {tests.TestCountBefore} to {tests.TestCountAfter}."));
            }

            if (tests.SkipsAdded > 0)
            {
                findings.Add(new("tests.skips",
                    $"{tests.SkipsAdded} test(s) newly marked as skipped. Fix the tests instead of skipping them."));
            }
        }

        var deletedTests = changes
            .Where(c => (c.Kind == FileChangeKind.Deleted && IsTest(c.Path))
                        || (c.Kind == FileChangeKind.Renamed && c.OldPath is not null && IsTest(c.OldPath) && !IsTest(c.Path)))
            .Select(c => GlobMatcher.Normalize(c.Kind == FileChangeKind.Deleted ? c.Path : c.OldPath!))
            .ToList();
        if (deletedTests.Count > 0)
        {
            findings.Add(new("tests.deleted", $"Test files were deleted: {Names(deletedTests)}."));
        }

        // ---------- Evidence ----------

        var evidence = input.Evidence;
        if (evidence is null || string.IsNullOrWhiteSpace(evidence.WorkItemId))
        {
            findings.Add(new("evidence.work-item", "The change is not linked to a work item."));
        }

        if (evidence?.Triage is null)
        {
            findings.Add(new("evidence.triage", "No triage result; the work item was never marked agent-ready."));
        }
        else if (evidence.Triage.Label != Core.TriageLabel.AgentReady)
        {
            findings.Add(new("evidence.triage",
                $"Triage marked the work item {evidence.Triage.Label}, not AgentReady."));
        }

        if (evidence?.SelfReview is null)
        {
            findings.Add(new("evidence.self-review", "The implementer gave no self-review."));
        }

        if (evidence?.Review is null)
        {
            findings.Add(new("evidence.review", "No reviewer verdict."));
        }
        else if (evidence.Review.Verdict != Core.ReviewVerdict.Approve)
        {
            findings.Add(new("evidence.review",
                $"The reviewer said {evidence.Review.Verdict}, not Approve."));
        }

        if (gate.RequireTestChange)
        {
            var sourceChanged = changes.Any(c => !IsTest(c.Path));
            // A test must gain at least one line; removing or renaming tests proves nothing.
            var testChanged = changes.Any(c => c.Kind != FileChangeKind.Deleted && IsTest(c.Path) && c.AddedLines > 0);
            if (sourceChanged && !testChanged)
            {
                findings.Add(new("evidence.test-change",
                    "Code outside tests changed but no line was added to a test file."));
            }
        }

        return new GateDecision(findings.Count == 0 ? GateOutcome.Passed : GateOutcome.Rejected, findings);
    }

    private static string Names(IReadOnlyList<string> paths)
    {
        var shown = string.Join(", ", paths.Take(MaxNamesInMessage));
        return paths.Count > MaxNamesInMessage ? $"{shown} and {paths.Count - MaxNamesInMessage} more" : shown;
    }
}
