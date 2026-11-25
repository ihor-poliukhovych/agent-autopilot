using System.Text.Json;
using Autopilot.ClaudeCode;
using Autopilot.Cli;
using Autopilot.Cli.Commands;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Diagnostics;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Git;
using Autopilot.Core.Local;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Policy;

namespace Autopilot.Diagnostics.Tests;

public sealed class McpConfigTests
{
    private static readonly McpServerSpec Server = new(@"C:\tools\autopilot.exe", ["mcp", "diagnostics", "--project", @"C:\repo"]);

    private static AgentRunRequest Request(AgentRole role, RoleConfig roleConfig, string runDirectory, IReadOnlyDictionary<string, McpServerSpec>? servers) => new(
        role, Path.GetTempPath(), "system", "prompt", roleConfig, [], null, runDirectory, null, servers);

    [Fact]
    public void Mcp_json_has_one_stdio_server_named_diagnostics()
    {
        var json = ClaudeRunFileWriter.BuildMcpConfigJson(new Dictionary<string, McpServerSpec> { [DiagnosticsMcp.ServerName] = Server });

        var servers = JsonDocument.Parse(json).RootElement.GetProperty("mcpServers");
        var diagnostics = Assert.Single(servers.EnumerateObject());
        Assert.Equal("diagnostics", diagnostics.Name);
        Assert.Equal("stdio", diagnostics.Value.GetProperty("type").GetString());
        Assert.Equal(@"C:\tools\autopilot.exe", diagnostics.Value.GetProperty("command").GetString());
        Assert.Equal(["mcp", "diagnostics", "--project", @"C:\repo"], diagnostics.Value.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.False(diagnostics.Value.TryGetProperty("env", out _));
    }

    [Fact]
    public void No_servers_gives_the_empty_config()
    {
        Assert.Equal(ClaudeRunFileWriter.EmptyMcpConfigJson, ClaudeRunFileWriter.BuildMcpConfigJson(null));
        Assert.Equal(ClaudeRunFileWriter.EmptyMcpConfigJson, ClaudeRunFileWriter.BuildMcpConfigJson(new Dictionary<string, McpServerSpec>()));
    }

    [Fact]
    public void Runner_writes_the_servers_to_the_run_mcp_json_and_keeps_strict_mcp_config()
    {
        using var folder = new TempFolder();
        var role = DiagnosticsMcp.WithDiagnosticsTools(RoleConfig.TriageDefault);
        var request = Request(AgentRole.Triage, role, folder.Combine("run"), new Dictionary<string, McpServerSpec> { ["diagnostics"] = Server });
        var options = new ClaudeCodeRunnerOptions { HookCommand = "\"C:\\tools\\autopilot.exe\" hook pre-tool-use" };

        var files = ClaudeRunFileWriter.Write(request, options);
        var args = ClaudeArgumentBuilder.Build(request, files, options);

        Assert.Contains("\"diagnostics\"", File.ReadAllText(files.McpConfigFile));
        Assert.Contains("--strict-mcp-config", args);
        var mcpConfig = args.ToList().IndexOf("--mcp-config");
        Assert.Equal(files.McpConfigFile, args[mcpConfig + 1]);

        var allowed = args[args.ToList().IndexOf("--allowedTools") + 1].Split(',');
        Assert.Superset(DiagnosticsMcp.ClaudeToolNames.ToHashSet(), allowed.ToHashSet());
        var builtIns = args[args.ToList().IndexOf("--tools") + 1].Split(',');
        Assert.DoesNotContain(builtIns, t => t.StartsWith("mcp__", StringComparison.Ordinal));
        var disallowed = args[args.ToList().IndexOf("--disallowedTools") + 1].Split(',');
        Assert.DoesNotContain("mcp__*", disallowed);

        // The hook's policy lists the same tools, so the hook allows them.
        var policy = PolicyJson.Deserialize(File.ReadAllText(files.PolicyFile));
        Assert.Superset(DiagnosticsMcp.ClaudeToolNames.ToHashSet(), policy.AllowedTools.ToHashSet());
    }

    [Fact]
    public void Without_servers_every_mcp_tool_stays_disallowed()
    {
        using var folder = new TempFolder();
        var request = Request(AgentRole.Reviewer, RoleConfig.ReviewerDefault, folder.Combine("run"), null);
        var options = new ClaudeCodeRunnerOptions { HookCommand = "hook" };

        var files = ClaudeRunFileWriter.Write(request, options);
        var args = ClaudeArgumentBuilder.Build(request, files, options);

        Assert.Equal(ClaudeRunFileWriter.EmptyMcpConfigJson, File.ReadAllText(files.McpConfigFile));
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("mcp__*", args[args.ToList().IndexOf("--disallowedTools") + 1].Split(','));
    }

    [Fact]
    public void Server_command_prefers_the_apphost()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "proj"));
        var asApphost = DiagnosticsServerCommand.Build(@"C:\tools\autopilot.exe", @"C:\tools\autopilot.dll", root, _ => true);
        Assert.Equal(@"C:\tools\autopilot.exe", asApphost.Command);
        Assert.Equal(["mcp", "diagnostics", "--project", root], asApphost.Args);

        var onDotnet = DiagnosticsServerCommand.Build(@"C:\dotnet\dotnet.exe", Path.Combine("C:\\tools", "autopilot.dll"), root, _ => true);
        Assert.EndsWith(OperatingSystem.IsWindows() ? "autopilot.exe" : "autopilot", onDotnet.Command);

        var noApphost = DiagnosticsServerCommand.Build(@"C:\dotnet\dotnet.exe", @"C:\tools\autopilot.dll", root, _ => false);
        Assert.Equal(@"C:\dotnet\dotnet.exe", noApphost.Command);
        Assert.Equal([@"C:\tools\autopilot.dll", "mcp", "diagnostics", "--project", root], noApphost.Args);
    }

    [Fact]
    public void Project_services_builds_the_server_only_for_jsonl()
    {
        using var project = Fixtures.ProjectWithLog();
        var config = ConfigLoader.Load(project.Path);
        var server = ProjectServices.DiagnosticsServerFor(project.Path, config);
        Assert.NotNull(server);
        Assert.Equal(["mcp", "diagnostics", "--project", Path.GetFullPath(project.Path)], server.Args.TakeLast(4));

        Assert.Null(ProjectServices.DiagnosticsServerFor(project.Path, new AutopilotConfig()));

        var outside = config with
        {
            Modules = config.Modules with
            {
                Diagnostics = new ModuleConfig { Type = "jsonl", Settings = new Dictionary<string, string> { ["path"] = "../other/app.jsonl" } },
            },
        };
        Assert.Throws<ProjectSetupException>(() => ProjectServices.DiagnosticsServerFor(project.Path, outside));
    }

    [Fact]
    public void Diagnostics_tools_are_added_once()
    {
        var once = DiagnosticsMcp.WithDiagnosticsTools(RoleConfig.ImplementerDefault);
        var twice = DiagnosticsMcp.WithDiagnosticsTools(once);
        Assert.Equal(once.AllowedTools, twice.AllowedTools);
        Assert.Equal(RoleConfig.ImplementerDefault.AllowedTools.Count + 4, once.AllowedTools.Count);
        Assert.Equal(
            ["mcp__diagnostics__list_recent_errors", "mcp__diagnostics__get_error_details", "mcp__diagnostics__search_logs", "mcp__diagnostics__get_endpoint_stats"],
            DiagnosticsMcp.ClaudeToolNames);
        Assert.True(DiagnosticsMcp.RoleUsesDiagnostics(AgentRole.Triage));
        Assert.True(DiagnosticsMcp.RoleUsesDiagnostics(AgentRole.Implementer));
        Assert.False(DiagnosticsMcp.RoleUsesDiagnostics(AgentRole.Reviewer));
    }

    [Fact]
    public void Hook_allows_diagnostics_tools_for_triage_but_not_for_the_reviewer()
    {
        using var folder = new TempFolder();
        var call = new ToolCall("mcp__diagnostics__list_recent_errors", JsonDocument.Parse("""{"sinceHours":24}""").RootElement);
        ToolPolicy PolicyFor(AgentRole role, RoleConfig config) => new(role, folder.Path, config.AllowedTools, [], [], [], config.MaxToolCalls, folder.Combine("count"));

        Assert.True(ToolPolicyEvaluator.Evaluate(call, PolicyFor(AgentRole.Triage, DiagnosticsMcp.WithDiagnosticsTools(RoleConfig.TriageDefault)), 1).Allowed);
        Assert.False(ToolPolicyEvaluator.Evaluate(call, PolicyFor(AgentRole.Reviewer, RoleConfig.ReviewerDefault), 1).Allowed);
        var other = call with { ToolName = "mcp__other__delete_everything" };
        Assert.False(ToolPolicyEvaluator.Evaluate(other, PolicyFor(AgentRole.Triage, DiagnosticsMcp.WithDiagnosticsTools(RoleConfig.TriageDefault)), 1).Allowed);
    }
}

