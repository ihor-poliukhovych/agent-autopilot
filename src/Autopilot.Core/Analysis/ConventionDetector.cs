using System.Text.Json;
using System.Text.RegularExpressions;

namespace Autopilot.Core.Analysis;

/// <summary>One inferred convention: the value, how sure we are (0 to 1), the share of samples that follow it and up to 3 example lines.</summary>
public sealed record ConventionFinding(string Value, double Confidence, IReadOnlyList<string> Examples, double? Share = null);

/// <summary>Everything the detector reads. All lists may be empty.</summary>
/// <param name="Branches">Remote branch names without the remote prefix.</param>
/// <param name="CommitSubjects">Recent commit subjects, newest first.</param>
/// <param name="PullRequestTitles">Titles from the tracker, or null when it cannot list them.</param>
/// <param name="Files">Repository file paths with forward slashes.</param>
/// <param name="DefaultBranch">What the remote HEAD points at, when known.</param>
/// <param name="RemoteUrl">The origin URL, used as a hint for the tracker.</param>
/// <param name="PackageJson">Text of the root package.json, when there is one.</param>
public sealed record ConventionInput(
    IReadOnlyList<string> Branches,
    IReadOnlyList<string> CommitSubjects,
    IReadOnlyList<string>? PullRequestTitles,
    IReadOnlyList<string> Files,
    string? DefaultBranch = null,
    string? RemoteUrl = null,
    string? PackageJson = null);

/// <summary>The proposal. When <see cref="HasHistory"/> is false the history based findings are the defaults.</summary>
public sealed record ConventionProposal(
    bool HasHistory,
    string? Note,
    ConventionFinding BaseBranch,
    ConventionFinding BranchPattern,
    ConventionFinding CommitPattern,
    ConventionFinding PullRequestTitlePattern,
    ConventionFinding TaskIdPattern,
    ConventionFinding TaskTracker,
    ConventionFinding BuildCommand,
    ConventionFinding TestCommand,
    ConventionFinding WritablePaths,
    ConventionFinding RiskyPaths);

/// <summary>Infers a project's conventions from its branch names, commit subjects, PR titles and file listing. Pure.</summary>
public static class ConventionDetector
{
    public const string DefaultBaseBranch = "main";
    public const string DefaultBranchPattern = "autopilot/<id>";
    public const string DefaultCommitPattern = "plain";
    public const string DefaultPrTitlePattern = "[autopilot] <title>";
    public const string CommitIdSubject = "#<id> <subject>";
    public const string CommitConventional = "conventional";

