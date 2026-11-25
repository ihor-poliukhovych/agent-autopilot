using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Autopilot.Core.Process;

namespace Autopilot.Core.Config;

/// <summary>Reads, checks and writes <c>.autopilot/autopilot.json</c>.</summary>
public static class ConfigLoader
{
    public static IReadOnlyList<string> KnownWorkItemTypes { get; } = ["local", "azure-devops"];
    public static IReadOnlyList<string> KnownPublisherTypes { get; } = ["local", "azure-devops"];
    public static IReadOnlyList<string> KnownDiagnosticsTypes { get; } = ["none", "jsonl"];

    /// <summary>Module types that are known but not built yet.</summary>
    public static IReadOnlyList<string> NotAvailableYet { get; } = [];

    /// <summary>camelCase, comments and trailing commas allowed, enums as kebab-case strings.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public static string ConfigPath(string projectRoot) =>
        Path.Combine(projectRoot, AutopilotConfig.FolderName, AutopilotConfig.FileName);

    public static bool Exists(string projectRoot) => File.Exists(ConfigPath(projectRoot));

    /// <summary>Loads the project's config. Throws <see cref="ConfigException"/> with a readable message.</summary>
    public static AutopilotConfig Load(string projectRoot) => LoadFile(ConfigPath(projectRoot));

    public static AutopilotConfig LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new ConfigException($"Config file not found: {path}. Run 'autopilot init' to create it.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>
    /// Parses the file onto the defaults: the JSON is deep-merged over the serialized
    /// <see cref="AutopilotConfig"/> defaults (objects merge key by key, arrays and values replace), so a
    /// file that sets only <c>roles.triage.model</c> keeps every other triage default.
    /// </summary>
    public static AutopilotConfig Parse(string json, string source = "autopilot.json")
    {
        try
        {
            var node = JsonNode.Parse(json, nodeOptions: null, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? throw new ConfigException($"{source} is empty.");
            if (node is not JsonObject user)
            {
                throw new ConfigException($"{source} must hold one JSON object ({{ ... }}).");
            }

            var merged = JsonSerializer.SerializeToNode(new AutopilotConfig(), JsonOptions)!.AsObject();
            MergeInto(merged, user);
            return merged.Deserialize<AutopilotConfig>(JsonOptions)
                ?? throw new ConfigException($"{source} is empty.");
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : string.Empty;
            throw new ConfigException($"{source} is not valid JSON{where}: {ex.Message}");
        }
    }

    /// <summary>Copies <paramref name="overlay"/> onto <paramref name="target"/>: objects merge, everything else replaces. Keys match ignoring case.</summary>
    public static void MergeInto(JsonObject target, JsonObject overlay)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(overlay);
        foreach (var (key, value) in overlay.ToList())
        {
            var existingKey = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (existingKey is not null && target[existingKey] is JsonObject targetChild && value is JsonObject overlayChild)
            {
                MergeInto(targetChild, overlayChild);
                continue;
            }

            if (existingKey is not null)
            {
                target.Remove(existingKey);
            }

            target[existingKey ?? key] = value?.DeepClone();
        }
    }

    public static string Serialize(AutopilotConfig config) => JsonSerializer.Serialize(config, JsonOptions);

    public static void Save(AutopilotConfig config, string projectRoot) => SaveFile(config, ConfigPath(projectRoot));

    public static void SaveFile(AutopilotConfig config, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serialize(config) + Environment.NewLine);
    }

