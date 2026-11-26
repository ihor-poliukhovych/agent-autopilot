using System.CommandLine;
using DevAutopilot.Cli.Commands;
using DevAutopilot.ClaudeCode.Hooks;

namespace DevAutopilot.Cli;

/// <summary>Builds the command tree and runs one invocation.</summary>
public static class CliApp
{
    public static async Task<int> RunAsync(string[] args, CliContext context, CancellationToken ct = default)
    {
        if (!HookCommand.IsHookInvocation(args))
        {
            return await RunCommandAsync(args, context, ct).ConfigureAwait(false);
        }

        // Claude Code treats every exit code except 2 as "go ahead", so the hook path turns every
        // failure, including a parse error or an exception, into exit 2 (deny).
        try
        {
            var exit = await RunCommandAsync(args, context, ct).ConfigureAwait(false);
            return exit == ExitCodes.Ok ? ExitCodes.Ok : PreToolUseHook.DenyExitCode;
        }
        catch (Exception ex)
        {
            try
            {
                await context.Error.WriteLineAsync($"Autopilot hook failed, so the call is denied: {ex.Message}").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Nothing more can be said; the exit code still denies.
            }

            return PreToolUseHook.DenyExitCode;
        }
    }

    private static async Task<int> RunCommandAsync(string[] args, CliContext context, CancellationToken ct)
    {
        var projectOption = new ProjectOptions();

        var root = new RootCommand("Dev Autopilot runs a Claude Code agent over your backlog behind a merge gate written in code. A person approves every merge.");
        projectOption.AddTo(root);

        root.Subcommands.Add(InitCommand.Create(projectOption, context));
        root.Subcommands.Add(DoctorCommand.Create(projectOption, context));
        root.Subcommands.Add(PipelineCommands.CreateTriage(projectOption, context));
        root.Subcommands.Add(PipelineCommands.CreateImplement(projectOption, context));
        root.Subcommands.Add(PipelineCommands.CreateRun(projectOption, context));
        root.Subcommands.Add(EvalCommand.Create(projectOption, context));
        root.Subcommands.Add(ProjectCommand.Create(projectOption, context));
        root.Subcommands.Add(AzureDevOpsCommand.Create(projectOption, context));
        root.Subcommands.Add(TaskCommand.Create(projectOption, context));
        foreach (var backend in BackendCommands.Create(projectOption, context))
        {
            root.Subcommands.Add(backend);
        }

        root.Subcommands.Add(HookCommand.Create(context));
        root.Subcommands.Add(McpCommand.Create(projectOption, context));

        var parseResult = root.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                await context.Error.WriteLineAsync(error.Message).ConfigureAwait(false);
            }

            await context.Error.WriteLineAsync("Run 'autopilot --help' to see the commands.").ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        var invocation = new InvocationConfiguration
        {
            Output = context.Out,
            Error = context.Error,
            EnableDefaultExceptionHandler = false,
        };

        try
        {
            return await parseResult.InvokeAsync(invocation, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await context.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        catch (UsageException ex)
        {
            await context.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.Usage;
        }
        catch (Exception ex)
        {
            await context.Error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }

    /// <summary>Resolves the --project value to a full path that exists.</summary>
    public static string ResolveProject(string? value)
    {
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(value) ? Directory.GetCurrentDirectory() : value);
        if (!Directory.Exists(path))
        {
            throw new UsageException($"The project folder does not exist: {path}");
        }

        return path;
    }
}

/// <summary>A mistake in how the command was called; exits with code 2.</summary>
public sealed class UsageException(string message) : Exception(message);
