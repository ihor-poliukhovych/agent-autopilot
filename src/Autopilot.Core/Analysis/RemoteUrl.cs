using System.Text.RegularExpressions;

namespace Autopilot.Core.Analysis;

/// <summary>A git remote, with user names and passwords removed from its address.</summary>
/// <param name="Name">The remote's name, for example origin.</param>
/// <param name="Url">The address without credentials.</param>
/// <param name="Kind"><c>azure-devops</c>, <c>github</c> or <c>other</c>.</param>
/// <param name="OrganizationUrl">Azure DevOps only: the organisation's https address.</param>
/// <param name="Project">Azure DevOps: the project; GitHub: the owner.</param>
/// <param name="Repository">The repository name, without ".git".</param>
public sealed record RemoteInfo(string Name, string Url, string Kind, string? OrganizationUrl, string? Project, string? Repository);

/// <summary>Reads the address of a git remote: Azure DevOps (https and ssh, old and new host names), GitHub, anything else.</summary>
public static partial class RemoteUrl
{
    public const string AzureDevOps = "azure-devops";
    public const string GitHub = "github";
    public const string Other = "other";

    public static RemoteInfo Parse(string name, string raw)
    {
        var text = (raw ?? string.Empty).Trim();
        var (host, segments) = Split(text);
        var clean = Clean(text, host, segments);
        var lower = host.ToLowerInvariant();

        var git = Index(segments, "_git");
        if (lower is "dev.azure.com" && git >= 1 && git + 1 < segments.Count)
        {
            // https://dev.azure.com/{org}/{project}/_git/{repo}, or /{org}/_git/{repo} (project = repository)
            var org = segments[0];
            var project = git >= 2 ? segments[1] : segments[git + 1];
            return new RemoteInfo(name, clean, AzureDevOps, $"https://dev.azure.com/{org}", project, Repo(segments[git + 1]));
        }

        if ((lower is "ssh.dev.azure.com" or "vs-ssh.visualstudio.com") && segments.Count >= 4 && segments[0] == "v3")
        {
            return new RemoteInfo(name, clean, AzureDevOps, $"https://dev.azure.com/{segments[1]}", segments[2], Repo(segments[3]));
        }

        var vs = git;
        if (lower.EndsWith(".visualstudio.com", StringComparison.Ordinal) && lower != "vs-ssh.visualstudio.com" && vs >= 0 && vs + 1 < segments.Count)
        {
            // https://{org}.visualstudio.com[/DefaultCollection]/{project}/_git/{repo}
            var org = lower[..^".visualstudio.com".Length];
            var before = segments.Take(vs).Where(s => !s.Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase)).ToList();
            var project = before.Count > 0 ? before[0] : segments[vs + 1];
            return new RemoteInfo(name, clean, AzureDevOps, $"https://{org}.visualstudio.com", project, Repo(segments[vs + 1]));
        }

        if ((lower is "github.com" or "www.github.com") && segments.Count >= 2)
        {
            return new RemoteInfo(name, clean, GitHub, null, segments[0], Repo(segments[1]));
        }

        return new RemoteInfo(name, clean, Other, null, null, segments.LastOrDefault(x => x is not "." and not "..") is { } last ? Repo(last) : null);
    }

    private static int Index(List<string> segments, string value) => segments.FindIndex(s => s.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static string Repo(string segment) => segment.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segment[..^4] : segment;

    private static string Clean(string text, string host, List<string> segments) =>
        host.Length == 0 ? text : $"{(text.Contains("://", StringComparison.Ordinal) ? text[..text.IndexOf("://", StringComparison.Ordinal)] : "ssh")}://{host}/{string.Join('/', segments)}";

    /// <summary>Splits an address into host and decoded path segments, whatever its form (https, ssh://, user@host:path).</summary>
    private static (string Host, List<string> Segments) Split(string text)
    {
        string host;
        string path;
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var rest = text[(scheme + 3)..];
            var slash = rest.IndexOf('/');
            var authority = slash < 0 ? rest : rest[..slash];
            path = slash < 0 ? string.Empty : rest[(slash + 1)..];
            host = authority[(authority.LastIndexOf('@') + 1)..];
            var colon = host.IndexOf(':');
            host = colon >= 0 ? host[..colon] : host;
        }
        else if (ScpLike().Match(text) is { Success: true } scp)
        {
            host = scp.Groups["host"].Value;
            path = scp.Groups["path"].Value;
        }
        else
        {
            return (string.Empty, [.. text.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)]);
        }

        var q = path.IndexOfAny(['?', '#']);
        path = q >= 0 ? path[..q] : path;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        return (host, segments);
    }

    [GeneratedRegex(@"^(?:[^@/\s]+@)?(?<host>[A-Za-z0-9.\-]{2,}):(?<path>.+)$")]
    private static partial Regex ScpLike();
}
