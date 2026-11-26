using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevAutopilot.Core.Config;

namespace DevAutopilot.Core.Local;

/// <summary>
/// Keeps triage outcomes in <c>.autopilot/out/triage/&lt;id&gt;.json</c> (for the harness) and
/// <c>&lt;id&gt;.md</c> (for a person). The pipeline reads the JSON back before implementing.
/// </summary>
public sealed class TriageStore(string projectRoot, string? dataFolder = null)
{
    public string Folder { get; } = ProjectPaths.TriageFolder(projectRoot, dataFolder);

    public string JsonPath(string id) => Path.Combine(Folder, ProjectPaths.SafeId(id) + ".json");

    public string MarkdownPath(string id) => Path.Combine(Folder, ProjectPaths.SafeId(id) + ".md");

    public async Task SaveAsync(WorkItem item, TriageResult result, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var record = new TriageRecord(item.Id, item.Title, DateTimeOffset.UtcNow, result, TextHash(item));
        await File.WriteAllTextAsync(JsonPath(item.Id), JsonSerializer.Serialize(record, ConfigLoader.JsonOptions), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(MarkdownPath(item.Id), ToMarkdown(item.Id, item.Title, result), ct).ConfigureAwait(false);
    }

    public async Task<TriageResult?> LoadAsync(string id, CancellationToken ct) =>
        (await LoadRecordAsync(id, ct).ConfigureAwait(false))?.Result;

    /// <summary>The stored record, with the hash of the text that was triaged, or null.</summary>
    public async Task<TriageRecord?> LoadRecordAsync(string id, CancellationToken ct)
    {
        var path = JsonPath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TriageRecord>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), ConfigLoader.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>SHA-256 (lower-case hex) of the title, a newline and the body: the text triage decided on.</summary>
    public static string TextHash(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var text = (item.Title ?? string.Empty) + "\n" + (item.Body ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static string ToMarkdown(string id, string title, TriageResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"# Triage: {id} {title}");
        text.AppendLine();
        text.AppendLine($"- Label: **{Label(result.Label)}**");
        text.AppendLine($"- Risk: {result.Risk.ToString().ToLowerInvariant()}");
        text.AppendLine();
        text.AppendLine("## Reason");
        text.AppendLine();
        text.AppendLine(result.Reason);
        text.AppendLine();
        text.AppendLine("## Acceptance criteria");
        text.AppendLine();
        if (result.AcceptanceCriteria.Count == 0)
        {
            text.AppendLine("(none)");
        }

        foreach (var criterion in result.AcceptanceCriteria)
        {
            text.AppendLine($"- {criterion}");
        }

        if (result.Label == TriageLabel.NeedsHuman)
        {
            text.AppendLine();
            text.AppendLine("A person needs to decide this item. Edit the task file and run triage again.");
        }

        return text.ToString();
    }

    public static string Label(TriageLabel label) => label == TriageLabel.AgentReady ? "agent-ready" : "needs-human";

    /// <summary>What <c>&lt;id&gt;.json</c> holds. TextHash is null in records written before it existed.</summary>
    public sealed record TriageRecord(string Id, string Title, DateTimeOffset TriagedAt, TriageResult Result, string? TextHash = null);
}
