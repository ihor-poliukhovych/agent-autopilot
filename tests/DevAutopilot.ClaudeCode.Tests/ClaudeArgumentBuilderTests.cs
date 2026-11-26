using DevAutopilot.Core.Config;

namespace DevAutopilot.ClaudeCode.Tests;

public class ClaudeArgumentBuilderTests
{
    private static readonly ClaudeRunFiles Files = ClaudeRunFiles.For(Path.Combine(Path.GetTempPath(), "run"));

    private static IReadOnlyList<string> Build(RoleConfig? role = null, string? schema = null, ClaudeCodeRunnerOptions? options = null, string systemPrompt = "sys") =>
        ClaudeArgumentBuilder.Build(TestData.Request(role, schema, systemPrompt), Files, options ?? TestData.Options());

    private static string? ValueOf(IReadOnlyList<string> args, string flag)
    {
        int i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    [Fact]
    public void Starts_with_print_mode_and_json_output()
    {
        var args = Build();
        Assert.Equal("-p", args[0]);
        Assert.Equal("json", ValueOf(args, "--output-format"));
    }

    [Fact]
    public void Prompt_is_not_on_the_command_line()
    {
        Assert.DoesNotContain("Do the task.", Build());
    }

    [Fact]
    public void Model_is_passed_when_set()
    {
        var args = Build(RoleConfig.ImplementerDefault with { Model = "haiku" });
        Assert.Equal("haiku", ValueOf(args, "--model"));
    }

    [Fact]
    public void Model_is_left_out_when_null()
    {
        Assert.DoesNotContain("--model", Build(RoleConfig.ImplementerDefault with { Model = null }));
    }

    [Fact]
    public void System_prompt_goes_through_a_file()
    {
        var args = Build();
        Assert.Equal(Files.SystemPromptFile, ValueOf(args, "--append-system-prompt-file"));
        Assert.DoesNotContain("sys", args);
        Assert.DoesNotContain("--system-prompt", args);
    }

    [Fact]
    public void Empty_system_prompt_adds_no_flag()
    {
        Assert.DoesNotContain("--append-system-prompt-file", Build(systemPrompt: ""));
    }

    [Fact]
    public void Role_tools_are_allowed_and_are_the_only_built_ins()
    {
        var args = Build(RoleConfig.TriageDefault);
        Assert.Equal("Read,Grep,Glob", ValueOf(args, "--allowedTools"));
        Assert.Equal("Read,Grep,Glob", ValueOf(args, "--tools"));
    }

    [Fact]
    public void Mcp_tools_are_allowed_but_not_listed_as_built_ins()
    {
        var role = RoleConfig.TriageDefault with { AllowedTools = ["Read", "mcp__diag__query"] };
        var args = Build(role, options: TestData.Options(mcp: ["diag.json"]));
        Assert.Equal("Read,mcp__diag__query", ValueOf(args, "--allowedTools"));
        Assert.Equal("Read", ValueOf(args, "--tools"));
    }

    [Fact]
    public void No_tools_gives_an_empty_tools_list_and_no_allowed_flag()
    {
        var args = Build(RoleConfig.TriageDefault with { AllowedTools = [] });
        Assert.Equal(string.Empty, ValueOf(args, "--tools"));
        Assert.DoesNotContain("--allowedTools", args);
    }

    [Fact]
    public void Risky_tools_that_are_not_allowed_are_disallowed()
    {
        string disallowed = ValueOf(Build(), "--disallowedTools")!;
        foreach (string tool in new[] { "WebFetch", "WebSearch", "Task", "Agent", "NotebookEdit" })
        {
            Assert.Contains(tool, disallowed.Split(','));
        }
    }

    [Fact]
    public void A_risky_tool_the_role_allows_is_not_disallowed()
    {
        var role = RoleConfig.TriageDefault with { AllowedTools = ["Read", "WebFetch"] };
        string disallowed = ValueOf(Build(role), "--disallowedTools")!;
        Assert.DoesNotContain("WebFetch", disallowed.Split(','));
        Assert.Contains("WebSearch", disallowed.Split(','));
    }

    [Fact]
    public void Without_mcp_files_all_mcp_tools_are_disallowed()
    {
        Assert.Contains("mcp__*", ValueOf(Build(), "--disallowedTools")!.Split(','));
    }

    [Fact]
    public void With_mcp_files_they_follow_the_run_config()
    {
        var args = Build(options: TestData.Options(mcp: ["a.json", "b.json"])).ToList();
        int i = args.IndexOf("--mcp-config");
        Assert.Equal([Files.McpConfigFile, "a.json", "b.json"], args.Skip(i + 1).Take(3));
        Assert.DoesNotContain("mcp__*", ValueOf(args, "--disallowedTools")!.Split(','));
    }

    [Fact]
    public void Isolation_flags_are_present()
    {
        var args = Build();
        Assert.Equal("dontAsk", ValueOf(args, "--permission-mode"));
        Assert.Equal(Files.SettingsFile, ValueOf(args, "--settings"));
        Assert.Equal(string.Empty, ValueOf(args, "--setting-sources"));
        Assert.Contains("--strict-mcp-config", args);
        Assert.Equal(Files.McpConfigFile, ValueOf(args, "--mcp-config"));
        Assert.Contains("--no-session-persistence", args);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
        Assert.DoesNotContain("--bare", args);
    }

    [Fact]
    public void Schema_is_passed_when_present()
    {
        const string schema = "{\"type\":\"object\"}";
        Assert.Equal(schema, ValueOf(Build(schema: schema), "--json-schema"));
    }

    [Fact]
    public void Schema_is_left_out_when_absent()
    {
        Assert.DoesNotContain("--json-schema", Build(schema: null));
    }

    [Fact]
    public void Max_turns_only_when_configured()
    {
        Assert.DoesNotContain("--max-turns", Build());
        Assert.Equal("12", ValueOf(Build(options: TestData.Options(maxTurns: 12)), "--max-turns"));
    }
}
