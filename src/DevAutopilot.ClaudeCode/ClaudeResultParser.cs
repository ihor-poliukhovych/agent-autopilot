using System.Text.Json;
using DevAutopilot.Core;

namespace DevAutopilot.ClaudeCode;

/// <summary>Turns the single JSON object of <c>claude -p --output-format json</c> into an <see cref="AgentRunResult"/>.</summary>
public static class ClaudeResultParser
{
    public const int MaxRawOutputInError = 2000;

    public static AgentRunResult Parse(
        string stdout,
        string stderr,
        int exitCode,
        TimeSpan duration,
        bool expectStructuredOutput)
    {
        stdout ??= string.Empty;
        stderr ??= string.Empty;

        JsonDocument? document = TryParseResultObject(stdout);
        if (document is null)
        {
            string raw = stdout.Trim().Length > 0 ? stdout : stderr;
            string what = stdout.Trim().Length > 0 ? "output that is not a Claude Code JSON result" : "no output";
            return Failure(
                $"Claude Code exited with code {exitCode} and {what}. First {MaxRawOutputInError} characters: {Truncate(raw)}",
                duration);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            bool isError = GetBool(root, "is_error") ?? true;
            string subtype = GetString(root, "subtype") ?? string.Empty;
            string resultText = GetString(root, "result") ?? string.Empty;
            int turns = (int)(GetLong(root, "num_turns") ?? 0);
            TokenUsage usage = ParseUsage(root);
            string? model = ParseModel(root);

            JsonElement? structured = root.TryGetProperty("structured_output", out JsonElement so)
                && so.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    ? so.Clone()
                    : null;

            string? error = null;
            if (isError || !string.Equals(subtype, "success", StringComparison.Ordinal))
            {
                string terminal = GetString(root, "terminal_reason") is { Length: > 0 } t ? $", {t}" : string.Empty;
                error = $"Claude Code reported an error ({(subtype.Length > 0 ? subtype : "no subtype")}{terminal}): {Truncate(resultText)}";
            }
            else if (exitCode != 0)
            {
                error = $"Claude Code exited with code {exitCode}. {Truncate(stderr)}".TrimEnd();
            }
            else if (expectStructuredOutput && structured is null)
            {
                error = "Claude Code finished without structured_output although a JSON schema was given.";
            }

            return new AgentRunResult(
                error is null,
                resultText,
                structured,
                turns,
                usage,
                duration,
                model,
                error);
        }
    }

    public static AgentRunResult Failure(string error, TimeSpan duration) =>
        new(false, string.Empty, null, 0, TokenUsage.Zero, duration, null, error);

    private static JsonDocument? TryParseResultObject(string stdout)
    {
        string text = stdout.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        JsonDocument? whole = TryParse(text);
        if (whole is not null)
        {
            if (IsResult(whole.RootElement))
            {
                return whole;
            }

            whole.Dispose();
        }

        // Tolerate stray lines (warnings) around the JSON object: take the last line that is a result.
        foreach (string line in text.Split('\n').Reverse())
        {
            JsonDocument? doc = TryParse(line.Trim());
            if (doc is null)
            {
                continue;
            }

            if (IsResult(doc.RootElement))
            {
                return doc;
            }

            doc.Dispose();
        }

        return null;
    }

    private static bool IsResult(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && (GetString(root, "type") == "result" || root.TryGetProperty("is_error", out _));

    private static JsonDocument? TryParse(string text)
    {
        if (!text.StartsWith('{'))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TokenUsage ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return TokenUsage.Zero;
        }

        return new TokenUsage(
            GetLong(usage, "input_tokens") ?? 0,
            GetLong(usage, "output_tokens") ?? 0,
            GetLong(usage, "cache_read_input_tokens") ?? 0,
            GetLong(usage, "cache_creation_input_tokens") ?? 0);
    }

    /// <summary>
    /// The model that did most of the work: a top-level <c>model</c> when present, otherwise the
    /// <c>modelUsage</c> entry with the most output tokens.
    /// </summary>
    private static string? ParseModel(JsonElement root)
    {
        if (GetString(root, "model") is { Length: > 0 } model)
        {
            return model;
        }

        if (!root.TryGetProperty("modelUsage", out JsonElement models) || models.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return models.EnumerateObject()
            .OrderByDescending(p => p.Value.ValueKind == JsonValueKind.Object ? GetLong(p.Value, "outputTokens") ?? 0 : 0)
            .Select(p => p.Name)
            .FirstOrDefault();
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static long? GetLong(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n)
            ? n
            : null;

    private static string Truncate(string text) =>
        text.Length <= MaxRawOutputInError ? text : text[..MaxRawOutputInError];
}
