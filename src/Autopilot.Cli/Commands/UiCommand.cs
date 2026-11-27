using System.CommandLine;
using Autopilot.Core.Workspace;
using Autopilot.Web;

namespace Autopilot.Cli.Commands;

/// <summary>
/// <c>autopilot ui</c>: the local web UI. It listens on 127.0.0.1, needs no sign-in, and keeps the same port on
/// every start. <c>ui install</c> starts it at sign-in; <c>ui link</c> and <c>ui require-key</c> are for shared machines.
/// </summary>
public static class UiCommand
{
    public static Command Create(ProjectOptions projectOption, CliContext context)
    {
        var port = new Option<int?>("--port") { Description = "Port. Default: the one saved for this machine (picked once from 5100-5999). 0 picks a free one for this run." };
        var noOpen = new Option<bool>("--no-open") { Description = "Do not open the browser." };
        var bind = new Option<string>("--bind") { Description = "Address to listen on. Default 127.0.0.1. Anything else needs the key (it is switched on automatically)." };
        var quiet = new Option<bool>("--quiet") { Description = "Do not print a line for every request." };
        var command = new Command("ui", "Start the local web UI at http://localhost:<port>. No sign-in is needed on this computer.") { port, noOpen, bind, quiet };
        command.SetAction(async (parse, ct) =>
        {
            var number = parse.GetValue(port);
            if (number is < 0 or > 65535)
            {
                throw new UsageException("--port must be between 0 and 65535.");
            }

            var address = System.Net.IPAddress.Loopback;
            if (parse.GetValue(bind) is { Length: > 0 } given && !System.Net.IPAddress.TryParse(given, out address))
            {
                throw new UsageException($"--bind must be an IP address, not \"{given}\".");
            }

            var workspace = projectOption.OpenWorkspace(parse);
            var settings = new WorkspaceSettings(workspace);
            var chosen = UiAccess.ResolvePort(settings, number, address);
            var backend = new UiBackend(context);
            var (file, arguments) = ProgramLocation.Current(context);
            var verbose = !parse.GetValue(quiet);
            var options = new WebUiOptions
            {
                Workspace = workspace,
                Port = chosen,
                BindAddress = address,
                Checker = backend,
                Sources = backend,
                AzureDevOps = backend,
                Program = new ProgramInfo(file, arguments),
                Launcher = context.JobLauncher ?? new Autopilot.Core.Jobs.DetachedProcessLauncher(),
                Killer = context.ProcessKiller ?? new Autopilot.Core.Jobs.ProcessTreeKiller(),
                Scheduler = context.Scheduler,
                IsProcessAlive = context.IsProcessAlive,
                RequestLog = verbose ? line => context.Out.WriteLine(line) : null,
            };
            var openBrowser = !parse.GetValue(noOpen);
            await WebUi.RunAsync(options, started =>
            {
                context.Out.WriteLine($"Autopilot UI is running at {started.Address}");
                if (started.SignInAddress is { } link)
                {
                    context.Out.WriteLine($"A key is required here. Sign in once per browser with: {link}");
                }

                context.Out.WriteLine($"Workspace: {options.Workspace.Root}. Press Ctrl+C to stop.");
                if (openBrowser)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(started.SignInAddress ?? started.Address) { UseShellExecute = true });
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        context.Out.WriteLine("The browser could not be opened; open the address yourself.");
                    }
                }
            }, ct).ConfigureAwait(false);
            return ExitCodes.Ok;
        });

        command.Subcommands.Add(CreateInstall(projectOption, context));
        command.Subcommands.Add(CreateUninstall(projectOption, context));
        command.Subcommands.Add(CreateStatus(projectOption, context));
        command.Subcommands.Add(CreateLink(projectOption, context));
        command.Subcommands.Add(CreateRequireKey(projectOption, context));
        return command;
    }

    private static AutostartPlan PlanFor(CliContext context, Workspace workspace)
    {
        var folders = context.UserFolders ?? (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        return UiAutostart.Plan(context.AutostartSystem ?? UiAutostart.Current(), folders.Home, folders.AppData, ProgramLocation.Current(context), workspace.Root);
    }

    private static Command CreateInstall(ProjectOptions projectOption, CliContext context)
    {
        var command = new Command("install", "Start the UI automatically when you sign in to this computer (no administrator rights).");
        command.SetAction(async (parse, ct) =>
        {
            var workspace = projectOption.OpenWorkspace(parse);
            var plan = PlanFor(context, workspace);
            var port = UiAccess.ResolvePort(new WorkspaceSettings(workspace), null);
            Directory.CreateDirectory(Path.GetDirectoryName(plan.FilePath)!);
            await File.WriteAllTextAsync(plan.FilePath, plan.Content, ct).ConfigureAwait(false);
            var run = context.RunSystemCommand ?? UiAutostart.RunAsync;
            foreach (var step in plan.Activate)
            {
                if (await run(step).ConfigureAwait(false) != 0)
                {
                    await context.Error.WriteLineAsync($"The file was written, but '{string.Join(' ', step)}' failed. Run it yourself, or sign out and in again.").ConfigureAwait(false);
                }
            }

            context.Out.WriteLine($"Installed: {plan.FilePath}");
            context.Out.WriteLine($"The UI starts when you sign in, at http://localhost:{port}/ . Start it now with: autopilot ui");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateUninstall(ProjectOptions projectOption, CliContext context)
    {
        var command = new Command("uninstall", "Remove the automatic start of the UI.");
        command.SetAction(async (parse, ct) =>
        {
            var plan = PlanFor(context, projectOption.OpenWorkspace(parse));
            var run = context.RunSystemCommand ?? UiAutostart.RunAsync;
            if (!File.Exists(plan.FilePath))
            {
                context.Out.WriteLine("The UI was not installed for autostart. Nothing to remove.");
                return ExitCodes.Ok;
            }

            foreach (var step in plan.Deactivate)
            {
                await run(step).ConfigureAwait(false);
            }

            File.Delete(plan.FilePath);
            context.Out.WriteLine($"Removed: {plan.FilePath}");
            context.Out.WriteLine("A UI that is running now keeps running until you close it (Ctrl+C) or sign out.");
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command CreateStatus(ProjectOptions projectOption, CliContext context)
    {
        var command = new Command("status", "Show where the UI runs, whether it starts at sign-in, and whether a key is needed.");
        command.SetAction((parse, ct) =>
        {
            var workspace = projectOption.OpenWorkspace(parse);
            var settings = new WorkspaceSettings(workspace);
            var plan = PlanFor(context, workspace);
            var port = settings.GetInt(WorkspaceSettings.UiPort);
            var inUse = port is { } p && (context.IsPortInUse?.Invoke(p) ?? !UiAccess.IsFree(System.Net.IPAddress.Loopback, p));
            context.Out.WriteLine(port is null
                ? "Address: not chosen yet. It is picked once from 5100-5999 the first time you run 'autopilot ui'."
                : $"Address: http://localhost:{port}/ ({(inUse ? "something is listening there now" : "not running now")})");
            context.Out.WriteLine($"Starts at sign-in: {(File.Exists(plan.FilePath) ? "yes" : "no")} ({plan.FilePath})");
            context.Out.WriteLine($"Key required: {(settings.GetBool(WorkspaceSettings.UiRequireKey) ? "yes (autopilot ui link prints the sign-in link)" : "no")}");
            context.Out.WriteLine($"Workspace: {workspace.Root}");
            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }

    private static Command CreateLink(ProjectOptions projectOption, CliContext context)
    {
        var rotate = new Option<bool>("--rotate") { Description = "Make a new key. Every browser signed in with the old one is signed out." };
        var command = new Command("link", "Print the sign-in link for this machine (only when a key is required).") { rotate };
        command.SetAction((parse, ct) =>
        {
            var settings = new WorkspaceSettings(projectOption.OpenWorkspace(parse));
            var port = UiAccess.ResolvePort(settings, null);
            if (!settings.GetBool(WorkspaceSettings.UiRequireKey))
            {
                context.Out.WriteLine($"No sign-in is needed: open {UiAccess.Address(port)}");
                context.Out.WriteLine("To ask for a key on a shared machine: autopilot ui require-key on");
                return Task.FromResult(ExitCodes.Ok);
            }

            var rotated = parse.GetValue(rotate);
            context.Out.WriteLine(UiAccess.Address(port, rotated ? UiAccess.Rotate(settings) : UiAccess.GetOrCreateKey(settings)));
            if (rotated)
            {
                context.Out.WriteLine("Old links and browsers are signed out within a few seconds.");
            }

            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }

    private static Command CreateRequireKey(ProjectOptions projectOption, CliContext context)
    {
        var state = new Argument<string>("state") { Description = "on or off" };
        var command = new Command("require-key", "Ask for a key before the UI answers (for shared machines). Off by default; always on when --bind is not 127.0.0.1.") { state };
        command.SetAction((parse, ct) =>
        {
            var settings = new WorkspaceSettings(projectOption.OpenWorkspace(parse));
            var value = parse.GetValue(state)?.ToLowerInvariant();
            if (value is not ("on" or "off"))
            {
                throw new UsageException("Say 'on' or 'off'.");
            }

            settings.Set(WorkspaceSettings.UiRequireKey, value == "on" ? "true" : "false");
            context.Out.WriteLine(value == "on"
                ? "A key is now required. Run 'autopilot ui link' for the sign-in link. Restart a running UI to apply it."
                : "No key is required any more. Restart a running UI to apply it.");
            return Task.FromResult(ExitCodes.Ok);
        });
        return command;
    }
}
