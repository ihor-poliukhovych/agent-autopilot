using System.Text.RegularExpressions;

namespace Autopilot.Core.Tasks;

/// <summary>A question split into its text, the answers a person can pick and the one that is recommended (0-based).</summary>
public sealed record ParsedQuestion(string Text, IReadOnlyList<string> Options, int? Recommended);

/// <summary>
/// Reads the options and the recommendation of a needs-human question. Triage returns them as fields; results
/// stored before that only have prose such as "(A) keep it, (B) remove it. I recommend B because ...", which
/// <see cref="FromProse"/> parses. When the prose has no clear options the question stays whole.
/// </summary>
public static partial class TriageQuestion
{
    /// <summary>Uses the structured options when there are two or more, else the prose of <paramref name="reason"/>.</summary>
    public static ParsedQuestion Resolve(string reason, IReadOnlyList<string>? options, int? recommended)
    {
        var prose = FromProse(reason);
        if (options is not { Count: >= 2 })
        {
            return prose;
        }

        int? index = recommended is { } n && n >= 1 && n <= options.Count ? n - 1 : null;
        // The reason still lists the options inline; show only the question when the prose split it.
        return new ParsedQuestion(prose.Options.Count >= 2 ? prose.Text : reason.Trim(), options, index);
    }

    public static ParsedQuestion FromProse(string? reason)
    {
        var text = (reason ?? string.Empty).Trim();
        var markers = Markers(text);
        if (markers.Count < 2)
        {
            return new ParsedQuestion(text, [], null);
        }

        var recommendation = RecommendStart().Match(text, markers[^1].End);
        var options = new List<string>();
        for (var i = 0; i < markers.Count; i++)
        {
            var start = markers[i].End;
            var end = i + 1 < markers.Count ? markers[i + 1].Start : recommendation.Success ? recommendation.Index : text.Length;
            var option = CleanOption(text[start..Math.Max(start, end)], last: i == markers.Count - 1);
            if (option.Length == 0)
            {
                return new ParsedQuestion(text, [], null);
            }

            options.Add(option);
        }

        int? recommended = null;
        var letter = RecommendLetter().Match(text, markers[0].Start);
        if (letter.Success)
        {
            var at = LetterIndex(letter.Groups[1].Value);
            recommended = at >= 0 && at < options.Count ? at : null;
        }

        var stem = text[..markers[0].Start].Trim();
        return new ParsedQuestion(stem.Length > 0 ? stem : text, options, recommended);
    }

    private sealed record Marker(int Start, int End, string Key);

    /// <summary>The run of markers (A), (B), (C)... or (1), (2)... that counts up from the first one.</summary>
    private static List<Marker> Markers(string text)
    {
        var found = new List<Marker>();
        foreach (Match m in MarkerPattern().Matches(text))
        {
            var key = m.Groups[1].Value.ToUpperInvariant();
            var wanted = found.Count == 0 ? null : ((char)(found[^1].Key[0] + 1)).ToString();
            var accepted = found.Count == 0 ? key is "A" or "1" : key == wanted;
            if (accepted)
            {
                found.Add(new Marker(m.Index, m.Index + m.Length, key));
            }
        }

        return found;
    }

    private static int LetterIndex(string key) => char.IsDigit(key[0]) ? key[0] - '1' : char.ToUpperInvariant(key[0]) - 'A';

    private static string CleanOption(string raw, bool last)
    {
        var option = raw.Trim();
        if (last)
        {
            // The last option runs to the end of its sentence; what follows is a comment.
            var stop = SentenceEnd().Match(option);
            if (stop.Success)
            {
                option = option[..stop.Index];
            }
        }

        option = TrailingJoin().Replace(option, string.Empty).Trim();
        return option.TrimEnd('.', ';', ',', ' ').Trim();
    }

    [GeneratedRegex(@"\(([A-Za-z]|[1-9])\)\s*")]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(@"(?:(?<=[.!?;])\s+|\n+|,\s+|\s+[—-]\s+)(?=(?:(?:I|We)\s+(?:would\s+)?|My\s+)?(?i:recommend|suggest|prefer))")]
    private static partial Regex RecommendStart();

    [GeneratedRegex(@"(?i:recommend\w*|suggest\w*|prefer\w*)\s*:?\s*(?i:option\s+)?\(?([A-Z]|[1-9])\)?(?=[\s.,:;)!?]|$)")]
    private static partial Regex RecommendLetter();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z])")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"(?:[,;]\s*|\s+)(?:or|and)\s*$|[,;]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingJoin();
}
