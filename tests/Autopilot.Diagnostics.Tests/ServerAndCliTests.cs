using System.Text.Json;
using Autopilot.ClaudeCode;
using Autopilot.ClaudeCode.Hooks;
using Autopilot.Cli;
using Autopilot.Cli.Commands;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace Autopilot.Diagnostics.Tests;

public sealed class DoctorDiagnosticsTests
{
    [Fact]
    public async Task Ok_with_event_count_and_time_range()
    {
        using var project = Fixtures.ProjectWithLog();
        var config = ConfigLoader.Load(project.Path);

        var result = await DoctorCommand.DiagnosticsCheckAsync(project.Path, config.Modules.Diagnostics, CancellationToken.None);

        Assert.Equal(CheckStatus.Ok, result.Status);
        Assert.Contains("9 event(s)", result.Detail);
        Assert.Contains("from 2026-11-20 10:00:00Z to 2026-11-20 14:00:00Z", result.Detail);
        Assert.Contains("3 unreadable line(s) skipped", result.Detail);
    }

    [Fact]
    public async Task Missing_file_fails_with_a_fix()
    {
        using var folder = new TempFolder();
        var module = new ModuleConfig { Type = "jsonl", Settings = new Dictionary<string, string> { ["path"] = "telemetry/app.jsonl" } };

        var result = await DoctorCommand.DiagnosticsCheckAsync(folder.Path, module, CancellationToken.None);

        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Contains("does not exist", result.Detail);
        Assert.NotNull(result.Fix);
    }

    [Fact]
    public async Task Path_outside_the_project_fails()
    {
        using var folder = new TempFolder();
        var module = new ModuleConfig { Type = "jsonl", Settings = new Dictionary<string, string> { ["path"] = "../app.jsonl" } };

        var result = await DoctorCommand.DiagnosticsCheckAsync(folder.Path, module, CancellationToken.None);

        Assert.Equal(CheckStatus.Fail, result.Status);
        Assert.Contains("outside the project root", result.Detail);
        Assert.Contains("settings.path", result.Fix);
    }

    [Fact]
    public async Task File_without_events_fails_and_off_is_skipped()
    {
        using var folder = new TempFolder();
        folder.Write("log.jsonl", "garbage\n{\"no\":\"timestamp\"}\n");
        var module = new ModuleConfig { Type = "jsonl", Settings = new Dictionary<string, string> { ["path"] = "log.jsonl" } };

        var empty = await DoctorCommand.DiagnosticsCheckAsync(folder.Path, module, CancellationToken.None);
        Assert.Equal(CheckStatus.Fail, empty.Status);
        Assert.Contains("no readable events", empty.Detail);
        Assert.Contains("2 unreadable", empty.Detail);

        var off = await DoctorCommand.DiagnosticsCheckAsync(folder.Path, new ModuleConfig { Type = "none" }, CancellationToken.None);
        Assert.Equal(CheckStatus.Skip, off.Status);
    }
}

