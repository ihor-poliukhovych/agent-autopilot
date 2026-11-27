using System.CommandLine;
using Autopilot.ClaudeCode.Hooks;

namespace Autopilot.Cli.Commands;

/// <summary>
/// <c>autopilot hook pre-tool-use --policy &lt;file&gt;</c>: the PreToolUse hook Claude Code runs before
/// every tool call. Not meant to be run by hand.
/// </summary>
public static class HookCommand
{
    public static Command Create(CliContext context)
    {
        var policy = new Option<string>("--policy") { Description = "Policy file written by the harness for this run.", Required = true };
        var preToolUse = new Command("pre-tool-use", "Decide one Claude Code tool call (reads the hook JSON from stdin).") { policy };
        preToolUse.SetAction(async (parse, _) =>
            await PreToolUseHook.RunAsync(parse.GetRequiredValue(policy), context.In, context.Out, context.Error).ConfigureAwait(false));

        var hook = new Command("hook", "Hooks that Claude Code calls during an agent run.") { preToolUse };
        hook.Hidden = true;
        return hook;
    }

    /// <summary>True when the arguments start the PreToolUse hook; that path must fail closed (exit 2).</summary>
    public static bool IsHookInvocation(IReadOnlyList<string>? args) =>
        args is not null
        && args.Contains("hook", StringComparer.Ordinal)
        && args.Contains("pre-tool-use", StringComparer.Ordinal);
}

public static class HookCommandLine
{
    /// <summary>
    /// The command Claude Code runs for the hook, without the policy argument. When the tool runs on the
    /// dotnet host (<c>dotnet autopilot.dll</c>), the apphost next to the assembly (<c>autopilot.exe</c>,
    /// or <c>autopilot</c> outside Windows) is preferred because it starts faster; only when there is
    /// none is the assembly path added after the host.
    /// </summary>
    public static string Build(string? processPath, string? entryAssemblyPath, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            throw new InvalidOperationException("Cannot tell which program is running, so the hook command cannot be built.");
        }

        fileExists ??= File.Exists;
        var host = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(host, "dotnet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entryAssemblyPath))
        {
            var appHost = AppHostPath(entryAssemblyPath, OperatingSystem.IsWindows());
            if (appHost is not null && fileExists(appHost))
            {
                return $"{Quote(appHost)} hook pre-tool-use";
            }

            return $"{Quote(processPath)} {Quote(entryAssemblyPath)} hook pre-tool-use";
        }

        return $"{Quote(processPath)} hook pre-tool-use";
    }

    /// <summary>The apphost next to an assembly: same folder and name, <c>.exe</c> on Windows, no extension elsewhere.</summary>
    public static string? AppHostPath(string entryAssemblyPath, bool windows)
    {
        var folder = Path.GetDirectoryName(entryAssemblyPath);
        var name = Path.GetFileNameWithoutExtension(entryAssemblyPath);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        return Path.Combine(folder, windows ? name + ".exe" : name);
    }

    private static string Quote(string path) => "\"" + path + "\"";
}
