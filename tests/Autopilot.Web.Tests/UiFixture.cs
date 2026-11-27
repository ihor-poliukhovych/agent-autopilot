using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Jobs;
using Autopilot.Core.Local;
using Autopilot.Core.Scheduling;
using Autopilot.Core.Tasks;
using Autopilot.Core.Workspace;
using Autopilot.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace Autopilot.Web.Tests;

public sealed class FakeChecker : ISetupChecker
{
    public List<SetupCheckRequest> Requests { get; } = [];

    /// <summary>Lines returned for every request; tests replace it to make a check fail.</summary>
    public Func<SetupCheckRequest, IReadOnlyList<CheckLine>> Lines { get; set; } = request =>
    [
        new CheckLine(CheckLine.Ok, "dotnet", "10.0"),
        new CheckLine(CheckLine.Ok, "git", "2.50"),
        new CheckLine(CheckLine.Ok, "claude", "2.0"),
        new CheckLine(CheckLine.Ok, "git repository", request.RepositoryFolder),
        new CheckLine(CheckLine.Ok, "base branch", "main (abc123)"),
        new CheckLine(CheckLine.Ok, "config", "ok"),
        new CheckLine(CheckLine.Ok, "writable paths", "src/**"),
        new CheckLine(request.SkipBuild ? CheckLine.Skip : CheckLine.Ok, "build command", "dotnet build"),
        new CheckLine(request.SkipBuild ? CheckLine.Skip : CheckLine.Ok, "test command", "dotnet test"),
        new CheckLine(CheckLine.Ok, "work items", "local"),
    ];

    public Task<IReadOnlyList<CheckLine>> RunAsync(SetupCheckRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Lines(request));
    }

    public Task<AutopilotConfig> DetectDefaultsAsync(string repositoryFolder, string? baseBranch, CancellationToken ct) =>
        Task.FromResult(new AutopilotConfig { BaseBranch = baseBranch ?? "main", Commands = new CommandsConfig { Build = "make", Test = "make test" } });
}

public sealed class FakeSources : IWorkItemSourceFactory
{
    public IWorkItemSource Create(string repositoryFolder, AutopilotConfig config, string dataFolder, LocalTaskService? tasks) =>
        tasks is null
            ? new LocalWorkItemSource(repositoryFolder, dataFolder)
            : new LocalTaskWorkItemSource(tasks, new TriageStore(repositoryFolder, dataFolder));
}

public sealed class FakeAdo : IAzureDevOpsProbe
{
    public ModuleConfig? Seen { get; private set; }

    public int SeenSkip { get; private set; }

    public bool? SeenDryRun { get; private set; }

    public Task<TrackerImportReport> ImportAsync(ModuleConfig module, string repositoryFolder, LocalTaskService tasks, int? limit, bool dryRun, CancellationToken ct)
    {
        Seen = module;
        SeenDryRun = dryRun;
        return Task.FromResult(new TrackerImportReport(3, 1, 2, dryRun));
    }

    public Task<WorkItemPreviewPage> PreviewAsync(ModuleConfig module, string repositoryFolder, int skip, int take, CancellationToken ct)
    {
        Seen = module;
        SeenSkip = skip;
        return Task.FromResult(new WorkItemPreviewPage([new WorkItemPreview("101", "Fix the login page", "Bug", "Active", null, null)], 11, skip, take, HasMore: true, Truncated: false));
    }
}

public sealed class FakeLauncher : IJobLauncher
{
    public List<(string File, string[] Arguments, string Folder)> Launched { get; } = [];

    public int NextPid { get; set; } = 4242;

    /// <summary>Thrown by the next launches when set, to test a process that cannot start.</summary>
    public Exception? Failure { get; set; }

    public int Launch(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Launched.Add((fileName, [.. arguments], workingDirectory));
        return NextPid++;
    }
}

public sealed class FakeKiller : IProcessKiller
{
    public List<int> Killed { get; } = [];

    public void Kill(int pid) => Killed.Add(pid);
}

public sealed class FakeScheduler : IScheduler
{
    public Dictionary<string, ScheduledJob> Jobs { get; } = [];

