using Autopilot.Cli.Commands;
using Autopilot.AzureDevOps;
using Autopilot.ClaudeCode;
using Autopilot.Core;
using Autopilot.Core.Config;
using Autopilot.Core.Diagnostics;
using Autopilot.Core.Facts;
using Autopilot.Core.Gate;
using Autopilot.Core.Git;
using Autopilot.Core.Local;
using Autopilot.Core.Pipeline;
using Autopilot.Core.Tasks;
using Autopilot.Diagnostics;

namespace Autopilot.Cli;

/// <summary>Builds the pipeline for a project from its config. Fails with a readable message.</summary>
public static class ProjectServices
{
    /// <summary>The value of <c>--source</c> that reads <c>.autopilot/tasks/</c> for one run.</summary>
    public const string LocalSource = "local";

    /// <summary>
    /// Builds the pipeline. <paramref name="source"/> "local" reads the work items from
    /// <c>.autopilot/tasks/</c> for this run whatever <c>modules.workItems</c> says, and then drafts stay
    /// local bundles too, so a local task id is never linked to an unrelated remote work item.
    /// </summary>
    public static async Task<AutopilotPipeline> CreatePipelineAsync(string projectRoot, CliContext context, CancellationToken ct, string? source = null, string? dataFolder = null, LocalTaskService? tasks = null, Func<AutopilotConfig, AutopilotConfig>? adjustConfig = null, Func<string, IDisposable>? acquireTriageLock = null, Func<IWorkItemSource, IWorkItemSource>? wrapItems = null)
    {
        AutopilotConfig config;
        try
        {
            config = ConfigLoader.Load(projectRoot, dataFolder);
        }
        catch (ConfigException ex)
        {
            throw new ProjectSetupException(ex.Message);
        }

        var problems = ConfigLoader.Validate(config);
        if (problems.Count > 0)
        {
            throw new ProjectSetupException(
                $"The settings in {ConfigLoader.ConfigPath(projectRoot, dataFolder)} have problems:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)) + Environment.NewLine
                + "Run 'autopilot doctor' for a full check.");
        }

        // A pool run builds on the integration branch: the caller swaps the base branch before anything reads it.
        config = adjustConfig?.Invoke(config) ?? config;

        var repoRoot = await GitClient.FindRepositoryRootAsync(projectRoot, ct).ConfigureAwait(false)
            ?? throw new ProjectSetupException($"{projectRoot} is not in a git repository. Run 'git init' and commit your code first.");

        var diagnosticsServer = DiagnosticsServerFor(projectRoot, config, dataFolder);
        var git = new GitClient(repoRoot, config.Identity is { } id ? new GitIdentity(id.Name, id.Email) : null);
        var worktreeRoot = context.WorktreeRoot ?? GitClient.DefaultWorktreeRoot(repoRoot);
        var localSource = string.Equals(source, LocalSource, StringComparison.OrdinalIgnoreCase);
        if (localSource && !string.Equals(config.Modules.WorkItems.Type, LocalSource, StringComparison.OrdinalIgnoreCase))
        {
            await context.Out.WriteLineAsync(
                $"--source local: work items come from .autopilot/tasks/ for this run (modules.workItems is \"{config.Modules.WorkItems.Type}\"); drafts are local bundles only.").ConfigureAwait(false);
        }

        var (workItems, publisher) = localSource
            ? CreateLocalModules(projectRoot, dataFolder, tasks)
            : CreateModules(projectRoot, config, dataFolder, tasks);
        return new AutopilotPipeline(new AutopilotPipelineOptions
        {
            ProjectRoot = projectRoot,
            DataFolder = dataFolder,
            Config = config,
            WorkItems = wrapItems?.Invoke(workItems) ?? workItems,
            Publisher = publisher,
            Runner = CreateRunner(context, config),
            Git = git,
            Facts = context.FactsCollectorFactory?.Invoke(config) ?? new GateFactsCollector(git, config, worktreeRoot) { BaseCacheFolder = ProjectPaths.GateBaseCacheFolder(projectRoot, dataFolder) },
            WorktreeRoot = worktreeRoot,
            EvaluateGate = context.EvaluateGate ?? MergeGate.Evaluate,
            Log = context.Out,
            DiagnosticsServer = diagnosticsServer,
            AcquireTriageLock = acquireTriageLock,
        });
    }

