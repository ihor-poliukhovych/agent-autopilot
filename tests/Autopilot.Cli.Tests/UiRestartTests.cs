using Autopilot.Cli.Commands;
using Autopilot.Core.Jobs;
using Microsoft.Data.Sqlite;

namespace Autopilot.Cli.Tests;

/// <summary>The UI pid file and <c>autopilot ui restart|status|install</c> with a fake killer, launcher and liveness check.</summary>
public sealed class UiRestartTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly HashSet<int> _alive = [];
    private readonly List<int> _killed = [];
    private readonly List<(string File, string[] Args)> _launched = [];
    private bool _killWorks = true;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string Workspace => _temp.Combine("ws");

    private string PidPath => UiPidFile.PathFor(Workspace);

    private sealed class Killer(UiRestartTests owner) : IProcessKiller
    {
        public void Kill(int pid)
        {
            owner._killed.Add(pid);
            if (owner._killWorks)
            {
                owner._alive.Remove(pid);
            }
        }
    }

    private sealed class Launcher(UiRestartTests owner) : IJobLauncher
    {
        public int Launch(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            owner._launched.Add((fileName, [.. arguments]));
            return 4242;
        }
    }

    private async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CliContext(TextReader.Null, output, error)
        {
            DoctorChecksTools = false,
            AutostartSystem = AutostartSystem.WindowsStartup,
            UserFolders = (_temp.Combine("home"), _temp.Combine("appdata")),
            Program = ("C:\\tools\\autopilot.exe", []),
            RunSystemCommand = _ => Task.FromResult(0),
            IsPortInUse = _ => false,
            ProcessKiller = new Killer(this),
            JobLauncher = new Launcher(this),
            IsProcessAlive = _alive.Contains,
            UiStopTimeout = TimeSpan.FromMilliseconds(200),
        };
        var exit = await CliApp.RunAsync([.. args, "--workspace", Workspace], context);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void Pid_file_round_trips_and_is_deleted()
    {
        UiPidFile.Write(PidPath, 123, "1.2.3");
        var info = UiPidFile.Read(PidPath);
        Assert.Equal(new UiProcessInfo(123, "1.2.3"), info);
        UiPidFile.Delete(PidPath);
        Assert.False(File.Exists(PidPath));
        Assert.Null(UiPidFile.Read(PidPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("12x\n1.0")]
    public void Corrupt_pid_file_reads_as_nothing_running(string content)
    {
        Directory.CreateDirectory(Workspace);
        File.WriteAllText(PidPath, content);
        Assert.Null(UiPidFile.Read(PidPath));
        Assert.Null(UiPidFile.ReadRunning(PidPath, _ => true));
    }

    [Fact]
    public void Stale_pid_is_not_running()
    {
        UiPidFile.Write(PidPath, 77, "1.0");
        Assert.Null(UiPidFile.ReadRunning(PidPath, _alive.Contains));
        _alive.Add(77);
        Assert.Equal(77, UiPidFile.ReadRunning(PidPath, _alive.Contains)?.Pid);
    }

    [Fact]
    public void Delete_keeps_a_file_written_by_another_ui()
    {
        UiPidFile.Write(PidPath, 8, "1.0");
        UiPidFile.Delete(PidPath, onlyIfPid: 9);
        Assert.True(File.Exists(PidPath));
        UiPidFile.Delete(PidPath, onlyIfPid: 8);
        Assert.False(File.Exists(PidPath));
    }

    [Fact]
    public async Task Restart_stops_the_process_removes_the_pid_file_and_starts_again()
    {
        UiPidFile.Write(PidPath, 500, "1.0");
        _alive.Add(500);
        var (exit, output, _) = await RunAsync("ui", "restart");
        Assert.Equal(0, exit);
        Assert.Equal([500], _killed);
        Assert.False(File.Exists(PidPath));
        var launch = Assert.Single(_launched);
        Assert.Contains("ui", launch.Args);
        Assert.Contains("Started the UI (pid 4242)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_without_a_pid_file_just_starts()
    {
        var (exit, output, _) = await RunAsync("ui", "restart");
        Assert.Equal(0, exit);
        Assert.Empty(_killed);
        Assert.Single(_launched);
        Assert.Contains("No UI was running", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_with_a_stale_pid_starts_and_clears_the_file()
    {
        UiPidFile.Write(PidPath, 501, "1.0");
        var (exit, output, _) = await RunAsync("ui", "restart");
        Assert.Equal(0, exit);
        Assert.Empty(_killed);
        Assert.False(File.Exists(PidPath));
        Assert.Single(_launched);
        Assert.Contains("No UI was running", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restart_fails_when_the_process_does_not_stop()
    {
        UiPidFile.Write(PidPath, 502, "1.0");
        _alive.Add(502);
        _killWorks = false;
        var (exit, _, error) = await RunAsync("ui", "restart");
        Assert.NotEqual(0, exit);
        Assert.Contains("did not stop", error, StringComparison.Ordinal);
        Assert.Empty(_launched);
    }

    [Fact]
    public async Task Status_shows_version_and_pid_or_not_running()
    {
        Assert.Contains("not running", (await RunAsync("ui", "status")).Out, StringComparison.Ordinal);
        UiPidFile.Write(PidPath, 600, "9.8.7");
        Assert.Contains("not running", (await RunAsync("ui", "status")).Out, StringComparison.Ordinal);
        _alive.Add(600);
        var text = (await RunAsync("ui", "status")).Out;
        Assert.Contains("version 9.8.7", text, StringComparison.Ordinal);
        Assert.Contains("pid 600", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_twice_leaves_one_autostart_entry()
    {
        await RunAsync("ui", "install");
        await RunAsync("ui", "install");
        var startup = Path.Combine(_temp.Combine("appdata"), "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        Assert.Single(Directory.GetFiles(startup));
    }
}
