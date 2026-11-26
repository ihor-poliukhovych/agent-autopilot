using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevAutopilot.Core;
using ModelContextProtocol;

namespace DevAutopilot.Diagnostics;

/// <summary>
/// The four read-only diagnostics tools. Each call reads the source again, so the answers follow the
/// log as it grows. Every answer is a JSON object with at most <see cref="MaxItems"/> items and long
/// text cut at a fixed length, so one call can never flood the agent's context.
/// </summary>
public sealed class DiagnosticsTools(IDiagnosticsSource source)
{
    public const int MaxItems = 50;
    public const int DefaultLimit = 20;
    public const int MaxSamples = 3;
    public const int MaxMessageLength = 500;
    public const int MaxExceptionLength = 4000;
    public const int MaxFirstLineLength = 300;

    /// <summary>Exception type used for error events that carry no exception text.</summary>
    public const string NoException = "(no exception)";

    private static readonly string[] ErrorLevels = ["error", "err", "critical", "crit", "fatal"];

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IDiagnosticsSource _source = source ?? throw new ArgumentNullException(nameof(source));

    [Description("Lists recent errors grouped by exception type and route, most frequent first: count, first and last time seen, and one sample message. An error is an event with level Error, Critical or Fatal, or with an exception. Read-only.")]
    public async Task<string> ListRecentErrorsAsync(
        [Description("Only errors from the last N hours, counted back from the newest event in the log (not from the current time, so an old log still works). Omit for all errors.")] int? sinceHours = null,
        [Description("Maximum number of groups to return, 1 to 50. Default 20.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (sinceHours is <= 0)
        {
            throw new McpException("sinceHours must be a positive number of hours.");
        }

        var events = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var newest = events.Count == 0 ? (DateTimeOffset?)null : events.Max(e => e.Timestamp);
        var from = sinceHours is int hours && newest is { } end ? end.AddHours(-hours) : (DateTimeOffset?)null;

        var errors = events.Where(IsError).Where(e => from is null || e.Timestamp >= from).ToList();
        var groups = errors
            .GroupBy(e => (Type: ExceptionType(e), Route: e.Route ?? string.Empty))
            .Select(g => new ErrorGroup(
                g.Key.Type,
                g.Key.Route.Length == 0 ? null : g.Key.Route,
                g.Count(),
                g.Min(e => e.Timestamp),
                g.Max(e => e.Timestamp),
                Truncate(g.MaxBy(e => e.Timestamp)!.Message, MaxMessageLength)))
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.LastSeen)
            .ThenBy(g => g.ExceptionType, StringComparer.Ordinal)
            .ToList();

        var take = Clamp(limit);
        return Serialize(new
        {
            errorEvents = errors.Count,
            groupCount = groups.Count,
            returned = Math.Min(take, groups.Count),
            window = new { from, to = newest },
            groups = groups.Take(take),
        });
    }

    [Description("Shows up to 3 recent full samples (message, route, status code, duration and stack trace) of one error group. Use an exceptionType from list_recent_errors. Read-only.")]
    public async Task<string> GetErrorDetailsAsync(
        [Description("Exception type, full (System.InvalidOperationException) or short (InvalidOperationException), as list_recent_errors shows it. Case does not matter.")] string exceptionType,
        [Description("Only errors on this route, for example GET /books/{id}. Omit for every route.")] string? route = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exceptionType))
        {
            throw new McpException("exceptionType is required. Call list_recent_errors to see the types.");
        }

