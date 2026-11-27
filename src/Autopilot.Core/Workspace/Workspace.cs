using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Autopilot.Core.Workspace;

/// <summary>
/// The per-user folder that holds everything Autopilot knows about its projects: one SQLite
/// database (<c>autopilot.db</c>) and a <c>projects/&lt;key&gt;/</c> folder per project. It is never inside a repository.
/// </summary>
public sealed class Workspace
{
    public const string EnvironmentVariable = "AUTOPILOT_WORKSPACE";
    public const string DefaultFolderName = ".autopilot";
    public const string DatabaseFileName = "autopilot.db";

    private const string MigrationPrefix = "Autopilot.Core.Migrations.";

    public Workspace(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string DatabasePath => Path.Combine(Root, DatabaseFileName);

    public string ProjectsFolder => Path.Combine(Root, "projects");

    /// <summary>The folder of one project's settings, tasks and run output.</summary>
    public string ProjectFolder(string key) => Path.Combine(ProjectsFolder, key);

    /// <summary>The explicit path, else <c>AUTOPILOT_WORKSPACE</c>, else <c>.autopilot</c> in the user profile.</summary>
    public static string ResolveRoot(string? explicitPath, Func<string, string?>? getEnvironment = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var fromEnvironment = (getEnvironment ?? Environment.GetEnvironmentVariable)(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultFolderName);
    }

    /// <summary>
    /// Like <see cref="ResolveRoot"/>. When the default location is used and only the folder of the old product
    /// name exists, it is moved first (see <see cref="LegacyWorkspaceMigration"/>); <paramref name="notice"/> gets
    /// the message for the person, if any.
    /// </summary>
    public static Workspace Resolve(string? explicitPath, Func<string, string?>? getEnvironment = null, Action<string>? notice = null, string? userProfile = null)
    {
        var root = ResolveRoot(explicitPath, getEnvironment);
        var profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(explicitPath)
            && string.IsNullOrWhiteSpace((getEnvironment ?? Environment.GetEnvironmentVariable)(EnvironmentVariable))
            && !string.IsNullOrWhiteSpace(profile))
        {
            var result = LegacyWorkspaceMigration.Apply(profile);
            if (result.Message is not null)
            {
                notice?.Invoke(result.Message);
            }

            if (result.Outcome != LegacyMigrationOutcome.NotNeeded)
            {
                root = result.Root;
            }
        }

        return new Workspace(root);
    }

    /// <summary>Opens the database, creating the folder and applying the missing migrations first.</summary>
    public SqliteConnection Open()
    {
        Directory.CreateDirectory(Root);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        try
        {
            Migrate(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    /// <summary>The highest migration number this build knows.</summary>
    public static int LatestVersion => Migrations().Max(m => m.Version);

    /// <summary>Applies numbered scripts (<c>001-name.sql</c>) not applied yet, each in one transaction.</summary>
    public static void Migrate(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }

        int current;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
            current = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var known = Migrations();
        if (current > known.Max(m => m.Version))
        {
            throw new WorkspaceException($"The workspace database is version {current}, newer than this Autopilot understands. Update Autopilot.");
        }

        foreach (var migration in known.Where(m => m.Version > current))
        {
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_version (version, name, applied_at) VALUES ($v, $n, $t)";
                record.Parameters.AddWithValue("$v", migration.Version);
                record.Parameters.AddWithValue("$n", migration.Name);
                record.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private static List<(int Version, string Name, string Sql)> Migrations()
    {
        var assembly = typeof(Workspace).Assembly;
        var list = new List<(int Version, string Name, string Sql)>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(MigrationPrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)))
        {
            var file = resource[MigrationPrefix.Length..];
            var number = file.Split('-')[0];
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException($"Migration {file} must start with a number.");
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            list.Add((version, file[..^4], reader.ReadToEnd()));
        }

        return list.OrderBy(m => m.Version).ToList();
    }
}

/// <summary>The workspace cannot be used; the message says why.</summary>
public sealed class WorkspaceException(string message) : Exception(message);
