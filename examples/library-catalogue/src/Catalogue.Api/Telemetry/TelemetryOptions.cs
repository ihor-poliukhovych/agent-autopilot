namespace Catalogue.Api.Telemetry;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>File that request records are appended to, one JSON object per line. Empty turns telemetry off.</summary>
    public string? Path { get; set; }
}
