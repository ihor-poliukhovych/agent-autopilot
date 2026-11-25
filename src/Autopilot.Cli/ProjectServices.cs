using Autopilot.Cli.Commands;
using Autopilot.ClaudeCode;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Git;
using Autopilot.Core.Local;
using Autopilot.Core.Pipeline;

namespace Autopilot.Cli;

/// <summary>Builds the pipeline for a project from its config. Fails with a readable message.</summary>
public static class ProjectServices
{
    public static async Task<AutopilotPipeline> CreatePipelineAsync(string projectRoot, CliContext context, CancellationToken ct)
    {
        AutopilotConfig config;
        try
        {
            config = ConfigLoader.Load(projectRoot);
        }
        catch (ConfigException ex)
        {
            throw new ProjectSetupException(ex.Message);
        }

        var problems = ConfigLoader.Validate(config);
        if (problems.Count > 0)
        {
            throw new ProjectSetupException(
                "The settings in .autopilot/autopilot.json have problems:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)) + Environment.NewLine
                + "Run 'autopilot doctor' for a full check.");
        }

        var repoRoot = await GitClient.FindRepositoryRootAsync(projectRoot, ct).ConfigureAwait(false)
            ?? throw new ProjectSetupException($"{projectRoot} is not in a git repository. Run 'git init' and commit your code first.");

        var git = new GitClient(repoRoot);
        var worktreeRoot = context.WorktreeRoot ?? GitClient.DefaultWorktreeRoot(repoRoot);
        return new AutopilotPipeline(new AutopilotPipelineOptions
        {
            ProjectRoot = projectRoot,
            Config = config,
            WorkItems = new LocalWorkItemSource(projectRoot),
            Publisher = new LocalPublisher(projectRoot),
            Runner = context.RunnerFactory?.Invoke() ?? new ClaudeCodeRunner(new ClaudeCodeRunnerOptions
            {
                HookCommand = HookCommandLine.Build(Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location),
                PassEnvironment = config.PassEnvironment,
            }),
            Git = git,
            Facts = context.FactsCollectorFactory?.Invoke(config) ?? new GateFactsCollector(git, config, worktreeRoot),
            WorktreeRoot = worktreeRoot,
            EvaluateGate = context.EvaluateGate ?? MergeGate.Evaluate,
            Log = context.Out,
        });
    }
}

/// <summary>The project is not set up well enough to run; the message says what to do.</summary>
public sealed class ProjectSetupException(string message) : Exception(message);