    private static readonly string[] BaseNames = ["main", "master", "develop", "trunk"];
    private static readonly Regex Hash = new(@"^#(\d+)\s+\S", RegexOptions.Compiled);
    private static readonly Regex AzureBoards = new(@"\bAB#\d+", RegexOptions.Compiled);
    private static readonly Regex JiraKey = new(@"\b[A-Z][A-Z0-9]+-\d+\b", RegexOptions.Compiled);
    private static readonly Regex HashNumber = new(@"#\d+", RegexOptions.Compiled);
    private static readonly Regex Conventional = new(@"^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([^)]*\))?!?: \S", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ConventionProposal Detect(ConventionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var commits = input.CommitSubjects.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        var files = input.Files.Select(f => f.Replace('\\', '/')).ToList();
        var baseName = BaseBranch(input, out var baseFinding);
        var topics = input.Branches.Where(b => !IsBaseLike(b, baseName)).ToList();
        var hasHistory = commits.Count > 0;

        var build = Build(files, input.PackageJson);
        var test = Test(files, input.PackageJson);
        var writable = Writable(files);
        var risky = Risky(files);

        if (!hasHistory)
        {
            return new ConventionProposal(
                false,
                "No usable history: the repository has no commits to learn from, so the defaults are offered.",
                baseFinding,
                new(DefaultBranchPattern, 0, []),
                new(DefaultCommitPattern, 0, []),
                new(DefaultPrTitlePattern, 0, []),
                new("<number>", 0, []),
                new("unknown", 0, []),
                build, test, writable, risky);
        }

        var prTitles = input.PullRequestTitles?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        var (idPattern, tracker) = TaskId(commits, topics, prTitles, input.RemoteUrl);
        return new ConventionProposal(
            true, null, baseFinding,
            BranchPattern(topics),
            CommitPattern(commits),
            prTitles is { Count: > 0 } ? CommitPattern(prTitles) : new(DefaultPrTitlePattern, 0, []),
            idPattern, tracker, build, test, writable, risky);
    }

    private static string BaseBranch(ConventionInput input, out ConventionFinding finding)
    {
        if (!string.IsNullOrWhiteSpace(input.DefaultBranch))
        {
            var name = input.DefaultBranch.Trim();
            finding = new(name, 1, [$"remote HEAD -> {name}"]);
            return name;
        }

        foreach (var candidate in BaseNames)
        {
            if (input.Branches.Contains(candidate, StringComparer.Ordinal))
            {
                finding = new(candidate, 0.7, [$"remote branch {candidate} exists"]);
                return candidate;
            }
        }

        finding = new(DefaultBaseBranch, 0.1, ["no remote HEAD or well known branch found; default"]);
        return DefaultBaseBranch;
    }

    private static bool IsBaseLike(string branch, string baseName) =>
        branch == baseName || BaseNames.Contains(branch, StringComparer.Ordinal) || branch == "HEAD";

    private static ConventionFinding BranchPattern(List<string> topics)
    {
        if (topics.Count == 0)
        {
            return new(DefaultBranchPattern, 0, ["no topic branches on the remote; default"]);
        }

        var groups = topics.GroupBy(BranchShape).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        var top = groups[0];
        var share = (double)top.Count() / topics.Count;
        return new(top.Key, Math.Round(share * Math.Min(1.0, topics.Count / 5.0), 2), top.Take(3).ToList(), Math.Round(share, 2));
    }

    /// <summary>task/task-12 becomes task/task-&lt;id&gt;; feature/12-add-x becomes feature/&lt;id&gt;-&lt;slug&gt;; fix/login becomes fix/&lt;slug&gt;.</summary>
    private static string BranchShape(string branch)
    {
        var m = Regex.Match(branch, @"^(.*?)(\d+)(.*)$");
        if (m.Success)
        {
            var tail = m.Groups[3].Value;
            return m.Groups[1].Value + "<id>" + (Regex.IsMatch(tail, @"^[-_/]?[A-Za-z]") ? tail[..(tail[0] is '-' or '_' or '/' ? 1 : 0)] + "<slug>" : string.Empty);
        }

        var slash = branch.LastIndexOf('/');
        return slash >= 0 ? branch[..(slash + 1)] + "<slug>" : "<slug>";
    }

    private static ConventionFinding CommitPattern(List<string> subjects)
    {
        var hash = subjects.Where(s => Hash.IsMatch(s)).ToList();
        var conv = subjects.Where(s => Conventional.IsMatch(s)).ToList();
        var plain = subjects.Count - hash.Count - conv.Count;
        var (value, matched) = hash.Count >= conv.Count && hash.Count > 0 && hash.Count >= plain ? (CommitIdSubject, hash)
            : conv.Count > 0 && conv.Count >= plain ? (CommitConventional, conv)
            : (DefaultCommitPattern, subjects.Where(s => !Hash.IsMatch(s) && !Conventional.IsMatch(s)).ToList());
        var share = subjects.Count == 0 ? 0 : (double)matched.Count / subjects.Count;
        return new(value, Math.Round(share * Math.Min(1.0, subjects.Count / 5.0), 2), matched.Take(3).ToList(), Math.Round(share, 2));
    }

    private static (ConventionFinding Pattern, ConventionFinding Tracker) TaskId(
        List<string> commits, List<string> branches, List<string>? prs, string? remoteUrl)
    {
        var lines = commits.Concat(branches).Concat(prs ?? []).ToList();
        var remote = remoteUrl ?? string.Empty;
        (string Pattern, string Tracker, Regex Rx)[] candidates =
        [
            ("AB#<number>", "azure-devops", AzureBoards),
            ("<KEY>-<number>", "jira", JiraKey),
            ("#<number>", remote.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) || remote.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase) ? "azure-devops" : "github", HashNumber),
        ];

