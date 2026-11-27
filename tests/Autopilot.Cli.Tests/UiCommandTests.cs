using Autopilot.Cli.Commands;
using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace Autopilot.Cli.Tests;

/// <summary><c>autopilot ui install|uninstall|status|link|require-key</c> against a temp workspace and temp user folders.</summary>
public sealed class UiCommandTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly List<string[]> _system = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private async Task<(int Exit, string Out, string Err)> RunAsync(AutostartSystem system, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var context = new CliContext(TextReader.Null, output, error)
        {
            DoctorChecksTools = false,
            AutostartSystem = system,
            UserFolders = (_temp.Combine("home"), _temp.Combine("appdata")),
            Program = ("C:\\tools\\autopilot.exe", []),
            RunSystemCommand = command =>
            {
                _system.Add(command);
                return Task.FromResult(0);
            },
            IsPortInUse = _ => false,
        };
        var exit = await CliApp.RunAsync([.. args, "--workspace", _temp.Combine("ws")], context);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Install_writes_a_hidden_startup_script_on_windows_and_uninstall_removes_it()
    {
        var (exit, output, _) = await RunAsync(AutostartSystem.WindowsStartup, "ui", "install");
        Assert.Equal(0, exit);
        var file = Path.Combine(_temp.Combine("appdata"), "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "Autopilot UI.vbs");
        Assert.True(File.Exists(file), output);
        var text = File.ReadAllText(file);
        Assert.Contains("ui", text, StringComparison.Ordinal);
        Assert.Contains("--no-open", text, StringComparison.Ordinal);
        Assert.Contains(", 0, False", text, StringComparison.Ordinal);
        Assert.Contains("yes", (await RunAsync(AutostartSystem.WindowsStartup, "ui", "status")).Out, StringComparison.Ordinal);

        Assert.Equal(0, (await RunAsync(AutostartSystem.WindowsStartup, "ui", "uninstall")).Exit);
        Assert.False(File.Exists(file));
        Assert.Contains("Starts at sign-in: no", (await RunAsync(AutostartSystem.WindowsStartup, "ui", "status")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_writes_a_systemd_user_unit_that_restarts_on_failure_and_enables_it()
    {
        Assert.Equal(0, (await RunAsync(AutostartSystem.SystemdUser, "ui", "install")).Exit);
        var unit = File.ReadAllText(Path.Combine(_temp.Combine("home"), ".config", "systemd", "user", "autopilot-ui.service"));
        Assert.Contains("Restart=on-failure", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=", unit, StringComparison.Ordinal);
        Assert.Contains(_system, c => c.SequenceEqual(["systemctl", "--user", "enable", "--now", "autopilot-ui.service"]));

        _system.Clear();
        Assert.Equal(0, (await RunAsync(AutostartSystem.SystemdUser, "ui", "uninstall")).Exit);
        Assert.Contains(_system, c => c.Contains("disable"));
    }

    [Fact]
    public async Task Install_writes_a_launchd_agent_that_is_kept_alive()
    {
        Assert.Equal(0, (await RunAsync(AutostartSystem.Launchd, "ui", "install")).Exit);
        var plist = File.ReadAllText(Path.Combine(_temp.Combine("home"), "Library", "LaunchAgents", "dev.autopilot.ui.plist"));
        Assert.Contains("<key>KeepAlive</key><true/>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>--no-open</string>", plist, StringComparison.Ordinal);
        Assert.Contains(_system, c => c[0] == "launchctl" && c.Contains("load"));
    }

    [Fact]
    public async Task Uninstall_without_install_says_so_and_succeeds()
    {
        var (exit, output, _) = await RunAsync(AutostartSystem.WindowsStartup, "ui", "uninstall");
        Assert.Equal(0, exit);
        Assert.Contains("Nothing to remove", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_keeps_the_port_that_was_chosen_once_and_status_shows_it()
    {
        await RunAsync(AutostartSystem.WindowsStartup, "ui", "install");
        var port = new WorkspaceSettings(new Workspace(_temp.Combine("ws"))).GetInt(WorkspaceSettings.UiPort);
        Assert.InRange(port!.Value, 5100, 5999);
        await RunAsync(AutostartSystem.WindowsStartup, "ui", "install");
        Assert.Equal(port, new WorkspaceSettings(new Workspace(_temp.Combine("ws"))).GetInt(WorkspaceSettings.UiPort));
        Assert.Contains($"http://localhost:{port}/", (await RunAsync(AutostartSystem.WindowsStartup, "ui", "status")).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Link_says_no_sign_in_is_needed_until_the_key_is_switched_on()
    {
        var plain = await RunAsync(AutostartSystem.WindowsStartup, "ui", "link");
        Assert.Contains("No sign-in is needed", plain.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("key=", plain.Out, StringComparison.Ordinal);

        Assert.Equal(0, (await RunAsync(AutostartSystem.WindowsStartup, "ui", "require-key", "on")).Exit);
        var first = (await RunAsync(AutostartSystem.WindowsStartup, "ui", "link")).Out.Trim();
        Assert.Contains("?key=", first, StringComparison.Ordinal);
        Assert.Equal(first, (await RunAsync(AutostartSystem.WindowsStartup, "ui", "link")).Out.Trim());

        var rotated = (await RunAsync(AutostartSystem.WindowsStartup, "ui", "link", "--rotate")).Out.Split('\n')[0].Trim();
        Assert.NotEqual(first, rotated);
    }

    [Fact]
    public async Task Require_key_refuses_a_word_that_is_not_on_or_off()
    {
        Assert.NotEqual(0, (await RunAsync(AutostartSystem.WindowsStartup, "ui", "require-key", "maybe")).Exit);
    }
}
