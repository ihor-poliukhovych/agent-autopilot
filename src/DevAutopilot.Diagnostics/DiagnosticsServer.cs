using System.Reflection;
using DevAutopilot.Core;
using DevAutopilot.Core.Diagnostics;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevAutopilot.Diagnostics;

/// <summary>
/// The read-only diagnostics MCP server (official ModelContextProtocol C# SDK, low-level API without a
/// host, so nothing but the protocol is ever written to stdout).
/// </summary>
public static class DiagnosticsServer
{
    public const string Instructions =
        "Read-only diagnostics from the application's log. Start with list_recent_errors or get_endpoint_stats, "
        + "then use get_error_details or search_logs for one problem. Nothing here can change the application.";

    /// <summary>Server options with the four tools over <paramref name="source"/>.</summary>
    public static McpServerOptions CreateOptions(IDiagnosticsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tools = new DiagnosticsTools(source);
        var collection = new McpServerPrimitiveCollection<McpServerTool>
        {
            Tool(DiagnosticsMcp.ListRecentErrors, "List recent errors",
                (Func<int?, int?, CancellationToken, Task<string>>)tools.ListRecentErrorsAsync),
            Tool(DiagnosticsMcp.GetErrorDetails, "Get error details",
                (Func<string, string?, CancellationToken, Task<string>>)tools.GetErrorDetailsAsync),
            Tool(DiagnosticsMcp.SearchLogs, "Search logs",
                (Func<string, string?, int?, CancellationToken, Task<string>>)tools.SearchLogsAsync),
            Tool(DiagnosticsMcp.GetEndpointStats, "Get endpoint stats",
                (Func<string?, CancellationToken, Task<string>>)tools.GetEndpointStatsAsync),
        };

        return new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "autopilot-diagnostics",
                Version = typeof(DiagnosticsServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
            },
            ServerInstructions = Instructions,
            ToolCollection = collection,
        };
    }

    /// <summary>Serves the tools over this process's stdin and stdout until the client disconnects.</summary>
    public static async Task RunStdioAsync(IDiagnosticsSource source, CancellationToken ct)
    {
        var options = CreateOptions(source);
        await using var transport = new StdioServerTransport(options);
        await RunAsync(transport, options, ct).ConfigureAwait(false);
    }

    /// <summary>Serves the tools over the given streams (used by tests).</summary>
    public static async Task RunStreamsAsync(IDiagnosticsSource source, Stream input, Stream output, CancellationToken ct)
    {
        var options = CreateOptions(source);
        await using var transport = new StreamServerTransport(input, output, options.ServerInfo?.Name);
        await RunAsync(transport, options, ct).ConfigureAwait(false);
    }

    private static async Task RunAsync(ITransport transport, McpServerOptions options, CancellationToken ct)
    {
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(ct).ConfigureAwait(false);
    }

    private static McpServerTool Tool(string name, string title, Delegate method) =>
        McpServerTool.Create(method, new McpServerToolCreateOptions
        {
            Name = name,
            Title = title,
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
        });
}
