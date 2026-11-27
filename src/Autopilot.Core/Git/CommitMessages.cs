using System.Text;
using Autopilot.Core.Config;

namespace Autopilot.Core.Git;

/// <summary>
/// Words the commits the agent makes and the title of its draft pull request. The default is a
/// Conventional Commits subject (<c>type(scope): subject</c>) so a project's commit-msg hook accepts it;
/// the work item id goes in a <c>Refs:</c> footer, never in the first line. No tool trailer is ever added.
/// </summary>
public static class CommitMessages
{
    /// <summary>The longest first line the project's hook accepts.</summary>
    public const int MaxFirstLine = 72;

    private const string ScopeLabelPrefix = "scope:";
    private const string ReviewSubject = "address review findings";

    /// <summary>The whole commit message of round 1 (the change) or a later round (review fixes).</summary>
    public static string Build(string? style, WorkItem item, int round)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsPlain(style))
        {
            return round == 1 ? $"{item.Id}: {item.Title}" : $"{item.Id}: {ReviewSubject}";
        }

        var first = round == 1 ? Subject(item) : Header(item, ReviewSubject);
        return first + "\n\nRefs: #" + item.Id;
    }

    /// <summary>The first line of the conventional commit, also used as the pull request title.</summary>
    public static string Subject(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Header(item, item.Title);
    }

    /// <summary>The pull request title, or null for the plain style (the publisher keeps its own wording).</summary>
    public static string? PullRequestTitle(string? style, WorkItem item) => IsPlain(style) ? null : Subject(item);

    /// <summary>The commit type for the labels: fix, docs, test, refactor, perf, ci, build, chore, else feat.</summary>
    public static string TypeFor(IEnumerable<string>? labels)
    {
        var set = (labels ?? []).Select(l => l.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (set.Overlaps(["bug", "fix", "defect", "regression"])) { return "fix"; }
        if (set.Overlaps(["docs", "documentation"])) { return "docs"; }
        if (set.Overlaps(["test", "tests"])) { return "test"; }
        if (set.Contains("refactor")) { return "refactor"; }
        if (set.Overlaps(["perf", "performance"])) { return "perf"; }
        if (set.Contains("ci")) { return "ci"; }
        if (set.Contains("build")) { return "build"; }
        if (set.Overlaps(["chore", "maintenance"])) { return "chore"; }
        return "feat";
    }

    /// <summary>The scope from a <c>scope:xyz</c> label, cleaned to lower case letters, digits and dashes; null when there is none.</summary>
    public static string? ScopeFor(IEnumerable<string>? labels)
    {
        foreach (var label in labels ?? [])
        {
            var trimmed = label.Trim();
            if (!trimmed.StartsWith(ScopeLabelPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var scope = new StringBuilder();
            foreach (var c in trimmed[ScopeLabelPrefix.Length..].ToLowerInvariant())
            {
                if (IsAsciiAlnum(c))
                {
                    scope.Append(c);
                }
                else if (c is '-' or '_' or ' ' or '/' or '.')
                {
                    scope.Append('-');
                }
            }

            var clean = scope.ToString().Trim('-');
            if (clean.Length is > 0 and <= 30)
            {
                return clean;
            }
        }

        return null;
    }

    private static string Header(WorkItem item, string title)
    {
        var scope = ScopeFor(item.Labels);
        var prefix = TypeFor(item.Labels) + (scope is null ? string.Empty : $"({scope})") + ": ";
        return prefix + Fit(title, MaxFirstLine - prefix.Length);
    }

    /// <summary>One line, first letter lower case, no leading symbol or final full stop, cut at a word to fit <paramref name="max"/>.</summary>
    private static string Fit(string? title, int max)
    {
        var line = string.Join(' ', (title ?? string.Empty).Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var start = 0;
        while (start < line.Length && !IsAsciiAlnum(char.ToLowerInvariant(line[start])))
        {
            start++;
        }

        line = start < line.Length ? char.ToLowerInvariant(line[start]) + line[(start + 1)..] : "update";
        if (line.Length > max)
        {
            var cut = line.LastIndexOf(' ', max);
            line = cut >= max / 2 ? line[..cut] : line[..max];
        }

        line = line.TrimEnd(' ', '.', ',', ';', ':', '-', '(', '[');
        return line.Length == 0 ? "update" : line;
    }

    private static bool IsAsciiAlnum(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsPlain(string? style) => string.Equals(style, CommitStyles.Plain, StringComparison.Ordinal);
}
