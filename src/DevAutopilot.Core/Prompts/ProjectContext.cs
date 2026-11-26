using System.Text;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Git;

namespace DevAutopilot.Core.Prompts;

/// <summary>One file of project rules and context, as read from the base commit.</summary>
public sealed record ProjectContextFile(string Path, string Text);

/// <summary>
/// The text of the <c>context</c> files, read from the base commit (never from the working tree, so
/// uncommitted edits do not reach the agents). Added to the triage, implementer and reviewer prompts
/// under "Project rules and context", cut at <see cref="MaxChars"/> characters in total.
/// </summary>
public sealed record ProjectContext(
    string Commit,
    IReadOnlyList<ProjectContextFile> Files,
    /// <summary>Configured paths that do not exist in the base commit.</summary>
    IReadOnlyList<string> Missing,
    /// <summary>Files that were left out or cut, with the reason, for the note at the end.</summary>
    IReadOnlyList<string> Notes,
    bool Truncated)
{
    /// <summary>Most characters of context text in one prompt (about 40 KB).</summary>
    public const int MaxChars = 40 * 1024;

    public const string Heading = "## Project rules and context";

    public static ProjectContext Empty { get; } = new(string.Empty, [], [], [], false);

    public bool IsEmpty => Files.Count == 0 && Notes.Count == 0;

    /// <summary>Total characters of file text included.</summary>
    public int TotalChars => Files.Sum(f => f.Text.Length);

    /// <summary>
    /// Reads every configured path at <paramref name="commit"/>. A folder includes every file in it, in
    /// path order. Binary files are skipped. Text past <paramref name="maxChars"/> in total is cut.
    /// </summary>
    public static async Task<ProjectContext> LoadAsync(GitClient git, string commit, IReadOnlyList<string>? paths, CancellationToken ct, int maxChars = MaxChars)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        if (paths is null || paths.Count == 0)
        {
            return Empty;
        }

        var files = new List<ProjectContextFile>();
        var missing = new List<string>();
        var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var left = maxChars;
        var truncated = false;

        foreach (var entry in paths.Where(p => ContextPaths.Problem(p) is null).Select(ContextPaths.Normalize))
        {
            var found = await git.ListFilesAtAsync(commit, entry, ct).ConfigureAwait(false);
            if (found.Count == 0)
            {
                missing.Add(entry);
                continue;
            }

            foreach (var file in found.Where(seen.Add))
            {
                if (left <= 0)
                {
                    truncated = true;
                    notes.Add($"{file}: not shown, the {maxChars}-character limit was reached");
                    continue;
                }

                var text = await git.ReadFileAtAsync(commit, file, ct).ConfigureAwait(false);
                if (text.Contains('\0', StringComparison.Ordinal))
                {
                    notes.Add($"{file}: skipped, it is a binary file");
                    continue;
                }

                text = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
                if (text.Length > left)
                {
                    truncated = true;
                    notes.Add($"{file}: cut after {left} of {text.Length} characters, the {maxChars}-character limit was reached");
                    text = text[..left];
                }

                left -= text.Length;
                files.Add(new ProjectContextFile(file, text));
            }
        }

        return new ProjectContext(commit, files, missing, notes, truncated);
    }

    /// <summary>The prompt section, or an empty string when there is no context.</summary>
    public string Render()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.AppendLine(Heading);
        text.AppendLine();
        text.AppendLine($"The project gives these rules and this context to every agent. They come from the base commit {Short(Commit)}. Follow them; where they conflict with the work item, say so. You cannot change these files.");
        text.AppendLine();
        foreach (var file in Files)
        {
            text.AppendLine($"<file path=\"{file.Path}\">");
            text.AppendLine(file.Text);
            text.AppendLine("</file>");
            text.AppendLine();
        }

        if (Notes.Count > 0)
        {
            text.AppendLine(Truncated
                ? "(The project context was cut at its size limit. Read the files themselves for the rest.)"
                : "(Some context files were left out.)");
            foreach (var note in Notes)
            {
                text.AppendLine($"- {note}");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Short(string commit) => commit.Length > 12 ? commit[..12] : commit;
}
