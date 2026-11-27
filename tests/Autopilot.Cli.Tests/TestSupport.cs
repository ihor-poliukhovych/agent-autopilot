using System.Text.Json;
using Autopilot.Core;
using Autopilot.Core.Git;
using Autopilot.Core.Process;

namespace Autopilot.Cli.Tests;

/// <summary>A temporary folder that is deleted (with read-only git objects) on dispose.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "autopilot-tests", Guid.NewGuid().ToString("N")[..12]);
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

    public void Dispose() => DeleteDirectory(Path);

    public static void DeleteDirectory(string path)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }
}

/// <summary>A temporary git repository with a main branch and one commit.</summary>
public sealed class TestRepo : IDisposable
{
    private readonly TempFolder _folder = new();

    private TestRepo()
    {
        Root = _folder.Combine("repo");
        WorktreeRoot = _folder.Combine("worktrees");
        Directory.CreateDirectory(Root);
        Git = new GitClient(Root);
    }

    public string Root { get; }

    /// <summary>Folder for worktrees, outside the repository, deleted with it.</summary>
    public string WorktreeRoot { get; }

    public GitClient Git { get; }

    public static async Task<TestRepo> CreateAsync(IReadOnlyDictionary<string, string>? files = null)
    {
        var repo = new TestRepo();
        var init = await ProcessRunner.RunAsync("git", ["init", "--quiet", "-b", "main"], repo.Root);
        Assert.True(init.Succeeded, init.Output);
        await repo.GitAsync("config", "core.longpaths", "true");
        foreach (var (path, content) in files ?? new Dictionary<string, string> { ["README.md"] = "hello\n" })
        {
            repo.Write(path, content);
        }

        await repo.Git.CommitAllAsync(repo.Root, "initial");
        return repo;
    }

    public string Write(string relativePath, string content)
    {
        var full = Path.Combine([Root, .. relativePath.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public async Task<string> GitAsync(params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, Root);
        Assert.True(result.Succeeded, result.Output);
        return result.StandardOutput;
    }

    public void Dispose()
    {
        // Worktrees live inside the temp folder, so deleting it removes them; prune is not needed after that.
        _folder.Dispose();
    }
}

/// <summary>An agent runner that answers from a script instead of starting Claude Code.</summary>
public sealed class FakeAgentRunner(Func<AgentRunRequest, int, AgentRunResult> respond) : IAgentRunner
{
    private readonly List<AgentRunRequest> _requests = [];

    public IReadOnlyList<AgentRunRequest> Requests => _requests;

    public Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct)
    {
        int callsForRole;
        lock (_requests)
        {
            _requests.Add(request);
            callsForRole = _requests.Count(r => r.Role == request.Role);
        }

        return Task.FromResult(respond(request, callsForRole));
    }

    public static AgentRunResult Answer(object structured) => new(
        true,
        "done",
        JsonSerializer.SerializeToElement(structured),
        3,
        new TokenUsage(100, 50, 10, 5),
        TimeSpan.FromSeconds(2),
        "test-model",
        null);
}

public static class Shell
{
    /// <summary>A shell command that prints a file's text.</summary>
    public static string PrintFile(string relativePath) =>
        OperatingSystem.IsWindows() ? $"type {relativePath.Replace('/', '\\')}" : $"cat {relativePath}";
}
