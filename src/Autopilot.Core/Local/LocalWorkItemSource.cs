namespace Autopilot.Core.Local;

/// <summary>
/// Work items from <c>.autopilot/tasks/*.md</c>. The id is the file name up to the first '-'
/// (<c>001-fix-404.md</c> is "001"), the title is the first <c># </c> heading, and optional
/// <c>Labels: a, b</c> and <c>Status: done</c> lines are read and kept out of the body.
/// </summary>
public sealed class LocalWorkItemSource(string projectRoot) : IWorkItemSource
{
    private static readonly string[] ClosedStatuses = ["done", "closed"];

    private readonly TriageStore _triage = new(projectRoot);

    public string TasksFolder { get; } = ProjectPaths.TasksFolder(projectRoot);

    public async Task<IReadOnlyList<WorkItem>> ListOpenAsync(CancellationToken ct)
    {
        var all = await ReadAllAsync(ct).ConfigureAwait(false);
        return all.Where(t => !t.Closed).Select(t => t.Item).ToList();
    }

    public async Task<WorkItem?> GetAsync(string id, CancellationToken ct)
    {
        var all = await ReadAllAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(t => string.Equals(t.Item.Id, id, StringComparison.OrdinalIgnoreCase)).Item;
    }

    public async Task RecordTriageAsync(string id, TriageResult result, CancellationToken ct)
    {
        var item = await GetAsync(id, ct).ConfigureAwait(false) ?? new WorkItem(id, id, string.Empty, []);
        await _triage.SaveAsync(item, result, ct).ConfigureAwait(false);
    }

    public static string IdFromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? name[..dash] : name;
    }

    /// <summary>Parses one task file. Returns the item and whether its status marks it closed.</summary>
    public static (WorkItem Item, bool Closed) Parse(string fileName, string text)
    {
        var id = IdFromFileName(fileName);
        string? title = null;
        var labels = new List<string>();
        var closed = false;
        var body = new List<string>();

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (title is null && line.StartsWith("# ", StringComparison.Ordinal))
            {
                title = line[2..].Trim();
                continue;
            }

            if (TryReadField(line, "Labels", out var labelText))
            {
                labels.AddRange(labelText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                continue;
            }

            if (TryReadField(line, "Status", out var status))
            {
                closed = ClosedStatuses.Contains(status.Trim(), StringComparer.OrdinalIgnoreCase);
                continue;
            }

            body.Add(line);
        }

        var item = new WorkItem(
            id,
            string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fileName) : title,
            string.Join('\n', body).Trim(),
            labels);
        return (item, closed);
    }

    private static bool TryReadField(string line, string field, out string value)
    {
        if (line.StartsWith(field + ":", StringComparison.OrdinalIgnoreCase))
        {
            value = line[(field.Length + 1)..].Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private async Task<List<(WorkItem Item, bool Closed)>> ReadAllAsync(CancellationToken ct)
    {
        var result = new List<(WorkItem Item, bool Closed)>();
        if (!Directory.Exists(TasksFolder))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(TasksFolder, "*.md").Order(StringComparer.OrdinalIgnoreCase))
        {
            var parsed = Parse(Path.GetFileName(file), await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            if (seen.Add(parsed.Item.Id))
            {
                result.Add(parsed);
            }
        }

        return result;
    }
}
