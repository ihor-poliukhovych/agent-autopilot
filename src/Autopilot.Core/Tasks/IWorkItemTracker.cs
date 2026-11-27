namespace Autopilot.Core.Tasks;

/// <summary>A work item as a tracker holds it.</summary>
public sealed record TrackerItem(string ExternalId, string Title, string Body, IReadOnlyList<string> Labels, bool Closed, string? Url = null);

/// <summary>A comment to show in the tracker. <see cref="Text"/> is markdown.</summary>
public sealed record TrackerComment(string Author, string Text);

/// <summary>
/// What to write back to one tracker item. A null part is left alone.
/// <see cref="Labels"/> is the full set the item should carry from Autopilot's side
/// (the adapter adds missing ones and must not remove labels it does not know about).
/// </summary>
public sealed record TrackerWrite(
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<TrackerComment>? Comments = null,
    bool? Closed = null,
    string? DraftLink = null);

/// <summary>
/// The whole contract between Autopilot's task model and a tracker (Azure DevOps, GitHub, ...).
/// Two methods on purpose: one reads items in, one writes results out. See docs/TRACKERS.md.
/// </summary>
public interface IWorkItemTracker
{
    /// <summary>A short name for messages and history, such as "azure-devops".</summary>
    string Name { get; }

    /// <summary>The items to import: open ones, and closed ones if the tracker can tell cheaply. The external id must be stable.</summary>
    Task<IReadOnlyList<TrackerItem>> ReadAsync(CancellationToken ct);

    /// <summary>Applies one write to one item. Must be safe to repeat with the same input.</summary>
    Task WriteAsync(string externalId, TrackerWrite write, CancellationToken ct);
}
