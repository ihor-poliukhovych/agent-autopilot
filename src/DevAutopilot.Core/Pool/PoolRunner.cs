using System.Text;
using DevAutopilot.Core.Evals;
using DevAutopilot.Core.Pipeline;
using DevAutopilot.Core.Runs;

namespace DevAutopilot.Core.Pool;

/// <summary>What happened to the pool items in one pool run, in the order they were tried.</summary>
public sealed record PoolRunResult(
    IReadOnlyList<ImplementResult> Done,
    IReadOnlyList<ImplementResult> Rejected,
    IReadOnlyList<ImplementResult> NeedsHuman,
    IReadOnlyList<string> NotReached,
    string? StoppedBecause,
    string? ReportPath)
{
    public bool Succeeded => StoppedBecause is null && Rejected.Count == 0;
}

/// <summary>
/// Runs the pool one item at a time. It stops on an infrastructure error (Claude Code signed out, a usage
/// limit, a program that cannot start): every later item would fail the same way. Items that were tried leave
/// the pool; items not reached and the item that hit the error stay. Takes the project's run lock first.
/// </summary>
public sealed class PoolRunner(
    PoolService pool,
    RunStore runs,
    ProjectRunLock runLock,
    string projectKey,
    Func<string, CancellationToken, Task<ImplementResult>> implement)
{
    public const string ReportFileName = "POOL-REPORT.md";

    /// <param name="limit">At most this many items are tried; the rest are listed as not reached.</param>
    /// <param name="reportFolder">Where <c>POOL-REPORT.md</c> is written (the project's workspace folder).</param>
    public async Task<PoolRunResult> RunAsync(int? limit, string reportFolder, CancellationToken ct)
    {
        if (limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The limit must be greater than 0.");
        }

        using var held = runLock.Acquire(projectKey, "pool run");
        var queue = pool.List().Select(e => e.WorkItemId).ToList();
        var done = new List<ImplementResult>();
        var rejected = new List<ImplementResult>();
        var needsHuman = new List<ImplementResult>();
        var notReached = new List<string>();
        string? stopped = null;

        for (var index = 0; index < queue.Count; index++)
        {
            var id = queue[index];
            if (stopped is not null)
            {
                notReached.Add($"{id}: not started. {stopped}");
                continue;
            }

            if (limit is { } max && index >= max)
            {
                notReached.Add($"{id}: not started, the limit of {max} was reached.");
                continue;
            }

            ct.ThrowIfCancellationRequested();
            var started = DateTimeOffset.UtcNow;
            ImplementResult result;
            try
            {
                result = await implement(id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new ImplementResult(id, ImplementOutcome.Failed, ex.Message);
            }

            var infrastructure = result.Outcome == ImplementOutcome.Failed ? InfrastructureErrors.Classify(result.Message) : null;
            if (infrastructure is not null)
            {
                stopped = $"Stopped at {id}: {infrastructure}";
                notReached.Add($"{id}: stopped here. {infrastructure}");
                continue;
            }

            runs.Record(result, started);
            pool.Remove(id);
            switch (result.Outcome)
            {
                case ImplementOutcome.Published:
                    done.Add(result);
                    break;
                case ImplementOutcome.NotReady or ImplementOutcome.NotFound:
                    needsHuman.Add(result);
                    break;
                default:
                    rejected.Add(result);
                    break;
            }
        }

        var outcome = new PoolRunResult(done, rejected, needsHuman, notReached, stopped, Path.Combine(reportFolder, ReportFileName));
        Directory.CreateDirectory(reportFolder);
        await File.WriteAllTextAsync(outcome.ReportPath!, Report(outcome, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);
        return outcome;
    }

    public static string Report(PoolRunResult result, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = new StringBuilder();
        text.AppendLine("# Pool report");
        text.AppendLine();
        text.AppendLine($"Finished {at:yyyy-MM-dd HH:mm} UTC.");
        if (result.StoppedBecause is not null)
        {
            text.AppendLine();
            text.AppendLine($"**{result.StoppedBecause}**");
        }

        Section(text, "Done (drafts waiting for a person)", result.Done.Select(r => $"{r.WorkItemId}: {r.Message}{Where(r)}"));
        Section(text, "Rejected", result.Rejected.Select(r => $"{r.WorkItemId}: {r.Message}{Where(r)}"));
        Section(text, "Needs a person", result.NeedsHuman.Select(r => $"{r.WorkItemId}: {r.Message}"));
        Section(text, "Not reached", result.NotReached);
        return text.ToString();
    }

    private static string Where(ImplementResult r) =>
        r.Location is not null ? $" (files: {r.Location})" : r.BranchName is not null ? $" (branch: {r.BranchName})" : string.Empty;

    private static void Section(StringBuilder text, string title, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        text.AppendLine();
        text.AppendLine($"## {title} ({list.Count})");
        text.AppendLine();
        if (list.Count == 0)
        {
            text.AppendLine("None.");
            return;
        }

        foreach (var line in list)
        {
            text.AppendLine($"- {line.ReplaceLineEndings(" ")}");
        }
    }
}
