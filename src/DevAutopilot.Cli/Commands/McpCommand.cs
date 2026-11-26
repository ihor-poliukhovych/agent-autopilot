using System.CommandLine;
using DevAutopilot.Core;
using DevAutopilot.Core.Config;
using DevAutopilot.Diagnostics;

namespace DevAutopilot.Cli.Commands;

/// <summary>
/// <c>autopilot mcp diagnostics --project &lt;path&gt;</c>: the read-only diagnostics MCP server over stdio.
/// Claude Code starts it from the run's mcp.json. stdout carries only the protocol; every message for a
/// person goes to stderr.
/// </summary>
public static class McpCommand
{
    public static Command Create(ProjectOptions projectOption, CliContext context)
    {
        var diagnostics = new Command(
            "diagnostics",
            "Serve the project's diagnostics source (modules.diagnostics) as a read-only MCP server over stdio.");
        diagnostics.SetAction(async (parse, ct) =>
        {
            var target = projectOption.Resolve(parse);
            var project = target.RepoRoot;
            JsonlDiagnosticsSource? source;
            try
            {
                var config = ConfigLoader.Load(project, target.DataFolder);
                source = DiagnosticsSourceFactory.Create(project, config.Modules.Diagnostics);
            }
            catch (Exception ex) when (ex is ConfigException or DiagnosticsConfigException)
            {
                await context.Error.WriteLineAsync($"The diagnostics server cannot start: {ex.Message}").ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            if (source is null)
            {
                await context.Error.WriteLineAsync(
                    "Diagnostics is off (modules.diagnostics.type is \"none\"). Set it to \"jsonl\" with settings.path in .autopilot/autopilot.json.").ConfigureAwait(false);
                return ExitCodes.Failure;
            }

            await DiagnosticsServer.RunStdioAsync(source, ct).ConfigureAwait(false);
            return ExitCodes.Ok;
        });

        return new Command("mcp", "MCP servers that Claude Code starts during an agent run.") { diagnostics };
    }
}

public static class DiagnosticsServerCommand
{
    /// <summary>
    /// The program and arguments Claude Code runs to start <c>autopilot mcp diagnostics</c> for the main
    /// project (not a worktree, so it reads the project's log and settings). Like the hook, the apphost
    /// (<c>autopilot.exe</c>) is preferred when the tool runs on the dotnet host.
    /// </summary>
    public static McpServerSpec Build(string? processPath, string? entryAssemblyPath, string projectRoot, Func<string, bool>? fileExists = null, string? dataFolder = null)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            throw new InvalidOperationException("Cannot tell which program is running, so the diagnostics server command cannot be built.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        fileExists ??= File.Exists;
        // A workspace project is named by key and workspace (its folder is <workspace>/projects/<key>).
        string[] serverArgs = dataFolder is null
            ? ["mcp", "diagnostics", "--project", Path.GetFullPath(projectRoot)]
            : ["mcp", "diagnostics", "--project", Path.GetFileName(Path.GetFullPath(dataFolder)), "--workspace", Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(dataFolder)))!];
        var host = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(host, "dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entryAssemblyPath))
        {
            var appHost = HookCommandLine.AppHostPath(entryAssemblyPath, OperatingSystem.IsWindows());
            if (appHost is not null && fileExists(appHost))
            {
                return new McpServerSpec(appHost, serverArgs);
            }

            return new McpServerSpec(processPath, [entryAssemblyPath, .. serverArgs]);
        }

        return new McpServerSpec(processPath, serverArgs);
    }
}
