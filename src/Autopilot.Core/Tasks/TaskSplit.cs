using System.Text;
using Autopilot.Core.Pool;
using Autopilot.Core.Workspace;

namespace Autopilot.Core.Tasks;

/// <summary>One child task proposed when a task is split.</summary>
public sealed record SplitProposal(string Title, string Body, IReadOnlyList<string> Labels, IReadOnlyList<string> Paths);

public sealed record SplitResult(bool Success, string? Error, IReadOnlyList<LocalTask> Children)
{
    public static SplitResult Fail(string error) => new(false, error, []);
}

/// <summary>
/// Splits a task: creates one child task per proposal, linked to the original, and queues the children in the
/// pool in proposal order. The original stays open as a parent group and is not queued; it closes by itself
/// when its last child closes (see <see cref="LocalTaskService.Close"/>).
/// </summary>
public sealed class TaskSplit(LocalTaskService tasks, PoolService pool)
{
    public SplitResult Split(int originalNumber, IReadOnlyList<SplitProposal>? proposals, string? author = null)
    {
        if (proposals is null || proposals.Count == 0)
        {
            return SplitResult.Fail("The split proposal has no child tasks.");
        }

        if (proposals.Any(p => p is null || string.IsNullOrWhiteSpace(p.Title)))
        {
            return SplitResult.Fail("Every child task in a split needs a title.");
        }

        if (proposals.Any(p => p.Title.Trim().Length > LocalTaskService.MaxTitleLength))
        {
            return SplitResult.Fail($"A title can have at most {LocalTaskService.MaxTitleLength} characters.");
        }

        var original = tasks.Get(originalNumber);
        if (original is null)
        {
            return SplitResult.Fail($"There is no task {originalNumber}.");
        }

        if (original.Closed)
        {
            return SplitResult.Fail($"Task {originalNumber} is closed and cannot be split.");
        }

        var children = new List<LocalTask>();
        foreach (var proposal in proposals)
        {
            var child = tasks.AddChild(originalNumber, proposal.Title, BodyWithPaths(proposal), proposal.Labels, author);
            children.Add(child);
            pool.Add(child.Id);
        }

        tasks.AppendHistory(originalNumber, LocalTaskService.HistoryKind.Split,
            $"Split into {children.Count} child tasks: " + string.Join(", ", children.Select(c => $"#{c.Number}")) + ".",
            string.Join('\n', children.Select(c => $"#{c.Number} {c.Title}")));
        return new SplitResult(true, null, children);
    }

    /// <summary>The child body followed by a <c>Paths:</c> line, the format <see cref="PoolTaskHints.PathsFrom"/> reads.</summary>
    private static string BodyWithPaths(SplitProposal proposal)
    {
        var text = new StringBuilder((proposal.Body ?? string.Empty).TrimEnd());
        var paths = (proposal.Paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        if (paths.Count > 0)
        {
            if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            text.Append("Paths: ").Append(string.Join(", ", paths));
        }

        return text.ToString();
    }
}
