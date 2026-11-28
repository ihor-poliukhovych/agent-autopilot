using Autopilot.Core.Workspace;

namespace Autopilot.Core.Runs;

/// <summary>One change of a setting: when, which field (for example <c>gate.maxFilesChanged</c>), the old and the new value.</summary>
public sealed record SettingsChange(long Id, string ProjectKey, DateTimeOffset At, string Field, string? OldValue, string? NewValue);

/// <summary>Appends and reads the setting changes of a project (table <c>settings_changes</c>), next to the run history.</summary>
public sealed class SettingsChangeStore(Workspace.Workspace workspace, string projectKey)
{
    public void Append(string field, string? oldValue, string? newValue, DateTimeOffset? at = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        using var connection = workspace.Open();
        Db.Execute(connection,
            "INSERT INTO settings_changes (project_key, at, field, old_value, new_value) VALUES ($k, $t, $f, $o, $n)",
            ("$k", projectKey), ("$t", Db.Stamp(at ?? DateTimeOffset.UtcNow)), ("$f", field), ("$o", oldValue), ("$n", newValue));
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<SettingsChange> List(int? limit = null)
    {
        using var connection = workspace.Open();
        return Db.Query(connection,
            r => new SettingsChange(r.GetInt64(0), r.GetString(1), Db.ParseStamp(r.GetString(2)), r.GetString(3), Db.StringOrNull(r, 4), Db.StringOrNull(r, 5)),
            "SELECT id, project_key, at, field, old_value, new_value FROM settings_changes WHERE project_key = $k ORDER BY id DESC LIMIT $l",
            ("$k", projectKey), ("$l", limit ?? -1));
    }
}
