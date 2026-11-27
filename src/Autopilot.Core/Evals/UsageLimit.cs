using System.Globalization;
using System.Text.RegularExpressions;

namespace Autopilot.Core.Evals;

/// <summary>
/// The subscription's usage limit as its own failure class: not a task failure, so it never parks a task or
/// uses a retry; the pool pauses until the limit resets.
/// </summary>
public static partial class UsageLimit
{
    private static readonly string[] Phrases = ["usage limit", "hit your limit", "limit will reset"];

    /// <summary>True when <paramref name="message"/> says the subscription's usage limit is reached.</summary>
    public static bool IsUsageLimit(string? message) =>
        !string.IsNullOrWhiteSpace(message) && Phrases.Any(p => message.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The reset time in the message as UTC, or null when there is none or it cannot be read. Local times
    /// without a zone use the clock's time zone; a time of day that has passed today means tomorrow.
    /// </summary>
    public static DateTimeOffset? ParseResetTime(string? message, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var now = clock.GetUtcNow();

        // "Claude AI usage limit reached|1764000000"
        if (EpochPattern().Match(message) is { Success: true } epoch
            && long.TryParse(epoch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            try
            {
                var at = epoch.Groups[1].Value.Length >= 12 ? DateTimeOffset.FromUnixTimeMilliseconds(seconds) : DateTimeOffset.FromUnixTimeSeconds(seconds);
                return at > now ? at : null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        // "limit will reset at 2026-11-28 05:00 UTC"
        if (IsoPattern().Match(message) is { Success: true } iso
            && DateTime.TryParse($"{iso.Groups[1].Value} {iso.Groups[2].Value}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
        {
            var zone = iso.Groups[3].Success ? TimeZoneInfo.Utc : clock.LocalTimeZone;
            var at = new DateTimeOffset(stamp, zone.GetUtcOffset(stamp)).ToUniversalTime();
            return at > now ? at : null;
        }

        // "resets 5pm", "resets at 3:30pm (Europe/Berlin)", "will reset at 17:00 UTC"
        if (TimePattern().Match(message) is { Success: true } time)
        {
            var hasMinutes = time.Groups["m"].Success;
            var meridiem = time.Groups["ap"].Value.ToLowerInvariant();
            if (!hasMinutes && meridiem.Length == 0)
            {
                return null;
            }

            var hour = int.Parse(time.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minute = hasMinutes ? int.Parse(time.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            if (meridiem.Length > 0)
            {
                if (hour is < 1 or > 12)
                {
                    return null;
                }

                hour = hour % 12 + (meridiem == "pm" ? 12 : 0);
            }

            if (hour > 23 || minute > 59)
            {
                return null;
            }

            var zone = clock.LocalTimeZone;
            if (time.Groups["zone"].Success)
            {
                var name = time.Groups["zone"].Value.Trim();
                if (name.Equals("UTC", StringComparison.OrdinalIgnoreCase) || name.Equals("GMT", StringComparison.OrdinalIgnoreCase))
                {
                    zone = TimeZoneInfo.Utc;
                }
                else
                {
                    try
                    {
                        zone = TimeZoneInfo.FindSystemTimeZoneById(name);
                    }
                    catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                    {
                        // Unknown zone name: fall back to the pool's own zone.
                    }
                }
            }

            var local = TimeZoneInfo.ConvertTime(now, zone);
            var candidate = local.Date.AddHours(hour).AddMinutes(minute);
            var result = new DateTimeOffset(candidate, zone.GetUtcOffset(candidate)).ToUniversalTime();
            if (result <= now)
            {
                candidate = candidate.AddDays(1);
                result = new DateTimeOffset(candidate, zone.GetUtcOffset(candidate)).ToUniversalTime();
            }

            return result;
        }

        return null;
    }

    [GeneratedRegex(@"limit reached\s*\|\s*(\d{9,13})\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpochPattern();

    [GeneratedRegex(@"reset\w*\s+(?:at\s+)?(\d{4}-\d{2}-\d{2})[T ](\d{2}:\d{2})(?::\d{2})?\s*(Z|UTC)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex IsoPattern();

    [GeneratedRegex(@"reset\w*\s+(?:at\s+)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm)?(?:\s*\((?<zone>[^)]+)\)|\s+(?<zone>UTC|GMT)\b)?", RegexOptions.IgnoreCase)]
    private static partial Regex TimePattern();
}
