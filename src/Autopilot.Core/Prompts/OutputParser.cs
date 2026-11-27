using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Autopilot.Core.Prompts;

/// <summary>
/// Turns an agent's structured output into contract records. Uses the structured output when there is
/// one, otherwise the first JSON object found in the result text. Returns false with a reason on failure.
/// </summary>
public static class OutputParser
{
    public static bool TryParseTriage(AgentRunResult run, [NotNullWhen(true)] out TriageResult? result, out string error)
    {
        result = null;
        if (!TryGetObject(run, out var json, out error))
        {
            return false;
        }

        var label = ReadString(json, "label")?.Trim().ToLowerInvariant();
        TriageLabel? parsedLabel = label switch
        {
            "agent-ready" or "agent_ready" or "agentready" => TriageLabel.AgentReady,
            "needs-human" or "needs_human" or "needshuman" => TriageLabel.NeedsHuman,
            _ => null,
        };
        if (parsedLabel is null)
        {
            error = $"triage label \"{label}\" is not agent-ready or needs-human";
            return false;
        }

        var risk = ReadString(json, "risk")?.Trim().ToLowerInvariant() switch
        {
            "low" => RiskLevel.Low,
            "medium" => RiskLevel.Medium,
            _ => RiskLevel.High, // unknown risk is treated as the worst case
        };

        var criteria = ReadList(json, "acceptanceCriteria");
        var reason = ReadString(json, "reason") ?? string.Empty;

        // An agent-ready item without acceptance criteria cannot be checked; send it to a person.
        if (parsedLabel == TriageLabel.AgentReady && criteria.Count == 0)
        {
            parsedLabel = TriageLabel.NeedsHuman;
            reason = "Triage gave no acceptance criteria, so a person must write them. " + reason;
        }

        result = new TriageResult(parsedLabel.Value, reason.Trim(), criteria, risk);
        return true;
    }

    public static bool TryParseSelfReview(AgentRunResult run, [NotNullWhen(true)] out SelfReview? result, out string error)
    {
        result = null;
        if (!TryGetObject(run, out var json, out error))
        {
            return false;
        }

        var summary = ReadString(json, "summary");
        if (string.IsNullOrWhiteSpace(summary))
        {
            error = "self-review has no summary";
            return false;
        }

        result = new SelfReview(summary.Trim(), ReadList(json, "testsThatProveIt"), ReadList(json, "doubts"));
        return true;
    }

    public static bool TryParseReview(AgentRunResult run, [NotNullWhen(true)] out ReviewResult? result, out string error)
    {
        result = null;
        if (!TryGetObject(run, out var json, out error))
        {
            return false;
        }

        var verdict = ReadString(json, "verdict")?.Trim().ToLowerInvariant();
        ReviewVerdict? parsed = verdict switch
        {
            "approve" or "approved" => ReviewVerdict.Approve,
            "request-changes" or "request_changes" or "requestchanges" => ReviewVerdict.RequestChanges,
            _ => null,
        };
        if (parsed is null)
        {
            error = $"review verdict \"{verdict}\" is not approve or request-changes";
            return false;
        }

        result = new ReviewResult(parsed.Value, ReadList(json, "findings"));
        return true;
    }

    private static bool TryGetObject(AgentRunResult run, out JsonElement json, out string error)
    {
        error = string.Empty;
        if (run.StructuredOutput is { ValueKind: JsonValueKind.Object } structured)
        {
            json = structured;
            return true;
        }

        if (TryExtractObject(run.ResultText, out json))
        {
            return true;
        }

        error = run.Succeeded ? "the agent gave no JSON answer" : $"the agent run failed: {run.Error}";
        return false;
    }

    /// <summary>Finds the first JSON object in free text, for example inside a ```json block.</summary>
    public static bool TryExtractObject(string? text, out JsonElement json)
    {
        json = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        for (var start = text.IndexOf('{', StringComparison.Ordinal); start >= 0; start = text.IndexOf('{', start + 1))
        {
            var end = text.LastIndexOf('}');
            while (end > start)
            {
                try
                {
                    using var document = JsonDocument.Parse(text[start..(end + 1)]);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        json = document.RootElement.Clone();
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // Try a shorter candidate.
                }

                end = text.LastIndexOf('}', end - 1);
            }
        }

        return false;
    }

    private static string? ReadString(JsonElement json, string name) =>
        TryGetProperty(json, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string> ReadList(JsonElement json, string name)
    {
        if (!TryGetProperty(json, name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static bool TryGetProperty(JsonElement json, string name, out JsonElement value)
    {
        foreach (var property in json.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
