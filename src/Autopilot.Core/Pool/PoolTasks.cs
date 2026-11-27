using System.Text.RegularExpressions;

namespace Autopilot.Core.Pool;

/// <summary>What the pool needs to know about a task to schedule it: where it writes and what it waits for.</summary>
public sealed record PoolTaskInfo(string Title, string Body, IReadOnlyList<string> Paths, IReadOnlyList<string> DependsOn);

public sealed record IntegrationResult(bool Merged, string Message);

/// <summary>
/// The pool's view of the tasks and of the repository. The runner calls it for the things that are not
/// scheduling: reading and writing task text, triage, bug tasks, and merging into the integration branch.
/// Tests give it a fake; the command line gives it the real one.
/// </summary>
public interface IPoolTasks
{
    Task<PoolTaskInfo?> DescribeAsync(string workItemId, CancellationToken ct);

    /// <summary>The triage's reason when the task is marked needs-human, otherwise null.</summary>
    Task<string?> QuestionAsync(string workItemId, CancellationToken ct);

    /// <summary>Appends text (an answer, notes for a retry) to the task so the next triage and implement see it.</summary>
    Task AnswerAsync(string workItemId, string text, CancellationToken ct);

    /// <summary>Triages the task again. True when it is agent-ready now.</summary>
    Task<bool> RetriageAsync(string workItemId, CancellationToken ct);

    /// <summary>Makes room for another attempt (the kept branch of the failed attempt is renamed).</summary>
    Task PrepareRetryAsync(string workItemId, CancellationToken ct);

    /// <summary>Creates a bug task and returns its id.</summary>
    Task<string> CreateBugAsync(string title, string body, CancellationToken ct);

    /// <summary>
    /// Merges a published task branch into the integration branch after build and tests pass.
    /// Returns null when the integration branch is switched off.
    /// </summary>
    Task<IntegrationResult?> IntegrateAsync(Pipeline.ImplementResult result, CancellationToken ct);
}

/// <summary>Reads the scheduling hints of a task: <c>Paths:</c> lines in its text and <c>depends-on:N</c> labels.</summary>
public static partial class PoolTaskHints
{
    /// <summary>Glob patterns from a line such as <c>Paths: src/Billing/**, tests/Billing.Tests/**</c>. Empty when there is none.</summary>
    public static IReadOnlyList<string> PathsFrom(string body)
    {
        var paths = new List<string>();
        foreach (Match match in PathsLine().Matches(body ?? string.Empty))
        {
            paths.AddRange(match.Groups[1].Value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Trim('`', ' ').Replace('\\', '/')).Where(p => p.Length > 0));
        }

        return paths;
    }

    /// <summary>Task ids from labels such as <c>depends-on:12</c> or <c>depends-on-12</c>.</summary>
    public static IReadOnlyList<string> DependenciesFrom(IEnumerable<string> labels) =>
        [.. labels.Select(l => DependsLabel().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value)];

    [GeneratedRegex(@"^\s*Paths\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex PathsLine();

    [GeneratedRegex(@"^depends-on[:\-](\S+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DependsLabel();
}

/// <summary>Decides whether two sets of write paths can touch the same file.</summary>
public static class PathOverlap
{
    /// <summary>
    /// True when a pattern of one set and a pattern of the other can match the same file. It compares the
    /// literal folder prefix of each glob: <c>src/a/**</c> and <c>src/a/b/*.cs</c> overlap, <c>src/a/**</c> and
    /// <c>src/b/**</c> do not. A pattern that starts with a wildcard overlaps everything.
    /// </summary>
    public static bool Overlaps(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        foreach (var a in first)
        {
            var left = Prefix(a);
            foreach (var b in second)
            {
                var right = Prefix(b);
                if (StartsWith(left, right) || StartsWith(right, left))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string[] Prefix(string pattern)
    {
        var segments = pattern.Trim().Replace('\\', '/').TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = new List<string>();
        foreach (var segment in segments)
        {
            if (segment.IndexOfAny(['*', '?', '[', '{']) >= 0)
            {
                break;
            }

            prefix.Add(segment);
        }

        return [.. prefix];
    }

    private static bool StartsWith(string[] longer, string[] shorter) =>
        shorter.Length <= longer.Length && shorter.Zip(longer).All(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));
}
