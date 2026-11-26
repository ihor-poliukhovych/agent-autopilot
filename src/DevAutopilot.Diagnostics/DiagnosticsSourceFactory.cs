using DevAutopilot.Core.Config;
using DevAutopilot.Core.Diagnostics;

namespace DevAutopilot.Diagnostics;

/// <summary>Builds the diagnostics source named in <c>modules.diagnostics</c>.</summary>
public static class DiagnosticsSourceFactory
{
    /// <summary>
    /// Returns the configured source, or null when the module is "none".
    /// Throws <see cref="DiagnosticsConfigException"/> for an unknown type or a bad path.
    /// </summary>
    public static JsonlDiagnosticsSource? Create(string projectRoot, ModuleConfig module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (string.Equals(module.Type, DiagnosticsMcp.NoneType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!DiagnosticsMcp.IsEnabled(module))
        {
            throw new DiagnosticsConfigException(
                $"modules.diagnostics.type \"{module.Type}\" is not supported. Use \"{DiagnosticsMcp.JsonlType}\" or \"{DiagnosticsMcp.NoneType}\".");
        }

        var path = module.Settings
            .FirstOrDefault(s => string.Equals(s.Key, DiagnosticsMcp.PathSetting, StringComparison.OrdinalIgnoreCase))
            .Value;
        return JsonlDiagnosticsSource.ForProject(projectRoot, path);
    }
}
