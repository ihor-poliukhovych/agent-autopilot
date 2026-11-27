using System.Globalization;

namespace Autopilot.Core.Workspace;

/// <summary>
/// Small named values that belong to the whole workspace, not to one project: the web UI port, the opt-in key
/// setting and the key itself. Stored in the workspace database, never in a repository.
/// </summary>
public sealed class WorkspaceSettings(Workspace workspace)
{
    public const string UiPort = "ui.port";
    public const string UiRequireKey = "ui.requireKey";
    public const string UiKey = "ui.key";

    public string? Get(string name)
    {
        using var connection = workspace.Open();
        using var command = Db.Command(connection, "SELECT value FROM workspace_settings WHERE name = $n", ("$n", name));
        return command.ExecuteScalar() as string;
    }

    public void Set(string name, string value)
    {
        using var connection = workspace.Open();
        Db.Execute(connection, "INSERT INTO workspace_settings (name, value) VALUES ($n, $v) ON CONFLICT(name) DO UPDATE SET value = $v", ("$n", name), ("$v", value));
    }

    public bool Remove(string name)
    {
        using var connection = workspace.Open();
        return Db.Execute(connection, "DELETE FROM workspace_settings WHERE name = $n", ("$n", name)) > 0;
    }

    public bool GetBool(string name) =>
        Get(name) is { } v && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("on", StringComparison.OrdinalIgnoreCase));

    public int? GetInt(string name) =>
        int.TryParse(Get(name), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
}