        var wanted = exceptionType.Trim();
        var events = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var matches = events
            .Where(IsError)
            .Where(e => TypeMatches(ExceptionType(e), wanted))
            .Where(e => string.IsNullOrWhiteSpace(route) || string.Equals(e.Route, route.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        return Serialize(new
        {
            exceptionType = wanted,
            route = string.IsNullOrWhiteSpace(route) ? null : route.Trim(),
            totalMatches = matches.Count,
            samples = matches.Take(MaxSamples).Select(e => new
            {
                timestamp = e.Timestamp,
                level = e.Level,
                route = e.Route,
                statusCode = e.StatusCode,
                durationMs = e.DurationMs,
                message = Truncate(e.Message, MaxMessageLength),
                exception = e.Exception is null ? null : Truncate(e.Exception, MaxExceptionLength),
            }),
        });
    }

    [Description("Finds log events whose message or exception contains the text (case does not matter), newest first. Read-only.")]
    public async Task<string> SearchLogsAsync(
        [Description("Text to look for in the message and the exception.")] string query,
        [Description("Only events with this level, for example Error or Warning. Case does not matter. Omit for every level.")] string? level = null,
        [Description("Maximum number of events to return, 1 to 50. Default 20.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpException("query is required.");
        }

        var events = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var matches = events
            .Where(e => string.IsNullOrWhiteSpace(level) || string.Equals(e.Level, level.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (e.Exception?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        var take = Clamp(limit);
        return Serialize(new
        {
            query,
            level = string.IsNullOrWhiteSpace(level) ? null : level.Trim(),
            totalMatches = matches.Count,
            returned = Math.Min(take, matches.Count),
            events = matches.Take(take).Select(e => new
            {
                timestamp = e.Timestamp,
                level = e.Level,
                route = e.Route,
                statusCode = e.StatusCode,
                message = Truncate(e.Message, MaxMessageLength),
                exception = e.Exception is null ? null : Truncate(FirstLine(e.Exception), MaxFirstLineLength),
            }),
        });
    }

    [Description("Request statistics per route, busiest first: request count, failed requests and error rate (status code 500 or higher, or an error without a status code), and p50/p95 duration in milliseconds. Read-only.")]
    public async Task<string> GetEndpointStatsAsync(
        [Description("Only this route, for example GET /books/{id}. Case does not matter. Omit for every route.")] string? route = null,
        CancellationToken cancellationToken = default)
    {
        var events = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var routes = events
            .Where(e => e.Route is not null)
            .Where(e => string.IsNullOrWhiteSpace(route) || string.Equals(e.Route, route.Trim(), StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Route!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var count = g.Count();
                var failed = g.Count(IsFailedRequest);
                var durations = g.Where(e => e.DurationMs is not null).Select(e => e.DurationMs!.Value).Order().ToList();
                return new EndpointStats(
                    g.Key,
                    count,
                    failed,
                    Math.Round((double)failed / count, 4),
                    Percentile(durations, 0.50),
                    Percentile(durations, 0.95));
            })
            .OrderByDescending(s => s.Count)
            .ThenBy(s => s.Route, StringComparer.Ordinal)
            .ToList();

        return Serialize(new
        {
            route = string.IsNullOrWhiteSpace(route) ? null : route.Trim(),
            routeCount = routes.Count,
            returned = Math.Min(MaxItems, routes.Count),
            routes = routes.Take(MaxItems),
        });
    }

    public sealed record ErrorGroup(
        string ExceptionType, string? Route, int Count, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string SampleMessage);

    public sealed record EndpointStats(
        string Route, int Count, int Errors, double ErrorRate, double? P50DurationMs, double? P95DurationMs);

    public static bool IsError(DiagnosticEvent e) =>
        e.Exception is not null || ErrorLevels.Contains(e.Level, StringComparer.OrdinalIgnoreCase);

    public static bool IsFailedRequest(DiagnosticEvent e) =>
        e.StatusCode is int code ? code >= 500 : IsError(e);

    /// <summary>
    /// The exception type from the exception text: the part of the first line before ':' when it looks
    /// like a type name ("System.InvalidOperationException: ..."), else the first line itself.
    /// </summary>
    public static string ExceptionType(DiagnosticEvent e)
    {
        if (e.Exception is null)
        {
            return NoException;
        }

        if (TypeFromJson(e.Exception) is { } jsonType)
        {
            return Truncate(jsonType, 200);
        }

        var first = FirstLine(e.Exception);
        var colon = first.IndexOf(':', StringComparison.Ordinal);
        var candidate = colon > 0 ? first[..colon].Trim() : first;
        if (candidate.Length == 0 || candidate.Any(char.IsWhiteSpace))
        {
            candidate = first;
        }

        return Truncate(candidate, 200);
    }

    /// <summary>An exception logged as a JSON object: its "type" (or "className") field.</summary>
    private static string? TypeFromJson(string exception)
    {
        if (!exception.TrimStart().StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(exception);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if ((property.Name.Equals("type", StringComparison.OrdinalIgnoreCase)
                     || property.Name.Equals("className", StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return property.Value.GetString()!.Trim();
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON after all; the first-line rule applies.
        }

        return null;
    }

    private static bool TypeMatches(string actual, string wanted) =>
        string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase)
        || actual.EndsWith("." + wanted, StringComparison.OrdinalIgnoreCase);

    private static double? Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return null;
        }

        // Nearest rank: the smallest value with at least p of the values at or below it.
        var rank = (int)Math.Ceiling(p * sorted.Count);
        return Math.Round(sorted[Math.Clamp(rank, 1, sorted.Count) - 1], 2);
    }

    private static int Clamp(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxItems);

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return (end < 0 ? text : text[..end]).Trim();
    }

    public static string Truncate(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max), $"... ({text.Length - max} more characters)");

    private async Task<List<DiagnosticEvent>> LoadAsync(CancellationToken ct)
    {
        try
        {
            var events = new List<DiagnosticEvent>();
            await foreach (var item in _source.ReadAsync(ct).ConfigureAwait(false))
            {
                events.Add(item);
            }

            return events;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"The diagnostics source could not be read: {ex.Message}");
        }
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
