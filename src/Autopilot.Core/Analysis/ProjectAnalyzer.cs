using System.Text.RegularExpressions;
using Autopilot.Core.Git;
using Autopilot.Core.Process;
using Autopilot.Core.Workspace;

namespace Autopilot.Core.Analysis;

/// <summary>
/// Looks at a folder and suggests every project setting, each with the file or rule behind it. It is
/// deterministic, uses no language model and no network, and only reads the folder (git is asked read-only
/// questions). Nothing is written.
/// </summary>
public static partial class ProjectAnalyzer
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    public static async Task<ProjectAnalysis> AnalyzeAsync(string folder, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new ArgumentException("Choose a folder first.");
        }

        var requested = Path.GetFullPath(folder.Trim());
        if (!Directory.Exists(requested))
        {
            throw new ArgumentException($"The folder {requested} does not exist.");
        }

        var warnings = new List<string>();
        var (root, gitRoot) = await FindRootAsync(requested, warnings, ct).ConfigureAwait(false);
        var isGit = gitRoot is not null;
        var remotes = isGit ? await ReadRemotesAsync(root, ct).ConfigureAwait(false) : [];
        var (baseBranch, candidates) = isGit ? await ChooseBaseBranchAsync(root, ct).ConfigureAwait(false) : (new Detected("main", "This folder is not a git repository, so there is no branch yet; main is the usual name", Confidence.Low), []);

        var index = FolderIndex.Build(root);
        if (index.Truncated)
        {
            warnings.Add($"The folder is very large; only the first {FolderIndex.MaxEntries} entries were looked at.");
        }

        var stacks = StackDetector.Detect(index);
        var (build, test) = StackDetector.Commands(index, stacks, warnings);
        var writable = StackDetector.WritablePaths(index, stacks);
        var risk = PathSuggester.RiskPaths(index);
        var context = PathSuggester.Context(index, warnings);
        var (taskSource, ado) = ChooseTaskSource(remotes);
        var (key, displayName) = ChooseNames(root, remotes);

        var needs = new List<NeedsInput>();
        if (!isGit)
        {
            needs.Add(new NeedsInput("gitRepository", "This folder is not a git repository. Run 'git init' and make a first commit, or pick the repository's top folder."));
        }

        if (stacks.Count == 0)
        {
            warnings.Add("No project file was recognised (.NET, Node, Python, Go, Rust, Maven or Gradle).");
        }

        if (build is null)
        {
            needs.Add(new NeedsInput("buildCommand", "No build command could be detected. Which command builds the project from a clean checkout?"));
        }
        else if (build.Confidence == Confidence.Low)
        {
            needs.Add(new NeedsInput("buildCommand", $"The build command is a low-confidence guess ({build.Evidence}). Is it right?"));
        }

        if (test is null)
        {
            needs.Add(new NeedsInput("testCommand", "No test command could be detected. Which command runs the tests?"));
        }
        else if (test.Confidence == Confidence.Low)
        {
            needs.Add(new NeedsInput("testCommand", $"The test command is a low-confidence guess ({test.Evidence}). Is it right?"));
        }

        if (writable.Count == 0)
        {
            needs.Add(new NeedsInput("writablePaths", "No source or test folder was found. Which folders may the agent change?"));
        }

        if (isGit && baseBranch.Confidence == Confidence.Low)
        {
            needs.Add(new NeedsInput("baseBranch", $"Which branch should agent work start from? ({baseBranch.Evidence})"));
        }

        if (ado is not null)
        {
            needs.Add(new NeedsInput("ado.tokenVariable", "Name of the environment variable that holds your Azure DevOps token (the default is AUTOPILOT_ADO_PAT). Set it before the connection check; its value is never stored."));
        }

        return new ProjectAnalysis
        {
            Folder = root,
            IsGitRepository = isGit,
            GitRoot = gitRoot,
            Remotes = remotes,
            BaseBranch = baseBranch,
            BaseBranchCandidates = candidates,
            Stacks = stacks,
            Build = build,
            Test = test,
            WritablePaths = writable,
            RiskPaths = risk,
            Context = context,
            TaskSource = taskSource,
            AzureDevOps = ado,
            Key = key,
            DisplayName = displayName,
            NeedsInput = needs,
            Warnings = warnings,
        };
    }

    /// <summary>The repository's top folder when the folder is inside one: the whole repository is analyzed then.</summary>
    private static async Task<(string Root, Detected? GitRoot)> FindRootAsync(string folder, List<string> warnings, CancellationToken ct)
    {
        if (Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git")))
        {
            return (folder, new Detected(folder, ".git found in this folder", Confidence.High));
        }

        var root = await GitClient.FindRepositoryRootAsync(folder, ct).ConfigureAwait(false);
        if (root is null)
        {
            return (folder, null);
        }

        warnings.Add($"{folder} is inside the git repository at {root}. The whole repository is analyzed, because a project is always a whole repository.");
        return (root, new Detected(root, $"git rev-parse --show-toplevel from {folder}", Confidence.High));
    }

    private static async Task<IReadOnlyList<RemoteInfo>> ReadRemotesAsync(string root, CancellationToken ct)
    {
        var result = await RunGitAsync(root, ["config", "--get-regexp", @"^remote\..*\.url$"], ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        var remotes = new List<RemoteInfo>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = RemoteLine().Match(line);
            if (match.Success)
            {
                remotes.Add(RemoteUrl.Parse(match.Groups["name"].Value, match.Groups["url"].Value));
            }
        }

        return [.. remotes.OrderBy(r => r.Name == "origin" ? 0 : 1).ThenBy(r => r.Name, StringComparer.Ordinal)];
    }

    private static async Task<(Detected Branch, IReadOnlyList<string> Candidates)> ChooseBaseBranchAsync(string root, CancellationToken ct)
    {
        var refs = await RunGitAsync(root, ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes"], ct).ConfigureAwait(false);
        var local = new HashSet<string>(StringComparer.Ordinal);
        var remote = new HashSet<string>(StringComparer.Ordinal);
        if (refs.Succeeded)
        {
            foreach (var line in refs.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    local.Add(line["refs/heads/".Length..]);
                }
                else if (line.StartsWith("refs/remotes/", StringComparison.Ordinal))
                {
                    var rest = line["refs/remotes/".Length..];
                    var slash = rest.IndexOf('/');
                    if (slash > 0 && rest[(slash + 1)..] != "HEAD")
                    {
                        remote.Add(rest[(slash + 1)..]);
                    }
                }
            }
        }

        var all = new HashSet<string>(local, StringComparer.Ordinal);
        all.UnionWith(remote);

        var head = await RunGitAsync(root, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], ct).ConfigureAwait(false);
        var originHead = head.Succeeded && head.StandardOutput.Trim().StartsWith("origin/", StringComparison.Ordinal) ? head.StandardOutput.Trim()["origin/".Length..] : null;
        var current = await new GitClient(root).GetCurrentBranchAsync(ct).ConfigureAwait(false);

        var candidates = new List<string>();
        void Add(string? name)
        {
            if (name is not null && all.Contains(name) && !candidates.Contains(name))
            {
                candidates.Add(name);
            }
        }

        Add(originHead);
        Add("main");
        Add("master");
        Add("develop");
        foreach (var release in all.Where(b => b.StartsWith("release/", StringComparison.Ordinal)).OrderByDescending(b => b, StringComparer.Ordinal))
        {
            Add(release);
        }

        Add(current);

        string Where(string name) => local.Contains(name) ? "branch" : "remote branch";
        if (originHead is not null && candidates.Contains(originHead))
        {
            return (new Detected(originHead, $"origin/HEAD points to {originHead}", Confidence.High), candidates);
        }

        foreach (var name in new[] { "main", "master" })
        {
            if (candidates.Contains(name))
            {
                return (new Detected(name, $"The {Where(name)} {name} exists" + (candidates.Contains("develop") ? "; develop exists too" : string.Empty), Confidence.High), candidates);
            }
        }

        if (candidates.Contains("develop"))
        {
            return (new Detected("develop", "The branch develop exists and there is no main or master", Confidence.Medium), candidates);
        }

        var releaseBranch = candidates.FirstOrDefault(c => c.StartsWith("release/", StringComparison.Ordinal));
        if (releaseBranch is not null)
        {
            return (new Detected(releaseBranch, "The newest release branch; there is no main, master or develop", Confidence.Low), candidates);
        }

        if (current is not null)
        {
            return (new Detected(current, "The branch that is checked out; there is no main, master or develop", Confidence.Low), candidates);
        }

        return (new Detected("main", "The repository has no branch yet (no commit); main is the usual name", Confidence.Low), candidates);
    }

    private static (Detected Source, AzureDevOpsGuess? Ado) ChooseTaskSource(IReadOnlyList<RemoteInfo> remotes)
    {
        var azure = remotes.FirstOrDefault(r => r.Kind == RemoteUrl.AzureDevOps && r.OrganizationUrl is not null && r.Project is not null && r.Repository is not null);
        if (azure is not null)
        {
            var from = $"remote {azure.Name} ({azure.Url})";
            return (
                new Detected(ProjectRegistry.AzureDevOpsSource, $"{from} is an Azure DevOps repository", Confidence.High),
                new AzureDevOpsGuess(
                    new Detected(azure.OrganizationUrl!, $"Organisation in the address of {from}", Confidence.High),
                    new Detected(azure.Project!, $"Project in the address of {from}", Confidence.High),
                    new Detected(azure.Repository!, $"Repository in the address of {from}", Confidence.High)));
        }

        var github = remotes.FirstOrDefault(r => r.Kind == RemoteUrl.GitHub);
        if (github is not null)
        {
            return (new Detected(ProjectRegistry.LocalSource, $"remote {github.Name} is a GitHub repository; GitHub issues are not supported yet, so tasks are local files", Confidence.Medium), null);
        }

        return (new Detected(ProjectRegistry.LocalSource, remotes.Count == 0 ? "No remote is configured, so tasks are local files" : "The remote is not Azure DevOps; tasks are local files", Confidence.Medium), null);
    }

    private static (Detected Key, Detected DisplayName) ChooseNames(string root, IReadOnlyList<RemoteInfo> remotes)
    {
        var fromRemote = remotes.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Repository) && ProjectRegistry.IsValidKey(ToKey(r.Repository)));
        var raw = fromRemote?.Repository ?? Path.GetFileName(root.TrimEnd('\\', '/'));
        var evidence = fromRemote is not null ? $"Repository name in remote {fromRemote.Name}" : "Name of the folder";
        var key = ToKey(raw);
        var valid = ProjectRegistry.IsValidKey(key);
        return (
            new Detected(valid ? key : "project", valid ? evidence : $"{evidence} is not a usable key; \"project\" is a placeholder", valid ? Confidence.High : Confidence.Low),
            new Detected(string.IsNullOrWhiteSpace(raw) ? "Project" : raw, evidence, valid ? Confidence.High : Confidence.Low));
    }

    private static string ToKey(string raw)
    {
        var key = NonKeyChars().Replace(raw.ToLowerInvariant(), "-").Trim('-', '_');
        return key.Length > ProjectRegistry.MaxKeyLength ? key[..ProjectRegistry.MaxKeyLength].Trim('-', '_') : key;
    }

    private static Task<ProcessResult> RunGitAsync(string folder, IReadOnlyList<string> arguments, CancellationToken ct) =>
        ProcessRunner.RunAsync("git", arguments, folder, GitTimeout, new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" }, ct);

    [GeneratedRegex(@"^remote\.(?<name>.+)\.url\s+(?<url>.+)$")]
    private static partial Regex RemoteLine();

    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex NonKeyChars();
}
