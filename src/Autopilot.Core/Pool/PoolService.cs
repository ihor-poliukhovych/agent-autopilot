using Autopilot.Core.Local;
using Autopilot.Core.Workspace;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Pool;

public sealed record PoolEntry(string WorkItemId, int Position, DateTimeOffset AddedAt);

/// <summary>The queue of work items a person picked for the agent, stored in the workspace database.</summary>
public sealed class PoolService(Workspace.Workspace workspace, string projectKey)
{
    /// <summary>Adds an item at the end. Returns false when it is already in the pool.</summary>
    public bool Add(string workItemId)
    {
        if (!ProjectPaths.IsValidId(workItemId))
        {
            throw new WorkspaceException(ProjectPaths.InvalidIdMessage(workItemId));
        }

        using var connection = workspace.Open();
        using var max = Db.Command(connection, "SELECT COALESCE(MAX(position), 0) + 1 FROM pool_items WHERE project_key = $k", ("$k", projectKey));
        var next = Convert.ToInt32(max.ExecuteScalar());
        return Db.Execute(connection,
            "INSERT OR IGNORE INTO pool_items (project_key, work_item_id, position, added_at) VALUES ($k, $i, $p, $t)",
            ("$k", projectKey), ("$i", workItemId), ("$p", next), ("$t", Db.Stamp(DateTimeOffset.UtcNow))) > 0;
    }

    public bool Remove(string workItemId)
    {
        using var connection = workspace.Open();
        var removed = Db.Execute(connection, "DELETE FROM pool_items WHERE project_key = $k AND work_item_id = $i", ("$k", projectKey), ("$i", workItemId)) > 0;
        if (removed)
        {
            Renumber(connection, Ids(connection));
        }

        return removed;
    }

    public IReadOnlyList<PoolEntry> List()
    {
        using var connection = workspace.Open();
        return Db.Query(connection, r => new PoolEntry(r.GetString(0), r.GetInt32(1), Db.ParseStamp(r.GetString(2))),
            "SELECT work_item_id, position, added_at FROM pool_items WHERE project_key = $k ORDER BY position", ("$k", projectKey));
    }

    /// <summary>Moves an item to a 1-based position (clamped to the pool's size). Returns false when it is not in the pool.</summary>
    public bool Move(string workItemId, int position)
    {
        using var connection = workspace.Open();
        var ids = Ids(connection);
        var current = ids.FindIndex(i => string.Equals(i, workItemId, StringComparison.Ordinal));
        if (current < 0)
        {
            return false;
        }

        ids.RemoveAt(current);
        ids.Insert(Math.Clamp(position - 1, 0, ids.Count), workItemId);
        Renumber(connection, ids);
        return true;
    }

    public int Clear()
    {
        using var connection = workspace.Open();
        return Db.Execute(connection, "DELETE FROM pool_items WHERE project_key = $k", ("$k", projectKey));
    }

    private List<string> Ids(SqliteConnection connection) =>
        Db.Query(connection, r => r.GetString(0), "SELECT work_item_id FROM pool_items WHERE project_key = $k ORDER BY position", ("$k", projectKey));

    private void Renumber(SqliteConnection connection, List<string> ids)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            Db.Execute(connection, "UPDATE pool_items SET position = $p WHERE project_key = $k AND work_item_id = $i",
                ("$p", index + 1), ("$k", projectKey), ("$i", ids[index]));
        }
    }
}
