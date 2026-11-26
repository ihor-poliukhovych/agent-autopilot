using System.CommandLine;
using DevAutopilot.Core.Workspace;

namespace DevAutopilot.Cli;

/// <summary>
/// Which project a command works on. <see cref="RepoRoot"/> is the git repository; <see cref="DataFolder"/> is the
/// workspace folder of the project's settings, tasks and out/, or null for the repository-local <c>.autopilot/</c>.
/// </summary>
public sealed record ProjectTarget(string RepoRoot, string? DataFolder, ProjectRecord? Project = null, DevAutopilot.Core.Workspace.Workspace? Workspace = null)
{
    /// <summary>Pool, jobs, runs and schedules are kept per registered project; a repository-local folder has none.</summary>
    public (ProjectRecord Project, DevAutopilot.Core.Workspace.Workspace Workspace) RequireRegistered() =>
        Project is not null && Workspace is not null
            ? (Project, Workspace)
            : throw new UsageException("This command needs a registered project. Run 'autopilot project add <key> --repo <folder>' (or 'project import-local'), then pass --project <key>.");
}

/// <summary>The global <c>--project</c> and <c>--workspace</c> options.</summary>
public sealed class ProjectOptions
{
    public ProjectOptions()
    {
        Project = new Option<string>("--project")
        {
            Description = "A registered project key (data lives in the workspace), or a repository folder that has its own .autopilot/. Default: the current folder.",
            Recursive = true,
        };
        Workspace = new Option<string>("--workspace")
        {
            Description = $"Workspace folder. Default: the {DevAutopilot.Core.Workspace.Workspace.EnvironmentVariable} environment variable, else .dev-autopilot in your user profile.",
            Recursive = true,
        };
    }

    public Option<string> Project { get; }

    public Option<string> Workspace { get; }

    public void AddTo(Command command)
    {
        command.Options.Add(Project);
        command.Options.Add(Workspace);
    }

    public DevAutopilot.Core.Workspace.Workspace OpenWorkspace(ParseResult parse) =>
        DevAutopilot.Core.Workspace.Workspace.Resolve(parse.GetValue(Workspace));

    /// <summary>
    /// A value that is a registered key selects the workspace project. Otherwise it is a repository folder, as
    /// before. With no value the current folder is used the old way.
    /// </summary>
    public ProjectTarget Resolve(ParseResult parse)
    {
        var value = parse.GetValue(Project);
        if (!string.IsNullOrWhiteSpace(value) && ProjectRegistry.IsValidKey(value))
        {
            var workspace = OpenWorkspace(parse);
            var record = new ProjectRegistry(workspace).Get(value);
            if (record is not null)
            {
                if (!Directory.Exists(record.RepositoryFolder))
                {
                    throw new UsageException($"The repository folder of project \"{value}\" no longer exists: {record.RepositoryFolder}");
                }

                return new ProjectTarget(record.RepositoryFolder, workspace.ProjectFolder(record.Key), record, workspace);
            }

            if (!Directory.Exists(value))
            {
                throw new UsageException($"There is no project \"{value}\" in the workspace {workspace.Root}. Run 'autopilot project list', or pass a repository folder.");
            }
        }

        return new ProjectTarget(CliApp.ResolveProject(value), null);
    }
}
