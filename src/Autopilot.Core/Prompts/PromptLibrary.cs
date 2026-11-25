using System.Reflection;
using System.Text;
using Autopilot.Core.Config;

namespace Autopilot.Core.Prompts;

/// <summary>System prompts and output schemas (embedded resources) and the per-run user prompts.</summary>
public static class PromptLibrary
{
    /// <summary>Diffs longer than this are cut in the reviewer prompt; the reviewer can run git diff itself.</summary>
    public const int MaxDiffChars = 80_000;

    public static string SystemPrompt(AgentRole role) => ReadResource(ResourceBase(role) + ".md");

    public static string OutputSchema(AgentRole role) => ReadResource(ResourceBase(role) + ".schema.json");

    public static string TriagePrompt(WorkItem item)
    {
        var text = new StringBuilder();
        text.AppendLine("Triage this work item.");
        text.AppendLine();
        AppendWorkItem(text, item, criteria: null);
        return text.ToString();
    }

    public static string ImplementerPrompt(
        WorkItem item,
        TriageResult triage,
        IReadOnlyList<string> writablePaths,
        CommandsConfig commands,
        IReadOnlyList<string>? reviewFindings = null)
    {
        var text = new StringBuilder();
        if (reviewFindings is { Count: > 0 })
        {
            text.AppendLine("A reviewer asked for changes to your earlier work on this item. Your earlier changes are already committed in this worktree. Fix every finding below, then answer with a new self-review of the whole change.");
            text.AppendLine();
            text.AppendLine("## Reviewer findings");
            text.AppendLine();
            foreach (var finding in reviewFindings)
            {
                text.AppendLine($"- {finding}");
            }

            text.AppendLine();
        }
        else
        {
            text.AppendLine("Implement this work item.");
            text.AppendLine();
        }

        AppendWorkItem(text, item, triage.AcceptanceCriteria);
        text.AppendLine("## Writable paths");
        text.AppendLine();
        text.AppendLine("You may create or change files only where one of these glob patterns matches (relative to the worktree root). Never under `.autopilot/`.");
        text.AppendLine();
        foreach (var path in writablePaths)
        {
            text.AppendLine($"- `{path}`");
        }

        text.AppendLine();
        text.AppendLine("## Commands");
        text.AppendLine();
        text.AppendLine($"- Build: `{commands.Build}`");
        text.AppendLine($"- Test: `{commands.Test}`");
        text.AppendLine();
        text.AppendLine("Both must pass before you finish.");
        return text.ToString();
    }

    public static string ReviewerPrompt(WorkItem item, TriageResult triage, string diff)
    {
        var text = new StringBuilder();
        text.AppendLine("Review this change.");
        text.AppendLine();
        AppendWorkItem(text, item, triage.AcceptanceCriteria);
        text.AppendLine("## Diff");
        text.AppendLine();
        var shown = diff.Length > MaxDiffChars
            ? diff[..MaxDiffChars] + "\n... (diff cut here; run `git diff HEAD~1` to see the rest)\n"
            : diff;
        text.AppendLine("```diff");
        text.AppendLine(shown.TrimEnd());
        text.AppendLine("```");
        return text.ToString();
    }

    private static void AppendWorkItem(StringBuilder text, WorkItem item, IReadOnlyList<string>? criteria)
    {
        text.AppendLine($"## Work item {item.Id}: {item.Title}");
        text.AppendLine();
        text.AppendLine(string.IsNullOrWhiteSpace(item.Body) ? "(no description)" : item.Body.Trim());
        text.AppendLine();
        if (item.Labels.Count > 0)
        {
            text.AppendLine($"Labels: {string.Join(", ", item.Labels)}");
            text.AppendLine();
        }

        if (criteria is not null)
        {
            text.AppendLine("## Acceptance criteria");
            text.AppendLine();
            if (criteria.Count == 0)
            {
                text.AppendLine("(none given)");
            }

            foreach (var criterion in criteria)
            {
                text.AppendLine($"- {criterion}");
            }

            text.AppendLine();
        }
    }

    private static string ResourceBase(AgentRole role) => role switch
    {
        AgentRole.Triage => "triage",
        AgentRole.Implementer => "implementer",
        AgentRole.Reviewer => "reviewer",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    private static string ReadResource(string fileName)
    {
        var name = "Autopilot.Core.Prompts." + fileName;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
