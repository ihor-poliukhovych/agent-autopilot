using Autopilot.Core;
using Autopilot.Core.Config;

namespace Autopilot.ClaudeCode.Tests;

internal static class TestData
{
    public static AgentRunRequest Request(
        RoleConfig? role = null,
        string? schema = null,
        string systemPrompt = "You are the implementer.",
        string? workingDirectory = null,
        string? runDirectory = null) => new(
        AgentRole.Implementer,
        workingDirectory ?? Path.Combine(Path.GetTempPath(), "wt"),
        systemPrompt,
        "Do the task.",
        role ?? RoleConfig.ImplementerDefault,
        ["src/**", "tests/**"],
        schema,
        runDirectory ?? Path.Combine(Path.GetTempPath(), "run"));

    public static ClaudeCodeRunnerOptions Options(IReadOnlyList<string>? mcp = null, int? maxTurns = null) => new()
    {
        HookCommand = "\"C:\\tools\\autopilot.exe\" hook pre-tool-use",
        McpConfigFiles = mcp ?? [],
        MaxTurns = maxTurns,
    };

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