        foreach (var (pattern, tracker, rx) in candidates)
        {
            var hits = lines.Where(l => rx.IsMatch(l)).ToList();
            if (hits.Count == 0) { continue; }
            var share = (double)hits.Count / lines.Count;
            var conf = Math.Round(Math.Min(1.0, share * 2) * Math.Min(1.0, hits.Count / 3.0), 2);
            return (new(pattern, conf, hits.Take(3).ToList(), Math.Round(share, 2)),
                    new(tracker, tracker == "github" && !remote.Contains("github.com", StringComparison.OrdinalIgnoreCase) ? Math.Min(conf, 0.4) : conf, hits.Take(1).ToList()));
        }

        var numbered = branches.Where(b => Regex.IsMatch(b, @"\d")).ToList();
        if (numbered.Count > 0)
        {
            var trackerGuess = TrackerFromRemote(remote);
            return (new("<number>", 0.4, numbered.Take(3).ToList(), Math.Round((double)numbered.Count / lines.Count, 2)),
                    new(trackerGuess, trackerGuess == "unknown" ? 0 : 0.3, []));
        }

        return (new("<number>", 0, []), new(TrackerFromRemote(remote), 0, []));
    }

    private static string TrackerFromRemote(string remote) =>
        remote.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) || remote.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase) ? "azure-devops"
        : remote.Contains("github.com", StringComparison.OrdinalIgnoreCase) ? "github"
        : "unknown";

    private static string? DotnetTarget(List<string> files)
    {
        string? Pick(string ext) => files.Where(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        return Pick(".slnx") ?? Pick(".sln") ?? Pick(".csproj");
    }

    private static string? Script(string? packageJson, string name)
    {
        if (string.IsNullOrWhiteSpace(packageJson)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(packageJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("scripts", out var scripts)
                && scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String)
            {
                return s.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static ConventionFinding Build(List<string> files, string? packageJson)
    {
        var target = DotnetTarget(files);
        if (target is not null) { return new($"dotnet build \"{target}\"", 0.9, [target]); }
        var script = Script(packageJson, "build");
        return script is not null ? new("npm run build", 0.8, [$"package.json scripts.build: {script}"]) : new(string.Empty, 0, []);
    }

    private static ConventionFinding Test(List<string> files, string? packageJson)
    {
        var target = DotnetTarget(files);
        if (target is not null) { return new($"dotnet test \"{target}\"", 0.9, [target]); }
        var script = Script(packageJson, "test");
        return script is not null ? new("npm test", 0.8, [$"package.json scripts.test: {script}"]) : new(string.Empty, 0, []);
    }

    private static ConventionFinding Writable(List<string> files)
    {
        var tops = files.Where(f => f.Contains('/')).Select(f => f[..f.IndexOf('/')]).Distinct(StringComparer.Ordinal).ToList();
        var found = new[] { "src", "tests", "test", "lib", "app" }.Where(tops.Contains).Select(d => d + "/**").ToList();
        return found.Count == 0 ? new(string.Empty, 0, []) : new(string.Join(", ", found), 0.6, found.Take(3).ToList());
    }

    private static ConventionFinding Risky(List<string> files)
    {
        var found = new List<string>();
        void Add(string glob, Func<string, bool> match)
        {
            var hit = files.FirstOrDefault(match);
            if (hit is not null) { found.Add(glob); }
        }

        Add(".github/**", f => f.StartsWith(".github/", StringComparison.Ordinal));
        Add(".autopilot/**", f => f.StartsWith(".autopilot/", StringComparison.Ordinal));
        Add("**/migrations/**", f => f.Contains("/migrations/", StringComparison.OrdinalIgnoreCase) || f.StartsWith("migrations/", StringComparison.OrdinalIgnoreCase));
        Add("**/.env*", f => Path.GetFileName(f).StartsWith(".env", StringComparison.Ordinal));
        Add("infra/**", f => f.StartsWith("infra/", StringComparison.Ordinal));
        Add("deploy/**", f => f.StartsWith("deploy/", StringComparison.Ordinal));
        Add("Dockerfile", f => f == "Dockerfile");
        return found.Count == 0 ? new(string.Empty, 0, []) : new(string.Join(", ", found), 0.6, found.Take(3).ToList());
    }
}