    public string Description => "a test scheduler";

    public Task InstallAsync(ScheduledJob job, CancellationToken ct)
    {
        Jobs[job.Name] = job;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string name, CancellationToken ct) => Task.FromResult(Jobs.Remove(name));

    public Task<bool> ExistsAsync(string name, CancellationToken ct) => Task.FromResult(Jobs.ContainsKey(name));
}

/// <summary>An in-memory host over a temp workspace. No key by default, like the real UI on this computer; with a key the client is already signed in.</summary>
public sealed class UiFixture : IAsyncDisposable
{
    public const string Key = "test-key";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "autopilot-ui-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplication _app;

    public UiFixture(bool signedIn = true, bool withAdo = true, bool? requireKey = false, TimeSpan? cacheTtl = null, Workspace? workspace = null, System.Net.IPAddress? bind = null, IWorkItemSourceFactory? sources = null, Action<string>? requestLog = null)
    {
        Directory.CreateDirectory(_temp);
        RepoFolder = Path.Combine(_temp, "repo");
        Directory.CreateDirectory(Path.Combine(RepoFolder, ".git")); // the registry only needs the entry to exist
        Workspace = workspace ?? new Workspace(Path.Combine(_temp, "ws"));
        Options = new WebUiOptions
        {
            Workspace = Workspace,
            Checker = Checker,
            Sources = sources ?? new FakeSources(),
            AzureDevOps = withAdo ? Ado : null,
            Program = new ProgramInfo("autopilot", []),
            Launcher = Launcher,
            Killer = Killer,
            Scheduler = Scheduler,
            IsProcessAlive = pid => AliveProcesses.Contains(pid),
            Environment = name => name == "AUTOPILOT_ADO_PAT" ? "x" : null,
            SessionKey = Key,
            RequireKey = requireKey,
            BindAddress = bind ?? System.Net.IPAddress.Loopback,
            TrackerCacheTtl = cacheTtl ?? TimeSpan.FromSeconds(30),
            RequestLog = requestLog,
        };
        _app = WebUi.Build(Options, builder => builder.WebHost.UseTestServer());
        _app.StartAsync().GetAwaiter().GetResult();
        Server = _app.GetTestServer();
        Client = Server.CreateClient();
        if (signedIn && requireKey == true)
        {
            SignIn(Client);
        }
    }

    public string RepoFolder { get; }

    public Workspace Workspace { get; }

    public WebUiOptions Options { get; }

    public FakeChecker Checker { get; } = new();

    public FakeAdo Ado { get; } = new();

    public FakeLauncher Launcher { get; } = new();

    public FakeKiller Killer { get; } = new();

    public FakeScheduler Scheduler { get; } = new();

    public HashSet<int> AliveProcesses { get; } = [];

    public TestServer Server { get; }

    public HttpClient Client { get; }

    public static void SignIn(HttpClient client)
    {
        var response = client.GetAsync($"/?key={Key}").GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
    }

    /// <summary>A request the way the page makes it: custom header and a matching Origin.</summary>
    public HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(SessionGuard.RequestHeader, "1");
        request.Headers.Add("Origin", "http://localhost");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var response = await Client.SendAsync(Request(method, path, body));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    public Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });

    /// <summary>A valid draft pointing at the temp repository folder.</summary>
    public object Draft(string key = "demo", string taskSource = "local", object? ado = null) => new
    {
        key,
        displayName = "Demo",
        repositoryFolder = RepoFolder,
        baseBranch = "main",
        buildCommand = "make",
        testCommand = "make test",
        writablePaths = new[] { "src/**" },
        riskPaths = new[] { "**/*.csproj" },
        taskSource,
        ado = ado ?? new { },
    };

    public async Task<JsonElement> CreateProjectAsync(string key = "demo")
    {
        var (status, body) = await PostAsync("/api/projects", Draft(key));
        Assert.True(status == HttpStatusCode.Created, body.ToString());
        return body;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Windows may hold the SQLite file a moment longer; the temp folder is cleaned later.
        }
    }
}
