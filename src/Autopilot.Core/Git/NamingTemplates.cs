using System.Text;
using Autopilot.Core.Config;

namespace Autopilot.Core.Git;

/// <summary>
/// Renders the branch name, commit message and pull request title from a project's templates. With no template
/// set, the results equal what <c>branchPrefix</c>, <c>commitStyle</c> and <see cref="CommitMessages"/> produce today.
/// Placeholders: {id}, {slug}, {subject}, {type}, {title}.
/// </summary>
public static class NamingTemplates
{
    public const string DefaultPrTitle = "[autopilot] {title}";
    public const int MaxSlugLength = 40;

    private const string ReviewSubject = "address review findings";

    public static readonly IReadOnlyList<string> Placeholders = ["id", "slug", "subject", "type", "title"];

    /// <summary>The branch template in effect: <c>branchTemplate</c>, else <c>branchPrefix</c> + "{id}".</summary>
    public static string EffectiveBranchTemplate(AutopilotConfig config) =>
        string.IsNullOrWhiteSpace(config.BranchTemplate) ? config.BranchPrefix + "{id}" : config.BranchTemplate;

    /// <summary>The pull request title template in effect: <c>prTitleTemplate</c>, else "[autopilot] {title}".</summary>
    public static string EffectivePrTitleTemplate(AutopilotConfig config) =>
        string.IsNullOrWhiteSpace(config.PrTitleTemplate) ? DefaultPrTitle : config.PrTitleTemplate;

    public static string Branch(AutopilotConfig config, WorkItem item) =>
        Render(EffectiveBranchTemplate(config), item, null);

    /// <summary>The commit message of round 1 (the change) or a later round (review fixes).</summary>
    public static string Commit(AutopilotConfig config, WorkItem item, int round)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(config.CommitTemplate))
        {
            return CommitMessages.Build(config.CommitStyle, item, round);
        }

        return Render(config.CommitTemplate, item, round == 1 ? OneLine(item.Title) : ReviewSubject);
    }

    public static string PullRequestTitle(AutopilotConfig config, WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(item);
        return Render(EffectivePrTitleTemplate(config), item, null);
    }

    /// <summary>Replaces the placeholders. {subject} falls back to the title when none is given.</summary>
    public static string Render(string template, WorkItem item, string? subject)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(item);
        var title = OneLine(item.Title);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = item.Id,
            ["slug"] = Slug(item.Title),
            ["subject"] = subject ?? title,
            ["type"] = CommitMessages.TypeFor(item.Labels),
            ["title"] = title.Length == 0 ? "change" : title,
        };

        var sb = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '{')
            {
                var end = template.IndexOf('}', i);
                if (end > 0 && values.TryGetValue(template[(i + 1)..end], out var value))
                {
                    sb.Append(value);
                    i = end;
                    continue;
                }
            }

            sb.Append(template[i]);
        }

        return sb.ToString();
    }

    /// <summary>Lower case letters and digits joined by single dashes, cut at <see cref="MaxSlugLength"/>.</summary>
    public static string Slug(string? title)
    {
        var sb = new StringBuilder();
        foreach (var c in (title ?? string.Empty).ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].Trim('-');
        }

        return slug.Length == 0 ? "change" : slug;
    }

    /// <summary>Problems with one template, each naming <paramref name="setting"/>. Empty when valid.</summary>
    public static IReadOnlyList<string> Validate(string setting, string? template, bool requireId)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(template))
        {
            return problems;
        }

        var depth = 0;
        var balanced = true;
        var name = new StringBuilder();
        var unknown = new List<string>();
        foreach (var c in template)
        {
            if (c == '{')
            {
                if (depth > 0) { balanced = false; }
                depth = 1;
                name.Clear();
            }
            else if (c == '}')
            {
                if (depth == 0)
                {
                    balanced = false;
                }
                else
                {
                    var n = name.ToString();
                    if (!Placeholders.Contains(n, StringComparer.Ordinal)) { unknown.Add(n); }
                    depth = 0;
                }
            }
            else if (depth > 0)
            {
                name.Append(c);
            }
        }

        if (depth > 0) { balanced = false; }

        var list = string.Join(", ", Placeholders.Select(p => "{" + p + "}"));
        if (!balanced)
        {
            problems.Add($"{setting} \"{template}\" has an unbalanced brace. Write each placeholder as {{name}}; known: {list}.");
        }

        foreach (var n in unknown.Distinct(StringComparer.Ordinal))
        {
            problems.Add($"{setting} \"{template}\" uses unknown placeholder {{{n}}}. Known: {list}.");
        }

        if (requireId && !template.Contains("{id}", StringComparison.Ordinal))
        {
            problems.Add($"{setting} \"{template}\" has no {{id}}. Without it two tasks would get the same branch name.");
        }

        return problems;
    }

    private static string OneLine(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}
