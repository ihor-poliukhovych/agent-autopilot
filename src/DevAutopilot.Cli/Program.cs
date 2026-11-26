using DevAutopilot.Cli;

using DevAutopilot.Cli.Commands;

// The hook path must exit 2 on any failure, even one during startup, or Claude Code lets the call through.
var isHook = HookCommand.IsHookInvocation(args);
try
{
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };

    return await CliApp.RunAsync(args, CliContext.FromConsole(), cancel.Token);
}
catch (Exception ex) when (isHook)
{
    try
    {
        await Console.Error.WriteLineAsync($"Autopilot hook failed, so the call is denied: {ex.Message}");
    }
    catch (Exception)
    {
        // The exit code still denies.
    }

    return DevAutopilot.ClaudeCode.Hooks.PreToolUseHook.DenyExitCode;
}
