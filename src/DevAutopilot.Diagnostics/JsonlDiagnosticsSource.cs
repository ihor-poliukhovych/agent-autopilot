using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DevAutopilot.Core;

namespace DevAutopilot.Diagnostics;

/// <summary>What one pass over the file found.</summary>
public sealed record JsonlReadResult(IReadOnlyList<DiagnosticEvent> Events, int SkippedLines);

/// <summary>
/// Reads diagnostic events from a JSON Lines file: one JSON object per line with the fields
/// <c>timestamp</c>, <c>level</c>, <c>message</c>, <c>exception</c>, <c>route</c>, <c>statusCode</c>
/// and <c>durationMs</c> (names match in any case). Unknown fields are ignored. A line that is not a
/// JSON object or has no readable timestamp is skipped and counted, never fatal.
/// </summary>
public sealed class JsonlDiagnosticsSource : IDiagnosticsSource
{
    public JsonlDiagnosticsSource(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    /// <summary>Creates the source for a path from the config, checked to be inside the project root.</summary>
    public static JsonlDiagnosticsSource ForProject(string projectRoot, string? configuredPath) =>
        new(DiagnosticsPath.Resolve(projectRoot, configuredPath));

    public string FilePath { get; }

    public async IAsyncEnumerable<DiagnosticEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = OpenReader();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (TryParseLine(line, out var item))
            {
                yield return item;
            }
        }
    }

    /// <summary>Reads the whole file and counts the non-blank lines that were skipped.</summary>
    public async Task<JsonlReadResult> ReadAllAsync(CancellationToken ct)
    {
        var events = new List<DiagnosticEvent>();
        var skipped = 0;
        using var reader = OpenReader();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParseLine(line, out var item))
            {
                events.Add(item);
            }
            else
            {
                skipped++;
            }
        }

        return new JsonlReadResult(events, skipped);
    }

    private StreamReader OpenReader()
    {
        if (!File.Exists(FilePath))
        {
            throw new FileNotFoundException($"The diagnostics file {FilePath} does not exist.", FilePath);
        }

        // The application may still be writing the log, so do not lock it.
        var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
    }

    /// <summary>Parses one line. Returns false for blank lines, bad JSON, non-objects and missing timestamps.</summary>
    public static bool TryParseLine(string? line, out DiagnosticEvent item)
    {
        item = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            DateTimeOffset? timestamp = null;
            string? level = null, message = null, exception = null, route = null;
            int? statusCode = null;
            double? durationMs = null;

            foreach (var property in root.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name.ToLowerInvariant())
                {
                    case "timestamp":
                        timestamp = ReadTimestamp(value);
                        break;
                    case "level":
                        level = ReadText(value);
                        break;
                    case "message":
                        message = ReadText(value);
                        break;
                    case "exception":
                        exception = ReadText(value);
                        break;
                    case "route":
                        route = ReadText(value);
                        break;
                    case "statuscode":
                        statusCode = ReadNumber(value) is { } code && code is >= 0 and <= 999 ? (int)code : null;
                        break;
                    case "durationms":
                        durationMs = ReadNumber(value) is { } ms && ms >= 0 && double.IsFinite(ms) ? ms : null;
                        break;
                }
            }

            if (timestamp is null)
            {
                return false;
            }

            item = new DiagnosticEvent(
                timestamp.Value,
                string.IsNullOrWhiteSpace(level) ? "Information" : level.Trim(),
                message ?? string.Empty,
                string.IsNullOrWhiteSpace(exception) ? null : exception,
                string.IsNullOrWhiteSpace(route) ? null : route.Trim(),
                statusCode,
                durationMs);
            return true;
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static string? ReadText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        // An exception logged as an object, or a number used as a level: keep its JSON text.
        _ => value.GetRawText(),
    };

    private static double? ReadNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var number) => number,
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };
}