public sealed class McpServerTests(ITestOutputHelper output)
{
    /// <summary>The built CLI next to the tests; started the way Claude Code starts it.</summary>
    private static (string Command, IList<string> Args) CliCommand(string projectRoot)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "autopilot.dll");
        var server = DiagnosticsServerCommand.Build(Path.Combine(AppContext.BaseDirectory, "dotnet"), dll, projectRoot);
        return (server.Command, server.Args.ToList());
    }

    private static async Task<McpClient> StartAsync(string projectRoot)
    {
        var (command, args) = CliCommand(projectRoot);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "diagnostics",
            Command = command,
            Arguments = args,
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    }

    [Fact]
    public async Task Cli_serves_four_read_only_tools_over_stdio()
    {
        using var project = Fixtures.ProjectWithLog();
        await using var client = await StartAsync(project.Path);

        var tools = await client.ListToolsAsync();

        Assert.Equal(DiagnosticsMcp.ToolNames.Order(), tools.Select(t => t.Name).Order());
        Assert.All(tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(t.ProtocolTool.Annotations?.DestructiveHint);
        });

        var schema = tools.Single(t => t.Name == DiagnosticsMcp.GetErrorDetails).JsonSchema;
        Assert.Contains("exceptionType", schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Cli_answers_each_tool()
    {
        using var project = Fixtures.ProjectWithLog();
        await using var client = await StartAsync(project.Path);

        async Task<JsonElement> Call(string name, Dictionary<string, object?> args)
        {
            var result = await client.CallToolAsync(name, args);
            Assert.NotEqual(true, result.IsError);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            return JsonDocument.Parse(text).RootElement;
        }

        var errors = await Call(DiagnosticsMcp.ListRecentErrors, new() { ["limit"] = 1 });
        Assert.Equal("System.InvalidOperationException", errors.GetProperty("groups")[0].GetProperty("exceptionType").GetString());

        var details = await Call(DiagnosticsMcp.GetErrorDetails, new() { ["exceptionType"] = "NullReferenceException" });
        Assert.Equal(1, details.GetProperty("totalMatches").GetInt32());

        var search = await Call(DiagnosticsMcp.SearchLogs, new() { ["query"] = "slow" });
        Assert.Equal(1, search.GetProperty("totalMatches").GetInt32());

        var stats = await Call(DiagnosticsMcp.GetEndpointStats, new() { ["route"] = "GET /books" });
        Assert.Equal(4, stats.GetProperty("routes")[0].GetProperty("count").GetInt32());

        var bad = await client.CallToolAsync(DiagnosticsMcp.SearchLogs, new Dictionary<string, object?> { ["query"] = "" });
        Assert.True(bad.IsError);
        Assert.Contains("query is required", Assert.IsType<TextContentBlock>(Assert.Single(bad.Content)).Text);
    }

    [Fact]
    public async Task Mcp_command_refuses_to_start_when_diagnostics_is_off_and_writes_nothing_to_stdout()
    {
        using var project = new TempFolder();
        project.Write(".autopilot/autopilot.json", "{}");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliApp.RunAsync(["mcp", "diagnostics", "--project", project.Path], new CliContext(TextReader.Null, stdout, stderr));

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("Diagnostics is off", stderr.ToString());
    }

    [Fact]
    public async Task Mcp_command_reports_a_bad_path_on_stderr()
    {
        using var project = new TempFolder();
        project.Write(".autopilot/autopilot.json", """{"modules":{"diagnostics":{"type":"jsonl","settings":{"path":"../x.jsonl"}}}}""");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliApp.RunAsync(["mcp", "diagnostics", "--project", project.Path], new CliContext(TextReader.Null, stdout, stderr));

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains("outside the project root", stderr.ToString());
    }

    /// <summary>
    /// Real run against the installed Claude Code (haiku, subscription login). Only when
    /// AUTOPILOT_LIVE_CLAUDE=1. Uses the built autopilot apphost as both the hook and the MCP server.
    /// </summary>
    [Fact]
    public async Task Live_claude_calls_list_recent_errors_through_the_runner()
    {
        if (Environment.GetEnvironmentVariable("AUTOPILOT_LIVE_CLAUDE") != "1")
        {
            return;
        }

        var appHost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "autopilot.exe" : "autopilot");
        Assert.True(File.Exists(appHost), $"apphost not found at {appHost}");

        using var project = Fixtures.ProjectWithLog();
        using var work = new TempFolder();
        var worktree = work.Combine("wt");
        Directory.CreateDirectory(worktree);
        var runDirectory = work.Combine("run");

        var server = DiagnosticsServerCommand.Build(appHost, null, project.Path);
        var role = DiagnosticsMcp.WithDiagnosticsTools(RoleConfig.TriageDefault with { Model = "haiku", AllowedTools = [] });
        var request = new AgentRunRequest(
            AgentRole.Triage,
            worktree,
            "Answer in one short line.",
            "Call the mcp__diagnostics__list_recent_errors tool once, then reply with the exceptionType of the first group.",
            role,
            [],
            null,
            runDirectory,
            null,
            new Dictionary<string, McpServerSpec> { [DiagnosticsMcp.ServerName] = server });

        var runner = new ClaudeCodeRunner(new ClaudeCodeRunnerOptions
        {
            HookCommand = $"\"{appHost}\" hook pre-tool-use",
            Timeout = TimeSpan.FromMinutes(4),
            MaxTurns = 6,
        });
        var result = await runner.RunAsync(request, CancellationToken.None);

        var log = Path.Combine(runDirectory, PreToolUseHook.LogFileName);
        Assert.True(File.Exists(log), $"no hook log; result: {result.Error} {result.ResultText}");
        var lines = File.ReadAllLines(log);
        foreach (var line in lines)
        {
            output.WriteLine($"hook: {line}");
        }

        output.WriteLine($"result: succeeded={result.Succeeded} turns={result.Turns} model={result.Model} text={result.ResultText}");
        var entries = lines.Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Contains(entries, e =>
            e.GetProperty("tool").GetString() == "mcp__diagnostics__list_recent_errors"
            && e.GetProperty("decision").GetString() == "allow");
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("InvalidOperationException", result.ResultText);
    }
}
