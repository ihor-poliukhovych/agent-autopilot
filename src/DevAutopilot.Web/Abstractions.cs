using DevAutopilot.Core;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Tasks;

namespace DevAutopilot.Web;

/// <summary>One line of a setup check: the same shape as a line of <c>autopilot doctor</c>.</summary>
public sealed record CheckLine(string Status, string Name, string Detail, string? Fix = null)
{
    public const string Ok = "ok";
    public const string Fail = "fail";
    public const string Warn = "warn";
    public const string Skip = "skip";
}

/// <summary>What the checker looks at: the repository and a folder that holds the draft settings.</summary>
public sealed record SetupCheckRequest(string RepositoryFolder, string DataFolder, bool SkipBuild);

/// <summary>
/// Runs the same checks as <c>autopilot doctor</c> and finds defaults for a folder. The web project cannot
/// reference the command line project, so the command line project supplies this.
/// </summary>
public interface ISetupChecker
{
    Task<IReadOnlyList<CheckLine>> RunAsync(SetupCheckRequest request, CancellationToken ct);

    Task<AutopilotConfig> DetectDefaultsAsync(string repositoryFolder, string? baseBranch, CancellationToken ct);
}

/// <summary>One work item of the preview, as the wizard shows it.</summary>
public sealed record WorkItemPreview(string Id, string Title, string? Type, string? State, string? AssignedTo, string? Url);

/// <summary>One page of the preview: the items, how many the query matches and whether there is a next page.</summary>
public sealed record WorkItemPreviewPage(IReadOnlyList<WorkItemPreview> Items, int Total, int Skip, int Take, bool HasMore, bool Truncated);

/// <summary>Reads work items from Azure DevOps for the wizard's preview, from the settings of the draft. Changes nothing.</summary>
public interface IAzureDevOpsProbe
{
    Task<WorkItemPreviewPage> PreviewAsync(ModuleConfig module, string repositoryFolder, int skip, int take, CancellationToken ct);
}

/// <summary>Builds the work item source of a project (the local tasks, the task files or a tracker).</summary>
public interface IWorkItemSourceFactory
{
    IWorkItemSource Create(string repositoryFolder, AutopilotConfig config, string dataFolder, LocalTaskService? tasks);
}

/// <summary>The program that detached jobs and the night schedule start, as a file and its leading arguments.</summary>
public sealed record ProgramInfo(string File, IReadOnlyList<string> Arguments);