    /// <summary>Returns every problem in plain English. An empty list means the config is usable.</summary>
    public static IReadOnlyList<string> Validate(AutopilotConfig config)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(config.BaseBranch))
        {
            problems.Add("baseBranch is empty. Set it to the branch agent work starts from, for example \"main\".");
        }

        if (string.IsNullOrWhiteSpace(config.BranchPrefix))
        {
            problems.Add("branchPrefix is empty. Use something like \"autopilot/\" so agent branches are easy to spot.");
        }

        if (config.Commands is null)
        {
            problems.Add("commands is missing. Add \"commands\": { \"build\": \"...\", \"test\": \"...\" }.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(config.Commands.Build))
            {
                problems.Add("commands.build is empty. Set the command that builds the project, for example \"dotnet build\".");
            }

            if (string.IsNullOrWhiteSpace(config.Commands.Test))
            {
                problems.Add("commands.test is empty. Set the command that runs the tests, for example \"dotnet test\".");
            }
        }

        var writable = config.WritablePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
        if (writable.Count == 0)
        {
            problems.Add("writablePaths is empty. List the folders the agent may change, for example \"src/**\" and \"tests/**\".");
        }

        foreach (var pattern in writable.Where(TargetsAutopilotFolder))
        {
            problems.Add($"writablePaths entry \"{pattern}\" points into .autopilot/. The agent may never change its own settings; remove it.");
        }

        if (config.Gate is null)
        {
            problems.Add("gate is missing. Remove the \"gate\" key to use the defaults, or fill in its limits.");
        }
        else
        {
            AddIfNotPositive(problems, "gate.maxFilesChanged", config.Gate.MaxFilesChanged);
            AddIfNotPositive(problems, "gate.maxSourceLinesChanged", config.Gate.MaxSourceLinesChanged);
            AddIfNotPositive(problems, "gate.maxTestLinesChanged", config.Gate.MaxTestLinesChanged);
        }

        if (config.Roles is null)
        {
            problems.Add("roles is missing. Remove the \"roles\" key to use the defaults.");
        }
        else
        {
            foreach (var role in Enum.GetValues<AgentRole>())
            {
                var name = JsonNamingPolicy.CamelCase.ConvertName(role.ToString());
                var roleConfig = config.Roles.For(role);
                if (roleConfig is null)
                {
                    problems.Add($"roles.{name} is missing. Remove it to use the default, or fill it in.");
                    continue;
                }

                AddIfNotPositive(problems, $"roles.{name}.maxToolCalls", roleConfig.MaxToolCalls);
                if (roleConfig.AllowedTools is null || roleConfig.AllowedTools.Count == 0)
                {
                    problems.Add($"roles.{name}.allowedTools is empty. The {name} agent needs at least Read, Grep and Glob.");
                }
            }
        }

        foreach (var name in config.PassEnvironment ?? [])
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add("passEnvironment has an empty entry. Remove it.");
            }
            else if (ChildEnvironment.IsNeverPassed(name))
            {
                problems.Add($"passEnvironment entry \"{name}\" is not allowed: ANTHROPIC_* and AUTOPILOT_* variables are never passed to the agent or to build and test commands. Remove it.");
            }
        }

        if (config.Modules is null)
        {
            problems.Add("modules is missing. Remove the \"modules\" key to use local files.");
        }
        else
        {
            CheckModule(problems, "modules.workItems", config.Modules.WorkItems, KnownWorkItemTypes);
            CheckModule(problems, "modules.publisher", config.Modules.Publisher, KnownPublisherTypes);
            CheckModule(problems, "modules.diagnostics", config.Modules.Diagnostics, KnownDiagnosticsTypes);
        }

        return problems;
    }

    /// <summary>
    /// True when a glob pattern names something under <c>.autopilot/</c> explicitly. Broad patterns such
    /// as <c>**</c> are allowed: <c>.autopilot/**</c> is always protected by the hook and the gate.
    /// </summary>
    public static bool TargetsAutopilotFolder(string pattern)
    {
        var normalized = pattern.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.TrimStart('/');
        return normalized.Equals(AutopilotConfig.FolderName, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(AutopilotConfig.FolderName + "/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("**/" + AutopilotConfig.FolderName, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(".autopilot*", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(".*", StringComparison.Ordinal);
    }

    private static void AddIfNotPositive(List<string> problems, string name, int value)
    {
        if (value <= 0)
        {
            problems.Add($"{name} is {value}. It must be greater than 0.");
        }
    }

    private static void CheckModule(List<string> problems, string name, ModuleConfig? module, IReadOnlyList<string> known)
    {
        if (module is null || string.IsNullOrWhiteSpace(module.Type))
        {
            problems.Add($"{name}.type is missing. Use one of: {string.Join(", ", known)}.");
            return;
        }

        if (!known.Contains(module.Type, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{name}.type \"{module.Type}\" is unknown. Use one of: {string.Join(", ", known)}.");
            return;
        }

        if (NotAvailableYet.Contains(module.Type, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{name}.type \"{module.Type}\" is not available yet. Use \"{known[0]}\" for now.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,

            // Readable files: quotes and plus signs are written as they are, not as unicode escapes.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>The config file is missing or cannot be read. The message says what to do.</summary>
public sealed class ConfigException(string message) : Exception(message);
