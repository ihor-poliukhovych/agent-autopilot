using DevAutopilot.Core.Config;

namespace DevAutopilot.Core.Local;

/// <summary>Where Dev Autopilot keeps its files inside a project's <c>.autopilot/</c> folder.</summary>
public static class ProjectPaths
{
    public const int MaxIdLength = 64;

    /// <summary>
    /// The folder with settings, tasks and out/. <paramref name="dataFolder"/> is a workspace project folder;
    /// null means the repository-local <c>.autopilot/</c> of <paramref name="projectRoot"/>.
    /// </summary>
    public static string AutopilotFolder(string projectRoot, string? dataFolder = null) =>
        dataFolder ?? Path.Combine(projectRoot, AutopilotConfig.FolderName);

    public static string TasksFolder(string projectRoot, string? dataFolder = null) => Path.Combine(AutopilotFolder(projectRoot, dataFolder), "tasks");

    public static string OutFolder(string projectRoot, string? dataFolder = null) => Path.Combine(AutopilotFolder(projectRoot, dataFolder), "out");

    public static string TriageFolder(string projectRoot, string? dataFolder = null) => Path.Combine(OutFolder(projectRoot, dataFolder), "triage");

    /// <summary>Cached build and test facts of base commits, one JSON file per key.</summary>
    public static string GateBaseCacheFolder(string projectRoot, string? dataFolder = null) => Path.Combine(OutFolder(projectRoot, dataFolder), "cache", "gate-base");

    public static string RunFolder(string projectRoot, string workItemId, string? dataFolder = null) =>
        Path.Combine(OutFolder(projectRoot, dataFolder), "runs", SafeId(workItemId));

    /// <summary>
    /// True for ids made of ASCII letters, digits, '-' and '_', at most <see cref="MaxIdLength"/> long,
    /// not starting with '_' or '.'. Ids become file, folder and branch names, so nothing else is allowed.
    /// Internal folder names use a character ids cannot have ('@'), so they never collide with an id.
    /// </summary>
    public static bool IsValidId(string? workItemId) =>
        !string.IsNullOrEmpty(workItemId)
        && workItemId.Length <= MaxIdLength
        && workItemId[0] is not ('_' or '.')
        && workItemId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Why an id is refused, in plain English.</summary>
    public static string InvalidIdMessage(string? workItemId) =>
        $"Work item id \"{workItemId}\" is not valid: use only letters, digits, '-' and '_', at most {MaxIdLength} characters, not starting with '_' or '.'.";

    /// <summary>Returns the id when it is valid (see <see cref="IsValidId"/>); throws otherwise.</summary>
    public static string SafeId(string workItemId)
    {
        if (!IsValidId(workItemId))
        {
            throw new ArgumentException(InvalidIdMessage(workItemId), nameof(workItemId));
        }

        return workItemId;
    }
}
