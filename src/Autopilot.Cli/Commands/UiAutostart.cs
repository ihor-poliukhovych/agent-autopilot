using System.Diagnostics;
using System.Security;

namespace Autopilot.Cli.Commands;

/// <summary>Which system starts the UI at sign-in.</summary>
public enum AutostartSystem
{
    /// <summary>A script in the Startup folder of the user (no administrator rights).</summary>
    WindowsStartup,

    /// <summary>A systemd user unit, restarted on failure.</summary>
    SystemdUser,

    /// <summary>A launchd agent, kept alive.</summary>
    Launchd,
}

/// <summary>The file to write and the commands that switch it on and off.</summary>
public sealed record AutostartPlan(AutostartSystem System, string FilePath, string Content, IReadOnlyList<string[]> Activate, IReadOnlyList<string[]> Deactivate);

/// <summary>Per-user autostart of <c>autopilot ui --no-open</c>. Nothing here needs administrator rights.</summary>
public static class UiAutostart
{
    public const string Name = "autopilot-ui";

    public static AutostartSystem Current() =>
        OperatingSystem.IsWindows() ? AutostartSystem.WindowsStartup
        : OperatingSystem.IsMacOS() ? AutostartSystem.Launchd
        : AutostartSystem.SystemdUser;

    /// <param name="home">The user's home folder.</param>
    /// <param name="appData">The roaming application data folder (Windows only).</param>
    /// <param name="program">The program and its leading arguments, as for a scheduled run.</param>
    public static AutostartPlan Plan(AutostartSystem system, string home, string appData, (string File, IReadOnlyList<string> Arguments) program, string workspaceRoot)
    {
        var command = new List<string>([program.File, .. program.Arguments, "ui", "--no-open", "--workspace", workspaceRoot]);
        switch (system)
        {
            case AutostartSystem.WindowsStartup:
                {
                    var path = Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "Autopilot UI.vbs");
                    // A script, not a .cmd, so no console window stays open. Quotes are doubled inside a VBScript string.
                    var line = string.Join(' ', command.Select(c => "\"\"" + c.Replace("\"", "\"\"\"\"", StringComparison.Ordinal) + "\"\""));
                    var content = "' Starts the Autopilot web UI when you sign in. Remove with: autopilot ui uninstall\r\n"
                        + $"CreateObject(\"WScript.Shell\").Run \"{line}\", 0, False\r\n";
                    return new AutostartPlan(system, path, content, [], []);
                }

            case AutostartSystem.SystemdUser:
                {
                    var path = Path.Combine(home, ".config", "systemd", "user", Name + ".service");
                    var content = $"""
                        [Unit]
                        Description=Autopilot web UI

                        [Service]
                        ExecStart={string.Join(' ', command.Select(SystemdQuote))}
                        Restart=on-failure
                        RestartSec=5

                        [Install]
                        WantedBy=default.target

                        """;
                    return new AutostartPlan(system, path, content,
                        [["systemctl", "--user", "daemon-reload"], ["systemctl", "--user", "enable", "--now", Name + ".service"]],
                        [["systemctl", "--user", "disable", "--now", Name + ".service"]]);
                }

            default:
                {
                    var path = Path.Combine(home, "Library", "LaunchAgents", "dev.autopilot.ui.plist");
                    var log = Path.Combine(workspaceRoot, "ui.log");
                    var arguments = string.Join(Environment.NewLine, command.Select(c => $"    <string>{SecurityElement.Escape(c)}</string>"));
                    var content = $"""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                        <plist version="1.0">
                        <dict>
                          <key>Label</key><string>dev.autopilot.ui</string>
                          <key>ProgramArguments</key>
                          <array>
                        {arguments}
                          </array>
                          <key>RunAtLoad</key><true/>
                          <key>KeepAlive</key><true/>
                          <key>StandardOutPath</key><string>{SecurityElement.Escape(log)}</string>
                          <key>StandardErrorPath</key><string>{SecurityElement.Escape(log)}</string>
                        </dict>
                        </plist>

                        """;
                    return new AutostartPlan(system, path, content, [["launchctl", "load", "-w", path]], [["launchctl", "unload", path]]);
                }
        }
    }

    private static string SystemdQuote(string part) =>
        part.Length > 0 && part.All(c => !char.IsWhiteSpace(c) && c != '"' && c != '\\') ? part : "\"" + part.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Runs one command and returns its exit code; -1 when it cannot start.</summary>
    public static async Task<int> RunAsync(string[] command)
    {
        try
        {
            var info = new ProcessStartInfo(command[0]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in command.Skip(1))
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info)!;
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
