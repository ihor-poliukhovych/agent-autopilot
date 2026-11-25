using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Catalogue.Api.Telemetry;

/// <summary>One request, as written to the telemetry file.</summary>
public sealed record TelemetryRecord(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string Method,
    string Route,
    int StatusCode,
    double DurationMs,
    string? Exception);

/// <summary>Appends <see cref="TelemetryRecord"/>s as JSON lines. Does nothing when no path is configured.</summary>
public sealed class TelemetryWriter(IOptions<TelemetryOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private readonly string? _path = string.IsNullOrWhiteSpace(options.Value.Path) ? null : options.Value.Path;

    public bool Enabled => _path is not null;

    public void Write(TelemetryRecord record)
    {
        if (_path is null)
        {
            return;
        }

        var line = JsonSerializer.Serialize(record, JsonOptions) + "\n";
        lock (_gate)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.AppendAllText(_path, line);
        }
    }
}
