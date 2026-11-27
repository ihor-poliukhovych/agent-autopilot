using System.Text.RegularExpressions;
using Autopilot.Core.Paths;

namespace Autopilot.Core.Pool;

/// <summary>Where the write paths of a task come from, most reliable first.</summary>
public enum PathSource
{
    /// <summary>A <c>Paths:</c> line a person wrote in the task text (or a test).</summary>
    Declared,

    /// <summary>The expected write paths triage gave for the task.</summary>
    Triage,

    /// <summary>Nothing narrower is known, so the project's writable paths stand in: the task may touch anything there.</summary>
    Project,
}

/// <summary>
/// What the pool needs to know about a task to schedule it: where it writes and what it waits for.
/// <paramref name="Topic"/> is what triage says the task's question is about (see <see cref="Config.PoolCategories"/>), null when unknown.
/// </summary>
public sealed record PoolTaskInfo(
    string Title,
    string Body,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> DependsOn,
    string? Topic = null,
    PathSource PathSource = PathSource.Declared);

public sealed record IntegrationResult(bool Merged, string Message);

/// <summary>What the stored triage of a task says.</summary>
public enum PoolTriageState
{
    /// <summary>There is no triage for the current text of the task, or the triage agent gave no result.</summary>
    Missing,

    AgentReady,

    NeedsHuman,
}

/// <summary>The triage verdict of a task: <c>Detail</c> is the question for a person when needs-human, the error when missing.</summary>
public sealed record PoolTriage(PoolTriageState State, string Detail)
{
    public string Label => State switch
    {
        PoolTriageState.AgentReady => "agent-ready",
        PoolTriageState.NeedsHuman => "needs-human",
        _ => "no triage",
    };
}

/// <summary>
/// The pool's view of the tasks and of the repository. The runner calls it for the things that are not
/// scheduling: reading and writing task text, triage, bug tasks, and merging into the integration branch.
/// Tests give it a fake; the command line gives it the real one.
/// </summary>
public interface IPoolTasks
{
    Task<PoolTaskInfo?> DescribeAsync(string workItemId, CancellationToken ct);

    /// <summary>Reads the stored triage of the task without running an agent. Missing when there is none for the current text.</summary>
    Task<PoolTriage> TriageStateAsync(string workItemId, CancellationToken ct);

    /// <summary>Appends text (an answer, notes for a retry) to the task so the next triage and implement see it.</summary>
    Task AnswerAsync(string workItemId, string text, CancellationToken ct);

    /// <summary>Triages the task (one agent run) and reads the stored result back.</summary>
    Task<PoolTriage> RetriageAsync(string workItemId, CancellationToken ct);

    /// <summary>Makes room for another attempt (the kept branch of the failed attempt is renamed).</summary>
    Task PrepareRetryAsync(string workItemId, CancellationToken ct);

    /// <summary>
    /// Called once the pool holds the task's lock, so no live run owns the task: removes or sets aside what an
    /// earlier run that died left behind (its branch and worktree). Returns a sentence about what was done, or null.
    /// </summary>
    Task<string?> ReleaseLeftoversAsync(string workItemId, CancellationToken ct) => Task.FromResult<string?>(null);

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
    public static bool Overlaps(IReadOnlyList<string> first, IReadOnlyList<string> second) => Find(first, second) is not null;

    /// <summary>The first pair of patterns (one of each set) that can match the same file, or null when the sets are disjoint.</summary>
    public static (string First, string Second)? Find(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        foreach (var a in first)
        {
            var left = Prefix(a);
            foreach (var b in second)
            {
                var right = Prefix(b);
                if (!StartsWith(left, right) && !StartsWith(right, left))
                {
                    continue;
                }

                // A pattern with no wildcard names one file: it overlaps only when the other pattern matches it.
                if (!HasWildcard(a) && HasWildcard(b) && !GlobMatcher.IsMatch(Clean(a), [Clean(b)]))
                {
                    continue;
                }

                if (!HasWildcard(b) && HasWildcard(a) && !GlobMatcher.IsMatch(Clean(b), [Clean(a)]))
                {
                    continue;
                }

                return (a, b);
            }
        }

        return null;
    }

    /// <summary>A sentence part that says why two tasks collide, for example "both change src/a/**".</summary>
    public static string Describe((string First, string Second) pair) =>
        string.Equals(Clean(pair.First), Clean(pair.Second), StringComparison.OrdinalIgnoreCase)
            ? $"both change {Clean(pair.First)}"
            : $"{Clean(pair.First)} and {Clean(pair.Second)} cover the same files";

    private static string Clean(string pattern) => pattern.Trim().Replace('\\', '/').TrimStart('/');

    private static bool HasWildcard(string pattern) => pattern.IndexOfAny(['*', '?', '[', '{']) >= 0;

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