public sealed class PipelineDiagnosticsTests
{
    private static readonly McpServerSpec Server = new("autopilot", ["mcp", "diagnostics", "--project", "root"]);

    private sealed class FixedFacts : IGateFactsCollector
    {
        public Task<GateInput> CollectAsync(string workItemId, string baseCommit, string headCommit, Evidence evidence, CancellationToken ct) =>
            Task.FromResult(new GateInput([], new TestFacts(true, true, 1, 1, 0, "log"), evidence));
    }

    private static RecordingRunner Runner() => new(request => request.Role switch
    {
        AgentRole.Triage => RecordingRunner.Answer(new { label = "agent-ready", reason = "Small.", acceptanceCriteria = new[] { "works" }, risk = "low" }),
        AgentRole.Implementer => Implement(request),
        _ => RecordingRunner.Answer(new { verdict = "approve", findings = Array.Empty<string>() }),
    });

    private static AgentRunResult Implement(AgentRunRequest request)
    {
        Directory.CreateDirectory(Path.Combine(request.WorkingDirectory, "src"));
        File.WriteAllText(Path.Combine(request.WorkingDirectory, "src", "Fix.cs"), "// fixed\n");
        return RecordingRunner.Answer(new { summary = "Fixed.", testsThatProveIt = new[] { "T" }, doubts = Array.Empty<string>() });
    }

