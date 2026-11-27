using System.Globalization;
using System.Text;
using System.Text.Json;
using Autopilot.Core.Config;
using Autopilot.Core.Gate;
using Autopilot.Core.Paths;

namespace Autopilot.Core.Local;

/// <summary>One changed file as the gate saw it.</summary>
public sealed record GateReportFile(string Path, FileChangeKind Kind, int Added, int Removed, bool Binary, bool Test, string? OldPath = null);

/// <summary>What the gate knew about the agent's evidence, without the full texts.</summary>
public sealed record GateReportEvidence(
    string WorkItemId,
    string? Triage,
    bool SelfReviewPresent,
    string? ReviewVerdict,
    int ReviewFindings,
    int TestLinesAdded);

/// <summary>
/// The gate's decision together with the facts it decided on. Written as <c>gate.json</c> next to
/// <c>PULL_REQUEST.md</c> or <c>REJECTED.md</c>, so a person can check the decision without re-running it.
/// </summary>
public sealed record GateReport(
    GateOutcome Outcome,
    bool HighRisk,
    IReadOnlyList<GateFinding> Findings,
    IReadOnlyList<GateReportFile> Files,
    int FilesChanged,
    int SourceLinesChanged,
    int TestLinesChanged,
    bool BuildSucceeded,
    bool TestsSucceeded,
    int TestCountBefore,
    int TestCountAfter,
    int SkipsAdded,
    bool TestCountsKnown,
    GateReportEvidence Evidence)
{
    public const string FileName = "gate.json";

    public static GateReport Create(GateInput input, GateDecision decision, AutopilotConfig config)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(config);

        var testPaths = config.Gate?.TestPaths ?? [];
        var files = (input.Changes ?? [])
            .Select(c => new GateReportFile(
                GlobMatcher.Normalize(c.Path),
                c.Kind,
                c.AddedLines,
                c.RemovedLines,
                c.Binary,
                GlobMatcher.IsMatch(c.Path, testPaths),
                c.OldPath is null ? null : GlobMatcher.Normalize(c.OldPath)))
            .ToList();
        var tests = input.Tests;
        var evidence = input.Evidence;
        return new GateReport(
            decision.Outcome,
            decision.HighRisk,
            decision.Findings,
            files,
            files.Count,
            files.Where(f => !f.Test).Sum(f => f.Added + f.Removed),
            files.Where(f => f.Test).Sum(f => f.Added + f.Removed),
            tests?.BuildSucceeded ?? false,
            tests?.TestsSucceeded ?? false,
            tests?.TestCountBefore ?? 0,
            tests?.TestCountAfter ?? 0,
            tests?.SkipsAdded ?? 0,
            tests?.TestCountsKnown ?? false,
            new GateReportEvidence(
                evidence?.WorkItemId ?? string.Empty,
                evidence?.Triage is null ? null : TriageStore.Label(evidence.Triage.Label),
                evidence?.SelfReview is not null,
                evidence?.Review is null ? null : evidence.Review.Verdict == ReviewVerdict.Approve ? "approve" : "request-changes",
                evidence?.Review?.Findings.Count ?? 0,
                files.Where(f => f.Test && f.Kind != FileChangeKind.Deleted).Sum(f => f.Added)));
    }

    public string ToJson() => JsonSerializer.Serialize(this, ConfigLoader.JsonOptions);

    public async Task WriteAsync(string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(System.IO.Path.Combine(folder, FileName), ToJson(), ct).ConfigureAwait(false);
    }

    /// <summary>The facts as a short markdown table, for PULL_REQUEST.md and REJECTED.md.</summary>
    public string FactsTable()
    {
        var text = new StringBuilder();
        text.AppendLine("| Fact | Value |");
        text.AppendLine("|---|---|");
        text.AppendLine($"| Files changed | {FilesChanged}{BinaryNote()} |");
        text.AppendLine($"| Lines changed outside tests | {N(SourceLinesChanged)} |");
        text.AppendLine($"| Lines changed in tests | {N(TestLinesChanged)} |");
        text.AppendLine($"| Build on a clean checkout | {(BuildSucceeded ? "succeeded" : "failed")} |");
        text.AppendLine($"| Tests on a clean checkout | {(TestsSucceeded ? "passed" : "failed")} |");
        text.AppendLine(TestCountsKnown
            ? $"| Test count (base -> branch) | {N(TestCountBefore)} -> {N(TestCountAfter)} |"
            : "| Test count (base -> branch) | could not be counted |");
        text.AppendLine($"| Tests newly skipped | {SkipsAdded} |");
        text.AppendLine($"| Triage | {Evidence.Triage ?? "none"} |");
        text.AppendLine($"| Self-review | {(Evidence.SelfReviewPresent ? "present" : "missing")} |");
        text.AppendLine($"| Reviewer | {Evidence.ReviewVerdict ?? "no review"} |");
        text.AppendLine();
        if (Files.Count > 0)
        {
            text.AppendLine("| File | Change | Added | Removed |");
            text.AppendLine("|---|---|---|---|");
            foreach (var file in Files)
            {
                var kind = file.Kind.ToString().ToLowerInvariant() + (file.Test ? ", test" : string.Empty);
                var path = file.OldPath is null ? file.Path : $"{file.OldPath} -> {file.Path}";
                var added = file.Binary ? "binary" : N(file.Added);
                var removed = file.Binary ? "binary" : N(file.Removed);
                text.AppendLine($"| `{path}` | {kind} | {added} | {removed} |");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private string BinaryNote()
    {
        var binary = Files.Count(f => f.Binary);
        return binary == 0 ? string.Empty : $" ({binary} binary)";
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
