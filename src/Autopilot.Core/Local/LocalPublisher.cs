using System.Text;
using System.Text.Json;
using Autopilot.Core.Config;
using Autopilot.Core.Gate;

namespace Autopilot.Core.Local;

/// <summary>
/// Publishes a passed change as a local bundle in <c>.autopilot/out/runs/&lt;id&gt;/</c>: the patch,
/// the gate result, the evidence and a pull request description. Nothing leaves the machine.
/// </summary>
public sealed class LocalPublisher(string projectRoot) : IPublisher
{
    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        try
        {
            var folder = ProjectPaths.RunFolder(projectRoot, request.WorkItem.Id);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "change.patch"), request.Diff, ct).ConfigureAwait(false);
            var gateJson = request.Report?.ToJson() ?? JsonSerializer.Serialize(request.Decision, ConfigLoader.JsonOptions);
            await File.WriteAllTextAsync(Path.Combine(folder, GateReport.FileName), gateJson, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(folder, "evidence.json"), JsonSerializer.Serialize(request.Evidence, ConfigLoader.JsonOptions), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(folder, "PULL_REQUEST.md"), PullRequestText(request), ct).ConfigureAwait(false);
            return new PublishResult(true, folder, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PublishResult(false, string.Empty, ex.Message);
        }
    }

    public static string PullRequestText(PublishRequest request)
    {
        var item = request.WorkItem;
        var evidence = request.Evidence;
        var text = new StringBuilder();
        text.AppendLine($"# {item.Title}");
        text.AppendLine();
        text.AppendLine("**Status: waiting for a person.** This is a draft. Agent Autopilot never merges; review the change and merge it yourself if it is right.");
        text.AppendLine();
        text.AppendLine($"- Work item: {item.Id}");
        text.AppendLine($"- Branch: `{request.BranchName}`");
        text.AppendLine($"- Patch: `change.patch` in this folder");
        text.AppendLine();

        text.AppendLine("## Work item");
        text.AppendLine();
        text.AppendLine(string.IsNullOrWhiteSpace(item.Body) ? "(no description)" : item.Body);
        text.AppendLine();
        if (evidence.Triage is { AcceptanceCriteria.Count: > 0 } triage)
        {
            text.AppendLine("Acceptance criteria:");
            foreach (var criterion in triage.AcceptanceCriteria)
            {
                text.AppendLine($"- {criterion}");
            }

            text.AppendLine();
        }

        text.AppendLine("## Summary");
        text.AppendLine();
        text.AppendLine(evidence.SelfReview?.Summary ?? "(the implementer gave no summary)");
        text.AppendLine();

        text.AppendLine("## Tests that prove it");
        text.AppendLine();
        AppendList(text, evidence.SelfReview?.TestsThatProveIt);
        if (evidence.SelfReview is { Doubts.Count: > 0 } selfReview)
        {
            text.AppendLine("## What the implementer is unsure about");
            text.AppendLine();
            AppendList(text, selfReview.Doubts);
        }

        text.AppendLine("## Reviewer verdict");
        text.AppendLine();
        text.AppendLine(evidence.Review is null
            ? "(no review)"
            : evidence.Review.Verdict == ReviewVerdict.Approve ? "approve" : "request-changes");
        text.AppendLine();
        if (evidence.Review is { Findings.Count: > 0 } review)
        {
            AppendList(text, review.Findings);
        }

        text.AppendLine("## Gate result");
        text.AppendLine();
        text.AppendLine(request.Decision.Outcome == GateOutcome.Passed ? "Passed." : "Rejected.");
        foreach (var finding in request.Decision.Findings)
        {
            text.AppendLine($"- [{finding.RuleId}] {finding.Message}{(finding.HighRisk ? " (high risk)" : string.Empty)}");
        }

        text.AppendLine();
        if (request.Report is { } report)
        {
            text.AppendLine("## Gate facts");
            text.AppendLine();
            text.AppendLine("Collected by the gate itself on clean checkouts; full facts in `gate.json`.");
            text.AppendLine();
            text.Append(report.FactsTable());
        }

        text.AppendLine("## Waiting for a person");
        text.AppendLine();
        text.AppendLine($"To take the change: `git merge {request.BranchName}` on your base branch after reviewing it, or open a pull request from the branch.");
        return text.ToString();
    }

    private static void AppendList(StringBuilder text, IReadOnlyList<string>? items)
    {
        if (items is null || items.Count == 0)
        {
            text.AppendLine("(none)");
        }
        else
        {
            foreach (var entry in items)
            {
                text.AppendLine($"- {entry}");
            }
        }

        text.AppendLine();
    }
}