    private static async Task<(TempFolder Folder, string Root)> CreateRepoAsync()
    {
        var folder = new TempFolder();
        var root = folder.Combine("repo");
        Directory.CreateDirectory(root);
        await Fixtures.RunGitAsync(root, "init", "--quiet", "-b", "main");
        File.WriteAllText(Path.Combine(root, ".gitignore"), ".autopilot/out/\n");
        File.WriteAllText(Path.Combine(root, "README.md"), "hello\n");
        await new GitClient(root).CommitAllAsync(root, "initial");
        Directory.CreateDirectory(Path.Combine(root, ".autopilot", "tasks"));
        File.WriteAllText(Path.Combine(root, ".autopilot", "tasks", "001-fix.md"), "# Fix it\nFix the bug.\n");
        return (folder, root);
    }

    private static AutopilotPipeline Pipeline(string root, string worktrees, IAgentRunner runner, McpServerSpec? server) => new(new AutopilotPipelineOptions
    {
        ProjectRoot = root,
        Config = new AutopilotConfig { Commands = new CommandsConfig { Build = "echo b", Test = "echo t" } },
        WorkItems = new LocalWorkItemSource(root),
        Runner = runner,
        Publisher = new LocalPublisher(root),
        Git = new GitClient(root),
        Facts = new FixedFacts(),
        WorktreeRoot = worktrees,
        EvaluateGate = (_, _) => new GateDecision(GateOutcome.Passed, []),
        DiagnosticsServer = server,
    });

    [Fact]
    public async Task Triage_and_implementer_get_the_server_and_tools_the_reviewer_does_not()
    {
        var (folder, root) = await CreateRepoAsync();
        using var _ = folder;
        var runner = Runner();

        await Pipeline(root, folder.Combine("wt"), runner, Server).RunAsync(null, CancellationToken.None);

        Assert.Equal([AgentRole.Triage, AgentRole.Implementer, AgentRole.Reviewer], runner.Requests.Select(r => r.Role));
        foreach (var request in runner.Requests)
        {
            var hasTools = DiagnosticsMcp.ClaudeToolNames.All(request.RoleConfig.AllowedTools.Contains);
            if (request.Role == AgentRole.Reviewer)
            {
                Assert.Null(request.McpServers);
                Assert.DoesNotContain(request.RoleConfig.AllowedTools, t => t.StartsWith("mcp__", StringComparison.Ordinal));
            }
            else
            {
                Assert.True(hasTools, $"{request.Role} is missing the diagnostics tools");
                Assert.Equal(Server, Assert.Single(request.McpServers!, s => s.Key == "diagnostics").Value);
            }
        }

        // The implementer keeps its own tools too.
        Assert.Superset(RoleConfig.ImplementerDefault.AllowedTools.ToHashSet(), runner.Requests[1].RoleConfig.AllowedTools.ToHashSet());
    }

    [Fact]
    public async Task Without_a_server_no_role_gets_mcp()
    {
        var (folder, root) = await CreateRepoAsync();
        using var _ = folder;
        var runner = Runner();

        await Pipeline(root, folder.Combine("wt"), runner, null).RunAsync(null, CancellationToken.None);

        Assert.Equal(3, runner.Requests.Count);
        Assert.All(runner.Requests, r =>
        {
            Assert.Null(r.McpServers);
            Assert.DoesNotContain(r.RoleConfig.AllowedTools, t => t.StartsWith("mcp__", StringComparison.Ordinal));
        });
    }
}
