using System.Text.Json;
using System.Text.Json.Nodes;
using DevAutopilot.Core.Config;

namespace DevAutopilot.Core.Settings;

/// <summary>The settings file of a project as it is now: the effective values and what is wrong with them.</summary>
public sealed record ProjectSettingsView(string Path, AutopilotConfig Config, JsonObject Raw, IReadOnlyList<string> Problems);

/// <summary>The change was refused. <see cref="Problems"/> lists every reason in plain English; nothing was written.</summary>
public sealed class SettingsInvalidException(IReadOnlyList<string> problems)
    : Exception("The settings were not saved:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Select(p => "- " + p)))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Reads, checks and saves a project's <c>autopilot.json</c>, and reads and writes the project's context
/// files for a person (the web UI calls this; the CLI is a thin layer). Saving merges a change onto the
/// file, so keys this version does not know are kept.
/// </summary>
public sealed class ProjectSettingsService(string repositoryRoot, string? dataFolder = null)
{
    public const int MaxContextFileBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Path { get; } = ConfigLoader.ConfigPath(repositoryRoot, dataFolder);

    public ProjectSettingsView Read()
    {
        if (!File.Exists(Path))
        {
            throw new ConfigException($"Config file not found: {Path}. Run 'autopilot init' or 'autopilot project add' to create it.");
        }

        var text = File.ReadAllText(Path);
        var raw = ParseObject(text, Path);
        var config = ConfigLoader.Parse(text, Path);
        return new ProjectSettingsView(Path, config, raw, ConfigLoader.Validate(config));
    }

    /// <summary>The problems the file would have after <paramref name="change"/> is merged onto it. Nothing is written.</summary>
    public IReadOnlyList<string> Check(JsonObject change)
    {
        try
        {
            var merged = Merge(change);
            return ConfigLoader.Validate(ConfigLoader.Parse(merged.ToJsonString(), Path));
        }
        catch (ConfigException ex)
        {
            return [ex.Message];
        }
    }

    /// <summary>
    /// Merges <paramref name="change"/> onto the file (objects merge key by key, values and arrays replace)
    /// and writes it. Throws <see cref="SettingsInvalidException"/> without writing when the result has problems.
    /// </summary>
    public ProjectSettingsView Save(JsonObject change)
    {
        var problems = Check(change);
        if (problems.Count > 0)
        {
            throw new SettingsInvalidException(problems);
        }

        var merged = Merge(change);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, merged.ToJsonString(Indented) + Environment.NewLine);
        return Read();
    }

    /// <summary>The context entries of the settings that exist in the repository, with their files.</summary>
    public IReadOnlyList<string> ListContextFiles()
    {
        var files = new List<string>();
        foreach (var entry in Read().Config.Context.Select(ContextPaths.Normalize))
        {
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(repositoryRoot, entry));
            if (File.Exists(full))
            {
                files.Add(entry);
            }
            else if (Directory.Exists(full))
            {
                files.AddRange(Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
                    .Select(f => System.IO.Path.GetRelativePath(repositoryRoot, f).Replace('\\', '/')));
            }
        }

        return files.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Reads a context file. Null when the file does not exist.</summary>
    public string? ReadContext(string relativePath)
    {
        var full = ResolveContextFile(relativePath);
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    /// <summary>Writes a context file inside one of the settings' context entries. Other paths are refused.</summary>
    public void WriteContext(string relativePath, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxContextFileBytes)
        {
            throw new SettingsInvalidException([$"The file is larger than {MaxContextFileBytes / 1024} KB. Context files are text the agent reads on every run; keep them short."]);
        }

        var full = ResolveContextFile(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private string ResolveContextFile(string relativePath)
    {
        if (ContextPaths.Problem(relativePath) is { } problem)
        {
            throw new SettingsInvalidException([problem]);
        }

        var normalized = ContextPaths.Normalize(relativePath);
        var root = System.IO.Path.GetFullPath(repositoryRoot);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, normalized));
        var inside = full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        var listed = Read().Config.Context.Select(ContextPaths.Normalize)
            .Any(entry => string.Equals(entry, normalized, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(entry + "/", StringComparison.OrdinalIgnoreCase));
        if (!inside || !listed)
        {
            throw new SettingsInvalidException([$"\"{relativePath}\" is not one of the project's context files. Add it (or its folder) to the \"context\" setting first."]);
        }

        return full;
    }

    private JsonObject Merge(JsonObject change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var current = File.Exists(Path) ? ParseObject(File.ReadAllText(Path), Path) : [];
        ConfigLoader.MergeInto(current, change);
        return current;
    }

    private static JsonObject ParseObject(string text, string source)
    {
        try
        {
            return JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new ConfigException($"{source} must hold one JSON object ({{ ... }}).");
        }
        catch (JsonException ex)
        {
            throw new ConfigException($"{source} is not valid JSON: {ex.Message}");
        }
    }
}
