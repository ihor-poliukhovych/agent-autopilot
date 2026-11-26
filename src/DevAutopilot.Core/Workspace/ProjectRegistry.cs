using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DevAutopilot.Core.Workspace;

/// <summary>A project registered in the workspace. Its repository is only linked, never written to.</summary>
public sealed record ProjectRecord(
    string Key,
    string DisplayName,
    string RepositoryFolder,
    string BaseBranch,
    string TaskSource,
    string SettingsJson,
    DateTimeOffset CreatedAt);

/// <summary>The values <see cref="ProjectRegistry.Add"/> takes. Display name defaults to the key.</summary>
public sealed record NewProject(
    string Key,
    string RepositoryFolder,
    string? DisplayName = null,
    string BaseBranch = "main",
    string TaskSource = ProjectRegistry.LocalSource,
    string SettingsJson = "{}");

/// <summary>A change to a project; null leaves a field as it is.</summary>
public sealed record ProjectUpdate(
    string? DisplayName = null,
    string? RepositoryFolder = null,
    string? BaseBranch = null,
    string? TaskSource = null,
    string? SettingsJson = null);

/// <summary>Add, list, get, update, remove and validate projects in the workspace database.</summary>
public sealed partial class ProjectRegistry(Workspace workspace)
{
    public const string LocalSource = "local";
    public const string AzureDevOpsSource = "azure-devops";
    public const int MaxKeyLength = 64;

    private const string SelectSql = "SELECT key, display_name, repository_folder, base_branch, task_source, settings_json, created_at FROM projects";

    private static readonly string[] ReservedNames =
        ["con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"];

    public static IReadOnlyList<string> TaskSources { get; } = [LocalSource, AzureDevOpsSource];

    public Workspace Workspace { get; } = workspace;

    /// <summary>True for keys of lower-case letters, digits, '-' and '_', starting with a letter or digit; they become folder names.</summary>
    public static bool IsValidKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength && KeyPattern().IsMatch(key)
        && !ReservedNames.Contains(key, StringComparer.Ordinal);

    public static string InvalidKeyMessage(string? key) =>
        $"Project key \"{key}\" is not valid: use lower-case letters, digits, '-' and '_', at most {MaxKeyLength} characters, starting with a letter or digit, and not a reserved device name.";

    /// <summary>What is wrong with a new project, in plain English. Empty means it can be added.</summary>
    public IReadOnlyList<string> Validate(NewProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var problems = new List<string>();
        if (!IsValidKey(project.Key))
        {
            problems.Add(InvalidKeyMessage(project.Key));
        }
        else if (Get(project.Key) is not null)
        {
            problems.Add($"A project with the key \"{project.Key}\" already exists.");
        }

        problems.AddRange(ValidateFields(project.RepositoryFolder, project.BaseBranch, project.TaskSource, project.SettingsJson));
        return problems;
    }

