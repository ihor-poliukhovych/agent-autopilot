using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Autopilot.Web;

public sealed record FeedbackBody(string? Kind, string? Title, string? Description, bool IncludeContext, string? Page);

/// <summary>What the person will send: the redacted text for the preview and the prefilled "new issue" address.</summary>
public sealed record FeedbackPreview(string Url, string Title, string Text, string Template);

/// <summary>
/// Turns a feedback form into a prefilled GitHub "new issue" address for the tool's own repository, so it
/// needs no token. Secrets, local paths and the user name are removed before the text goes into the address.
/// </summary>
public static partial class Feedback
{
    public const string Repository = "https://github.com/ihor-poliukhovych/autopilot";

    /// <summary>Longer addresses are refused by GitHub or the browser; the longest field is cut until it fits.</summary>
    public const int MaxUrlLength = 6000;

    private const string BugTemplate = "bug_report.yml";
    private const string IdeaTemplate = "feature_request.yml";

    public static string Redact(string? text, IEnumerable<string>? privateWords = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = PrivateKeyBlock().Replace(text, "[redacted]");
        result = Assignment().Replace(result, m => m.Groups["name"].Value + m.Groups["sep"].Value + "[redacted]");
        result = Bearer().Replace(result, "$1[redacted]");
        result = KnownToken().Replace(result, "[redacted]");
        result = UrlCredentials().Replace(result, "$1[redacted]@");
        result = WindowsPath().Replace(result, "<path>");
        result = UnixPath().Replace(result, "<path>");
        foreach (var word in privateWords ?? [])
        {
            if (word.Length >= 3)
            {
                result = Regex.Replace(result, Regex.Escape(word), "<user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        return result;
    }

    public static FeedbackPreview Build(FeedbackBody body, string version, string os, IEnumerable<string>? privateWords = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        var words = privateWords?.ToList() ?? [];
        var kind = (body.Kind ?? "bug").Trim().ToLowerInvariant();
        if (kind is not ("bug" or "idea" or "question"))
        {
            throw new ArgumentException("The kind must be bug, idea or question.");
        }

        var title = Redact(body.Title, words).Trim().ReplaceLineEndings(" ");
        var description = Redact(body.Description, words).Trim();
        if (title.Length == 0)
        {
            throw new ArgumentException("Give the feedback a short title.");
        }

        if (description.Length == 0)
        {
            throw new ArgumentException("Describe what happened or what you want.");
        }

        var context = body.IncludeContext
            ? $"Autopilot {version}, {Redact(os, words)}" + (string.IsNullOrWhiteSpace(body.Page) ? string.Empty : $", page {Redact(body.Page, words).Trim()}")
            : string.Empty;
        var template = kind == "bug" ? BugTemplate : IdeaTemplate;
        var label = kind switch { "bug" => "bug", "idea" => "enhancement", _ => "question" };
        var prefix = kind == "question" ? "Question: " : string.Empty;

        // The field names are the ids in .github/ISSUE_TEMPLATE; GitHub fills a form field from the same-named parameter.
        var fields = kind == "bug"
            ? new List<(string Name, string Value)> { ("what", description), ("version", context) }
            : [("problem", kind == "question" ? "Question: " + description : description)];
        var text = new StringBuilder().AppendLine($"[{kind}] {prefix}{title}").AppendLine().AppendLine(description);
        if (context.Length > 0)
        {
            text.AppendLine().Append("Context: ").AppendLine(context);
        }

        var url = Compose(template, prefix + title, label, fields);
        while (url.Length > MaxUrlLength && fields.Any(f => f.Value.Length > 200))
        {
            var longest = fields.Select((f, i) => (f.Value.Length, i)).Max().i;
            var value = fields[longest].Value;
            fields[longest] = (fields[longest].Name, value[..(value.Length * 3 / 4)] + " ...[cut]");
            url = Compose(template, prefix + title, label, fields);
        }

        return new FeedbackPreview(url, prefix + title, text.ToString().TrimEnd(), template);
    }

    internal static string Compose(string template, string title, string label, IEnumerable<(string Name, string Value)> fields)
    {
        var url = new StringBuilder(Repository).Append("/issues/new?template=").Append(Uri.EscapeDataString(template))
            .Append("&title=").Append(Uri.EscapeDataString(title))
            .Append("&labels=").Append(Uri.EscapeDataString(label));
        foreach (var (name, value) in fields)
        {
            if (value.Length > 0)
            {
                url.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
            }
        }

        return url.ToString();
    }

    internal static string OperatingSystem => RuntimeInformation.OSDescription;

    internal static IEnumerable<string> LocalNames() => [Environment.UserName, Environment.MachineName];

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)")]
    private static partial Regex PrivateKeyBlock();

    // password=..., "token": "...", AUTOPILOT_ADO_PAT=..., client_secret: ...
    [GeneratedRegex(@"(?<name>[\w.-]*(?:password|passwd|secret|token|api[_-]?key|apikey|credential|_pat)[\w.-]*[""']?)(?<sep>\s*[:=]\s*[""']?)[^\s""',;&]+", RegexOptions.IgnoreCase)]
    private static partial Regex Assignment();

    [GeneratedRegex(@"(\b(?:Bearer|Basic)\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    // GitHub, Slack, AWS, OpenAI style keys and JWTs.
    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{20,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{5,})")]
    private static partial Regex KnownToken();

    [GeneratedRegex(@"(://)[^\s/@:]+(?::[^\s/@]*)?@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"\b[A-Za-z]:[\\/][^\s""'<>|*?]+|\\\\[\w.$-]+\\[^\s""'<>|*?]+")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"(?<![\w/.])/(?:home|Users|root|var|tmp|etc|opt|mnt)/[^\s""'<>|*?:]+")]
    private static partial Regex UnixPath();
}
