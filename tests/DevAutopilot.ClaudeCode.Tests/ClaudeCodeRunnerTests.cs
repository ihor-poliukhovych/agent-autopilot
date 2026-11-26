using DevAutopilot.Core.Config;

namespace DevAutopilot.ClaudeCode.Tests;

public class ClaudeEnvironmentTests
{
    [Fact]
    public void Only_the_allow_list_is_kept_and_anthropic_variables_are_always_removed()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ANTHROPIC_API_KEY"] = "k",
            ["anthropic_auth_token"] = "t",
            ["ANTHROPIC_MODEL"] = "m",
            ["AUTOPILOT_ADO_PAT"] = "p",
            ["Path"] = "/usr/bin",
            ["DOTNET_ROOT"] = "/dotnet",
            ["MSBUILDDEBUGPATH"] = "/x",
            ["GITHUB_TOKEN"] = "secret",
            ["MY_TOOL_HOME"] = "/tool",
        };

        ClaudeEnvironment.Apply(env, ["MY_TOOL_HOME", "AUTOPILOT_ADO_PAT", "ANTHROPIC_MODEL"]);

        Assert.Equal(["DOTNET_ROOT", "MSBUILDDEBUGPATH", "MY_TOOL_HOME", "Path"], env.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Apply_works_on_process_start_info_environment()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("x");
        psi.Environment["ANTHROPIC_API_KEY"] = "k";
        psi.Environment["SOME_SECRET"] = "s";
        psi.Environment["TEMP"] = "t";

        ClaudeEnvironment.Apply(psi.Environment);

        Assert.False(psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(psi.Environment.ContainsKey("SOME_SECRET"));
        Assert.True(psi.Environment.ContainsKey("TEMP"));
    }
}

public class HookRanCheckTests : IDisposable
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

    private static DevAutopilot.Core.AgentRunResult Result(int turns) =>
        new(true, "done", null, turns, DevAutopilot.Core.TokenUsage.Zero, TimeSpan.FromSeconds(1), "m", null);

    [Fact]
    public void Several_turns_without_a_hook_log_fail_the_run()
    {
        var checkedResult = ClaudeCodeRunner.CheckHookRan(Result(3), ClaudeRunFiles.For(_dir));

        Assert.False(checkedResult.Succeeded);
        Assert.Contains("the policy hook did not run", checkedResult.Error);
    }

    [Fact]
    public void Empty_hook_log_fails_the_run()
    {
        File.WriteAllText(Path.Combine(_dir, DevAutopilot.ClaudeCode.Hooks.PreToolUseHook.LogFileName), string.Empty);

        Assert.False(ClaudeCodeRunner.CheckHookRan(Result(2), ClaudeRunFiles.For(_dir)).Succeeded);
    }

    [Fact]
    public void Hook_log_with_entries_or_a_single_turn_keeps_the_result()
    {
        Assert.True(ClaudeCodeRunner.CheckHookRan(Result(1), ClaudeRunFiles.For(_dir)).Succeeded);

        File.WriteAllText(Path.Combine(_dir, DevAutopilot.ClaudeCode.Hooks.PreToolUseHook.LogFileName), "{\"tool\":\"Read\"}\n");
        Assert.True(ClaudeCodeRunner.CheckHookRan(Result(5), ClaudeRunFiles.For(_dir)).Succeeded);
    }
}

public class ClaudeCodeRunnerTests : IDisposable
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
    public async Task Missing_executable_is_a_failed_result()
    {
        string wt = Directory.CreateDirectory(Path.Combine(_dir, "wt")).FullName;
        var runner = new ClaudeCodeRunner(TestData.Options() with { ClaudeExecutable = Path.Combine(_dir, "no-such-claude") });

        var result = await runner.RunAsync(TestData.Request(workingDirectory: wt, runDirectory: Path.Combine(_dir, "run")), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task Run_directory_inside_the_worktree_is_refused()
    {
        string wt = Directory.CreateDirectory(Path.Combine(_dir, "wt")).FullName;
        var runner = new ClaudeCodeRunner(TestData.Options());

        var result = await runner.RunAsync(TestData.Request(workingDirectory: wt, runDirectory: Path.Combine(wt, "run")), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("outside the working directory", result.Error);
        Assert.False(Directory.Exists(Path.Combine(wt, "run")));
    }

    [Fact]
    public async Task Missing_working_directory_is_refused()
    {
        var runner = new ClaudeCodeRunner(TestData.Options());
        var result = await runner.RunAsync(TestData.Request(workingDirectory: Path.Combine(_dir, "missing"), runDirectory: Path.Combine(_dir, "run")), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("does not exist", result.Error);
    }

    /// <summary>
    /// Real run against the installed Claude Code. Uses the subscription, so it only runs when
    /// AUTOPILOT_LIVE_CLAUDE=1. AUTOPILOT_LIVE_HOOK must be a command that allows every call.
    /// </summary>
    [Fact]
    public async Task Live_run_with_structured_output()
    {
        string? hook = Environment.GetEnvironmentVariable("AUTOPILOT_LIVE_HOOK");
        if (Environment.GetEnvironmentVariable("AUTOPILOT_LIVE_CLAUDE") != "1" || string.IsNullOrWhiteSpace(hook))
        {
            return;
        }

        string wt = Directory.CreateDirectory(Path.Combine(_dir, "wt")).FullName;
        File.WriteAllText(Path.Combine(wt, "hello.txt"), "hello-content");
        string run = Path.Combine(_dir, "run");
        var role = RoleConfig.TriageDefault with { Model = "haiku", AllowedTools = ["Read"] };
        var request = TestData.Request(
            role,
            schema: """{"type":"object","properties":{"content":{"type":"string"}},"required":["content"]}""",
            systemPrompt: "Answer briefly.",
            workingDirectory: wt,
            runDirectory: run) with { Prompt = "Read hello.txt with the Read tool and return its content." };

        var result = await new ClaudeCodeRunner(new ClaudeCodeRunnerOptions { HookCommand = hook, Timeout = TimeSpan.FromMinutes(3) })
            .RunAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("hello-content", result.StructuredOutput!.Value.GetProperty("content").GetString());
        Assert.True(File.Exists(Path.Combine(run, ClaudeRunFiles.StdoutFileName)));
        Assert.True(File.Exists(Path.Combine(run, ClaudeRunFiles.StderrFileName)));
    }
}