    public ProjectRecord Add(NewProject project)
    {
        var problems = Validate(project);
        if (problems.Count > 0)
        {
            throw new WorkspaceException(string.Join(Environment.NewLine, problems));
        }

        var record = new ProjectRecord(
            project.Key,
            string.IsNullOrWhiteSpace(project.DisplayName) ? project.Key : project.DisplayName.Trim(),
            Path.GetFullPath(project.RepositoryFolder),
            project.BaseBranch.Trim(),
            project.TaskSource,
            project.SettingsJson,
            DateTimeOffset.UtcNow);
        using (var connection = Workspace.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO projects (key, display_name, repository_folder, base_branch, task_source, settings_json, created_at) VALUES ($k, $d, $r, $b, $s, $j, $c)";
            command.Parameters.AddWithValue("$k", record.Key);
            command.Parameters.AddWithValue("$d", record.DisplayName);
            command.Parameters.AddWithValue("$r", record.RepositoryFolder);
            command.Parameters.AddWithValue("$b", record.BaseBranch);
            command.Parameters.AddWithValue("$s", record.TaskSource);
            command.Parameters.AddWithValue("$j", record.SettingsJson);
            command.Parameters.AddWithValue("$c", record.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        var folder = Workspace.ProjectFolder(record.Key);
        Directory.CreateDirectory(Path.Combine(folder, "tasks"));
        Directory.CreateDirectory(Path.Combine(folder, "out"));
        return record;
    }

    public IReadOnlyList<ProjectRecord> List()
    {
        if (!File.Exists(Workspace.DatabasePath))
        {
            return [];
        }

        using var connection = Workspace.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " ORDER BY key";
        using var reader = command.ExecuteReader();
        var list = new List<ProjectRecord>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }

        return list;
    }

    public ProjectRecord? Get(string key)
    {
        if (!File.Exists(Workspace.DatabasePath) || !IsValidKey(key))
        {
            return null;
        }

        using var connection = Workspace.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE key = $k";
        command.Parameters.AddWithValue("$k", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public ProjectRecord Update(string key, ProjectUpdate change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var current = Get(key) ?? throw new WorkspaceException($"There is no project \"{key}\".");
        var updated = current with
        {
            DisplayName = string.IsNullOrWhiteSpace(change.DisplayName) ? current.DisplayName : change.DisplayName.Trim(),
            RepositoryFolder = change.RepositoryFolder is null ? current.RepositoryFolder : Path.GetFullPath(change.RepositoryFolder),
            BaseBranch = string.IsNullOrWhiteSpace(change.BaseBranch) ? current.BaseBranch : change.BaseBranch.Trim(),
            TaskSource = change.TaskSource ?? current.TaskSource,
            SettingsJson = change.SettingsJson ?? current.SettingsJson,
        };
        var problems = ValidateFields(updated.RepositoryFolder, updated.BaseBranch, updated.TaskSource, updated.SettingsJson).ToList();
        if (problems.Count > 0)
        {
            throw new WorkspaceException(string.Join(Environment.NewLine, problems));
        }

        using var connection = Workspace.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE projects SET display_name = $d, repository_folder = $r, base_branch = $b, task_source = $s, settings_json = $j WHERE key = $k";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$d", updated.DisplayName);
        command.Parameters.AddWithValue("$r", updated.RepositoryFolder);
        command.Parameters.AddWithValue("$b", updated.BaseBranch);
        command.Parameters.AddWithValue("$s", updated.TaskSource);
        command.Parameters.AddWithValue("$j", updated.SettingsJson);
        command.ExecuteNonQuery();
        return updated;
    }

    /// <summary>
    /// Unlinks the project. Its workspace folder (settings, tasks, run output) is deleted only when
    /// <paramref name="deleteData"/> is true. The linked repository is never touched.
    /// Returns false when there is no such project.
    /// </summary>
    public bool Remove(string key, bool deleteData = false)
    {
        if (Get(key) is null)
        {
            return false;
        }

        using (var connection = Workspace.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM projects WHERE key = $k";
            command.Parameters.AddWithValue("$k", key);
            command.ExecuteNonQuery();
        }

        var folder = Workspace.ProjectFolder(key);
        if (deleteData && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        return true;
    }

    private static IEnumerable<string> ValidateFields(string repositoryFolder, string baseBranch, string taskSource, string settingsJson)
    {
        if (string.IsNullOrWhiteSpace(repositoryFolder) || !Directory.Exists(repositoryFolder))
        {
            yield return $"The repository folder does not exist: {repositoryFolder}";
        }
        else if (!Directory.Exists(Path.Combine(repositoryFolder, ".git")) && !File.Exists(Path.Combine(repositoryFolder, ".git")))
        {
            yield return $"{Path.GetFullPath(repositoryFolder)} is not the root of a git repository (no .git entry). Point to the top-level folder of the repository.";
        }

        if (string.IsNullOrWhiteSpace(baseBranch))
        {
            yield return "The base branch is empty.";
        }

        if (!TaskSources.Contains(taskSource, StringComparer.Ordinal))
        {
            yield return $"Task source \"{taskSource}\" is not known. Use \"local\" or \"azure-devops\".";
        }

        var settingsProblem = SettingsProblem(settingsJson);
        if (settingsProblem is not null)
        {
            yield return settingsProblem;
        }
    }

    private static string? SettingsProblem(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? null : "The project settings must be a JSON object.";
        }
        catch (JsonException ex)
        {
            return $"The project settings are not valid JSON: {ex.Message}";
        }
    }

    private static ProjectRecord Read(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex KeyPattern();
}
