using Autopilot.Core.Config;

namespace Autopilot.Core.Local;

/// <summary>Where Agent Autopilot keeps its files inside a project's <c>.autopilot/</c> folder.</summary>
public static class ProjectPaths
{
    public static string AutopilotFolder(string projectRoot) => Path.Combine(projectRoot, AutopilotConfig.FolderName);

    public static string TasksFolder(string projectRoot) => Path.Combine(AutopilotFolder(projectRoot), "tasks");

    public static string OutFolder(string projectRoot) => Path.Combine(AutopilotFolder(projectRoot), "out");

    public static string TriageFolder(string projectRoot) => Path.Combine(OutFolder(projectRoot), "triage");

    public static string RunFolder(string projectRoot, string workItemId) =>
        Path.Combine(OutFolder(projectRoot), "runs", SafeId(workItemId));

    /// <summary>Returns the id when it is safe to use as a file name; throws otherwise.</summary>
    public static string SafeId(string workItemId)
    {
        if (string.IsNullOrWhiteSpace(workItemId)
            || workItemId.Contains("..", StringComparison.Ordinal)
            || workItemId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || workItemId.Contains('/')
            || workItemId.Contains('\\'))
        {
            throw new ArgumentException($"Work item id \"{workItemId}\" cannot be used as a file name.", nameof(workItemId));
        }

        return workItemId;
    }
}
