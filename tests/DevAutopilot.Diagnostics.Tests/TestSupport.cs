using System.Text.Json;
using DevAutopilot.Core;
using DevAutopilot.Core.Process;

namespace DevAutopilot.Diagnostics.Tests;

/// <summary>A temporary folder deleted on dispose (git objects are read-only, so attributes are reset).</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "autopilot-tests", "diag-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relativePath, string content)
    {
        var full = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(Path))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }
}

public static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Text(string name) => File.ReadAllText(Path(name));

    public const string DiagnosticsConfig = """
        {
          "commands": { "build": "echo build", "test": "echo test" },
          "modules": { "diagnostics": { "type": "jsonl", "settings": { "path": "telemetry/app.jsonl" } } }
        }
        """;

    /// <summary>A project folder with the fixture log at telemetry/app.jsonl and diagnostics turned on.</summary>
    public static TempFolder ProjectWithLog(string? config = null)
    {
        var folder = new TempFolder();
        folder.Write("telemetry/app.jsonl", Text("app.jsonl"));
        folder.Write(".autopilot/autopilot.json", config ?? DiagnosticsConfig);
        return folder;
    }

    public static async Task RunGitAsync(string folder, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, folder);
        Assert.True(result.Succeeded, result.Output);
    }
}

/// <summary>An agent runner that records requests and answers from a script.</summary>
public sealed class RecordingRunner(Func<AgentRunRequest, AgentRunResult> respond) : IAgentRunner
{
    private readonly List<AgentRunRequest> _requests = [];

    public IReadOnlyList<AgentRunRequest> Requests => _requests;

    public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        _requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static AgentRunResult Answer(object structured) => new(
        true, "done", JsonSerializer.SerializeToElement(structured), 3, TokenUsage.Zero, TimeSpan.FromSeconds(1), "test-model", null);
}
