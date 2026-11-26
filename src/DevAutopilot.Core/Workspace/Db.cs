using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DevAutopilot.Core.Workspace;

/// <summary>Small helpers over <see cref="SqliteCommand"/> so the services stay short.</summary>
internal static class Db
{
    public static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static int Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        return command.ExecuteNonQuery();
    }

    public static List<T> Query<T>(SqliteConnection connection, Func<SqliteDataReader, T> read, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        using var reader = command.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
        {
            list.Add(read(reader));
        }

        return list;
    }

    public static string Stamp(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseStamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? ParseStampOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ParseStamp(reader.GetString(ordinal));

    public static string? StringOrNull(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
