using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DevAutopilot.Core;
using DevAutopilot.Core.Gate;
using DevAutopilot.Core.Policy;

namespace DevAutopilot.ClaudeCode;

/// <summary>Paths of the files one run writes to its run directory.</summary>
public sealed record ClaudeRunFiles(
    string RunDirectory,
    string PolicyFile,
    string SettingsFile,
    string McpConfigFile,
    string SystemPromptFile,
    string CounterFile,
    string StdoutFile,
    string StderrFile)
{
    public const string PolicyFileName = "policy.json";
    public const string SettingsFileName = "settings.json";
    public const string McpConfigFileName = "mcp.json";
    public const string SystemPromptFileName = "system-prompt.md";
    public const string CounterFileName = "tool-calls.count";
    public const string StdoutFileName = "claude-stdout.json";
    public const string StderrFileName = "claude-stderr.txt";

    public static ClaudeRunFiles For(string runDirectory)
    {
        string dir = Path.GetFullPath(runDirectory);
        return new ClaudeRunFiles(
            dir,
            Path.Combine(dir, PolicyFileName),
            Path.Combine(dir, SettingsFileName),
            Path.Combine(dir, McpConfigFileName),
            Path.Combine(dir, SystemPromptFileName),
            Path.Combine(dir, CounterFileName),
            Path.Combine(dir, StdoutFileName),
            Path.Combine(dir, StderrFileName));
    }
}

/// <summary>JSON format of policy.json, shared by the runner (writes) and the hook (reads).</summary>
public static class PolicyJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(ToolPolicy policy) => JsonSerializer.Serialize(policy, Options);

    public static ToolPolicy Deserialize(string json) =>
        JsonSerializer.Deserialize<ToolPolicy>(json, Options)
        ?? throw new InvalidDataException("The policy file is empty.");
}

/// <summary>Builds the content of the files the runner writes before it starts Claude Code.</summary>
public static class ClaudeRunFileWriter
{
    public static ToolPolicy BuildPolicy(AgentRunRequest request, ClaudeRunFiles files)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(files);

        return new ToolPolicy(
            request.Role,
            Path.GetFullPath(request.WorkingDirectory),
            request.RoleConfig.AllowedTools,
            request.WritablePaths,
            MergeGate.ProtectedPaths.Concat(request.ProtectedPaths ?? []).Distinct(StringComparer.Ordinal).ToList(),
            request.RoleConfig.AllowedCommands,
            request.RoleConfig.MaxToolCalls,
            files.CounterFile,
            request.ConfiguredCommands is { Count: > 0 } configured ? configured : null);
    }

    /// <summary>The hook command line: the configured command plus <c>--policy "&lt;file&gt;"</c>.</summary>
    public static string BuildHookCommand(string hookCommand, string policyFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hookCommand);

        // Forward slashes work on Windows too and survive both cmd.exe and a POSIX shell.
        string path = policyFile.Replace('\\', '/');
        if (path.Contains('"'))
        {
            throw new ArgumentException("The policy path must not contain a double quote.", nameof(policyFile));
        }

        return $"{hookCommand.Trim()} --policy \"{path}\"";
    }

    /// <summary>Settings passed with <c>--settings</c>: one PreToolUse hook for every tool.</summary>
    public static string BuildSettingsJson(string hookCommandLine, int hookTimeoutSeconds)
    {
        var settings = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["matcher"] = "*",
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = hookCommandLine,
                                ["timeout"] = hookTimeoutSeconds,
                            },
                        },
                    },
                },
            },
        };

        return settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string EmptyMcpConfigJson => "{\n  \"mcpServers\": {}\n}";

    /// <summary>
    /// The run's mcp.json: <c>{"mcpServers": {"&lt;name&gt;": {"type": "stdio", "command": ..., "args": [...]}}}</c>.
    /// No <c>env</c> entry: the server inherits Claude Code's already filtered environment.
    /// </summary>
    public static string BuildMcpConfigJson(IReadOnlyDictionary<string, McpServerSpec>? servers)
    {
        if (servers is null || servers.Count == 0)
        {
            return EmptyMcpConfigJson;
        }

        var entries = new JsonObject();
        foreach (var (name, server) in servers.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(server.Command);
            var args = new JsonArray();
            foreach (var arg in server.Args)
            {
                args.Add(arg);
            }

            entries[name] = new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = server.Command,
                ["args"] = args,
            };
        }

        return new JsonObject { ["mcpServers"] = entries }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Creates the run directory and writes policy, settings, MCP config and system prompt.</summary>
    public static ClaudeRunFiles Write(AgentRunRequest request, ClaudeCodeRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        ClaudeRunFiles files = ClaudeRunFiles.For(request.RunDirectory);
        Directory.CreateDirectory(files.RunDirectory);

        File.WriteAllText(files.PolicyFile, PolicyJson.Serialize(BuildPolicy(request, files)));
        File.WriteAllText(
            files.SettingsFile,
            BuildSettingsJson(BuildHookCommand(options.HookCommand, files.PolicyFile), options.HookTimeoutSeconds));
        File.WriteAllText(files.McpConfigFile, BuildMcpConfigJson(request.McpServers));
        File.WriteAllText(files.SystemPromptFile, request.SystemPrompt ?? string.Empty);

        // A fresh counter for every run.
        if (File.Exists(files.CounterFile))
        {
            File.Delete(files.CounterFile);
        }

        return files;
    }
}
