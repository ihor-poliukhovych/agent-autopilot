using System.Text.Json;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Gate;

namespace DevAutopilot.ClaudeCode.Tests;

public class ClaudeRunFilesTests : IDisposable
{
    private readonly string _dir = TestData.NewTempDirectory();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Settings_have_one_pre_tool_use_hook_for_all_tools()
    {
        string json = ClaudeRunFileWriter.BuildSettingsJson("hook --policy \"p\"", 30);
        using var doc = JsonDocument.Parse(json);

        var groups = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse");
        Assert.Equal(1, groups.GetArrayLength());
        var group = groups[0];
        Assert.Equal("*", group.GetProperty("matcher").GetString());
        var hook = Assert.Single(group.GetProperty("hooks").EnumerateArray());
        Assert.Equal("command", hook.GetProperty("type").GetString());
        Assert.Equal("hook --policy \"p\"", hook.GetProperty("command").GetString());
        Assert.Equal(30, hook.GetProperty("timeout").GetInt32());
        Assert.Single(doc.RootElement.EnumerateObject());
    }

    [Fact]
    public void Hook_command_appends_the_policy_with_forward_slashes()
    {
        string line = ClaudeRunFileWriter.BuildHookCommand("\"C:\\tools\\autopilot.exe\" hook pre-tool-use ", "C:\\runs\\1\\policy.json");
        Assert.Equal("\"C:\\tools\\autopilot.exe\" hook pre-tool-use --policy \"C:/runs/1/policy.json\"", line);
    }

    [Fact]
    public void Hook_command_rejects_quotes_in_the_path()
    {
        Assert.Throws<ArgumentException>(() => ClaudeRunFileWriter.BuildHookCommand("hook", "a\"b"));
    }

    [Fact]
    public void Write_creates_policy_settings_mcp_and_prompt_files()
    {
        string run = Path.Combine(_dir, "run");
        string wt = Path.Combine(_dir, "wt");
        var request = TestData.Request(workingDirectory: wt, runDirectory: run, systemPrompt: "rules");

        ClaudeRunFiles files = ClaudeRunFileWriter.Write(request, TestData.Options());

        var policy = PolicyJson.Deserialize(File.ReadAllText(files.PolicyFile));
        Assert.Equal(AgentRole.Implementer, policy.Role);
        Assert.Equal(Path.GetFullPath(wt), policy.WorktreeRoot);
        Assert.Equal(RoleConfig.ImplementerDefault.AllowedTools, policy.AllowedTools);
        Assert.Equal(RoleConfig.ImplementerDefault.AllowedCommands, policy.AllowedCommands);
        Assert.Equal(RoleConfig.ImplementerDefault.MaxToolCalls, policy.MaxToolCalls);
        Assert.Equal(["src/**", "tests/**"], policy.WritablePaths);
        Assert.Equal(MergeGate.ProtectedPaths, policy.ProtectedPaths); // the request names no extra paths
        Assert.Equal(Path.Combine(Path.GetFullPath(run), ClaudeRunFiles.CounterFileName), policy.CounterFile);

        string settings = File.ReadAllText(files.SettingsFile);
        Assert.Contains(files.PolicyFile.Replace('\\', '/'), settings.Replace("\\\"", "\""));

        using var mcp = JsonDocument.Parse(File.ReadAllText(files.McpConfigFile));
        Assert.Empty(mcp.RootElement.GetProperty("mcpServers").EnumerateObject());

        Assert.Equal("rules", File.ReadAllText(files.SystemPromptFile));
    }

    [Fact]
    public void Write_resets_the_call_counter()
    {
        string run = Path.Combine(_dir, "run");
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, ClaudeRunFiles.CounterFileName), "41");

        ClaudeRunFileWriter.Write(TestData.Request(runDirectory: run, workingDirectory: Path.Combine(_dir, "wt")), TestData.Options());

        Assert.False(File.Exists(Path.Combine(run, ClaudeRunFiles.CounterFileName)));
    }

    [Fact]
    public void Policy_json_round_trips_with_role_as_text()
    {
        var policy = ClaudeRunFileWriter.BuildPolicy(TestData.Request(), ClaudeRunFiles.For(_dir));
        string json = PolicyJson.Serialize(policy);

        Assert.Contains("\"Implementer\"", json);
        var back = PolicyJson.Deserialize(json);
        Assert.Equal(policy.Role, back.Role);
        Assert.Equal(policy.AllowedTools, back.AllowedTools);
        Assert.Equal(policy.CounterFile, back.CounterFile);
    }

    [Fact]
    public void Risk_paths_from_the_request_are_protected_too()
    {
        var request = TestData.Request() with { ProtectedPaths = new AutopilotConfig().RiskPaths };

        var policy = ClaudeRunFileWriter.BuildPolicy(request, ClaudeRunFiles.For(_dir));

        Assert.Contains(".autopilot/**", policy.ProtectedPaths);
        Assert.Contains("**/*.csproj", policy.ProtectedPaths);
        Assert.Contains("**/*.props", policy.ProtectedPaths);
        Assert.Contains("**/*.targets", policy.ProtectedPaths);
    }

    [Fact]
    public void Configured_commands_reach_the_policy_and_survive_the_json()
    {
        const string build = "dotnet build \"App.sln\" -p:Deterministic=true";
        var request = TestData.Request() with { ConfiguredCommands = [build, "dotnet test \"App.sln\""] };

        var policy = ClaudeRunFileWriter.BuildPolicy(request, ClaudeRunFiles.For(_dir));
        var back = PolicyJson.Deserialize(PolicyJson.Serialize(policy));

        Assert.Equal([build, "dotnet test \"App.sln\""], back.ConfiguredCommands);
        Assert.Contains("configuredCommands", PolicyJson.Serialize(policy), StringComparison.Ordinal);
    }

    [Fact]
    public void A_policy_file_without_configured_commands_still_reads()
    {
        var policy = ClaudeRunFileWriter.BuildPolicy(TestData.Request(), ClaudeRunFiles.For(_dir));
        var node = System.Text.Json.Nodes.JsonNode.Parse(PolicyJson.Serialize(policy))!.AsObject();
        node.Remove("configuredCommands");

        var back = PolicyJson.Deserialize(node.ToJsonString());

        Assert.Null(policy.ConfiguredCommands);
        Assert.Null(back.ConfiguredCommands);
        Assert.Equal(policy.AllowedCommands, back.AllowedCommands);
    }

    [Fact]
    public void Default_hook_timeout_is_120_seconds()
    {
        Assert.Equal(120, TestData.Options().HookTimeoutSeconds);
    }
}