    /// <summary>The agent runner: the one the context gives (tests), otherwise Claude Code headless.</summary>
    public static IAgentRunner CreateRunner(CliContext context, AutopilotConfig config) =>
        context.RunnerFactory?.Invoke() ?? new ClaudeCodeRunner(new ClaudeCodeRunnerOptions
        {
            HookCommand = HookCommandLine.Build(Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location),
            PassEnvironment = config.PassEnvironment,
        });

    /// <summary>
    /// Picks the work item source and the publisher from the config. The local bundle is always
    /// written; with Azure DevOps the draft pull request is opened after it.
    /// The module lives as long as the process (one command), so it is not disposed here.
    /// </summary>
    public static (IWorkItemSource WorkItems, IPublisher Publisher) CreateModules(string projectRoot, AutopilotConfig config, string? dataFolder = null, LocalTaskService? tasks = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var (workItems, publisher) = CreateLocalModules(projectRoot, dataFolder, tasks);

        var adoForItems = AzureDevOpsModule.IsSelected(config.Modules.WorkItems);
        var adoForPublisher = AzureDevOpsModule.IsSelected(config.Modules.Publisher);
        if (!adoForItems && !adoForPublisher)
        {
            return (workItems, publisher);
        }

        try
        {
            // Both modules read the same settings block; the publisher's wins when both are set.
            var module = adoForPublisher ? config.Modules.Publisher : config.Modules.WorkItems;
            var ado = AzureDevOpsModule.Create(module, projectRoot, config.BaseBranch, config.BranchPrefix, requireRepository: adoForPublisher);
            if (adoForItems)
            {
                workItems = ado.WorkItems;
            }

            if (adoForPublisher)
            {
                publisher = new LocalThenRemotePublisher(publisher, ado.CreatePublisher());
            }
        }
        catch (AzureDevOpsSettingsException ex)
        {
            throw new ProjectSetupException(ex.Message);
        }

        return (workItems, publisher);
    }

    /// <summary>
    /// The local source and publisher. With <paramref name="tasks"/> (a registered project whose tasks are local) the
    /// work items are the workspace tasks and the gate verdict and draft are appended to their history; otherwise
    /// they are the repository-local task files.
    /// </summary>
    public static (IWorkItemSource WorkItems, IPublisher Publisher) CreateLocalModules(string projectRoot, string? dataFolder, LocalTaskService? tasks)
    {
        IPublisher publisher = new LocalPublisher(projectRoot, dataFolder);
        return tasks is null
            ? (new LocalWorkItemSource(projectRoot, dataFolder), publisher)
            : (new LocalTaskWorkItemSource(tasks, new TriageStore(projectRoot, dataFolder)), new TaskHistoryPublisher(publisher, tasks));
    }

    /// <summary>
    /// The diagnostics MCP server for agent runs when modules.diagnostics.type is "jsonl", else null.
    /// The path setting is checked here so a bad path stops the run before any agent starts.
    /// </summary>
    public static McpServerSpec? DiagnosticsServerFor(string projectRoot, AutopilotConfig config, string? dataFolder = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!DiagnosticsMcp.IsEnabled(config.Modules.Diagnostics))
        {
            return null;
        }

        try
        {
            DiagnosticsSourceFactory.Create(projectRoot, config.Modules.Diagnostics);
        }
        catch (DiagnosticsConfigException ex)
        {
            throw new ProjectSetupException(ex.Message);
        }

        return DiagnosticsServerCommand.Build(
            Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location, projectRoot, dataFolder: dataFolder);
    }
}

/// <summary>Writes the local bundle first (the audit trail), then publishes remotely.</summary>
public sealed class LocalThenRemotePublisher(IPublisher local, IPublisher remote) : IPublisher
{
    public async Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        var localResult = await local.PublishAsync(request, ct).ConfigureAwait(false);
        if (!localResult.Succeeded)
        {
            return localResult;
        }

        var remoteResult = await remote.PublishAsync(request, ct).ConfigureAwait(false);
        return remoteResult.Succeeded
            ? remoteResult
            : remoteResult with { Error = $"{remoteResult.Error} (the local bundle is in {localResult.Location})" };
    }
}

/// <summary>The project is not set up well enough to run; the message says what to do.</summary>
public sealed class ProjectSetupException(string message) : Exception(message);
