using Autopilot.Core.Config;
using Autopilot.Core.Jobs;
using Autopilot.Core.Local;
using Autopilot.Core.Pool;
using Autopilot.Core.Runs;
using Autopilot.Core.Settings;
using Autopilot.Core.Tasks;
using Autopilot.Core.Workspace;

namespace Autopilot.Web;

/// <summary>The core services of one registered project, built for one request.</summary>
public sealed class ProjectScope
{
    public ProjectScope(WebUiOptions options, ProjectRecord project)
    {
        Options = options;
        Project = project;
        Workspace = options.Workspace;
        DataFolder = Workspace.ProjectFolder(project.Key);
        Pool = new PoolService(Workspace, project.Key);
        Runs = new RunStore(Workspace, project.Key);
        Jobs = new JobService(Workspace, project.Key, options.IsProcessAlive);
        Lock = new ProjectRunLock(Workspace, options.IsProcessAlive);
        Settings = new ProjectSettingsService(project.RepositoryFolder, DataFolder);
        Triage = new TriageStore(project.RepositoryFolder, DataFolder);
        if (project.TaskSource == ProjectRegistry.LocalSource)
        {
            var service = new LocalTaskService(Workspace, project.Key);
            // Same rule as the command line: task files that were never imported keep being read until they are.
            var tasksFolder = Path.Combine(DataFolder, "tasks");
            LegacyTaskFiles = Directory.Exists(tasksFolder) && Directory.EnumerateFiles(tasksFolder, "*.md").Any()
                && service.List(includeClosed: true).Count == 0;
            LocalTasks = LegacyTaskFiles ? null : service;
        }
    }

    public WebUiOptions Options { get; }

    public ProjectRecord Project { get; }

    public Workspace Workspace { get; }

    public string DataFolder { get; }

    public PoolService Pool { get; }

    public RunStore Runs { get; }

    public JobService Jobs { get; }

    public ProjectRunLock Lock { get; }

    public ProjectSettingsService Settings { get; }

    public TriageStore Triage { get; }

    /// <summary>The tasks kept in the workspace database; null for a tracker project or while task files wait for an import.</summary>
    public LocalTaskService? LocalTasks { get; }

    public bool LegacyTaskFiles { get; }

    public WorkItemListService CreateList()
    {
        var config = ConfigLoader.Load(Project.RepositoryFolder, DataFolder);
        var source = Options.Sources.Create(Project.RepositoryFolder, config, DataFolder, LocalTasks);
        return new WorkItemListService(source, Triage, Pool, Runs);
    }

    public ScheduleServiceFactory Schedule => new(this);

    /// <summary>The command line a detached or scheduled run starts, quoted for a shell.</summary>
    public string ProgramCommandLine() => CommandLine(["--project", Project.Key, "--workspace", Workspace.Root]);

    public string CommandLine(IEnumerable<string> extra) =>
        string.Join(' ', new[] { Options.Program.File }.Concat(Options.Program.Arguments).Concat(extra).Select(Quote));

    private static string Quote(string part) => part.Length > 0 && part.All(c => !char.IsWhiteSpace(c) && c != '"') ? part : "\"" + part + "\"";

    public readonly record struct ScheduleServiceFactory(ProjectScope Scope)
    {
        public Autopilot.Core.Scheduling.ScheduleService Create() => new(
            Scope.Workspace, Scope.Project.Key,
            Scope.Options.Scheduler ?? (OperatingSystem.IsWindows()
                ? new Autopilot.Core.Scheduling.WindowsTaskScheduler()
                : new Autopilot.Core.Scheduling.CronScheduler()));
    }
}

/// <summary>Finds a project by key and builds its scope.</summary>
public sealed class ProjectScopes(WebUiOptions options)
{
    public ProjectScope Get(string key)
    {
        var project = new ProjectRegistry(options.Workspace).Get(key)
            ?? throw new NotFoundException($"There is no project \"{key}\".");
        return new ProjectScope(options, project);
    }
}

/// <summary>The thing asked for does not exist (HTTP 404).</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>The request cannot be done now, for example a run is already active (HTTP 409).</summary>
public sealed class ConflictException(string message) : Exception(message);
