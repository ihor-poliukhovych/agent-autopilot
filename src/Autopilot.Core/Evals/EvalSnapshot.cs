using System.Security.Cryptography;
using System.Text;
using Autopilot.Core.Git;
using Autopilot.Core.Process;

namespace Autopilot.Core.Evals;

/// <summary>
/// The pinned copy of the project every eval run starts from: the project's files without
/// <c>.autopilot/evals/</c> (the agent must never see hidden tests) and without <c>.autopilot/out/</c>.
/// The files are copied once into a staging folder; each run copies from there, so a file edited in
/// the project during the eval does not change later runs.
/// </summary>
public sealed class EvalSnapshot
{
    /// <summary>The fixed author and date of the snapshot commit, so every run gets the same commit hash.</summary>
    public static GitIdentity Author { get; } = new("Agent Autopilot Eval", "eval@localhost");

    public const string CommitDate = "2000-01-01T00:00:00Z";

    private static readonly string[] ExcludedPrefixes = [".autopilot/evals/", ".autopilot/out/"];
    private static readonly string[] ExcludedFolderNames = ["bin", "obj", ".git"];
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    private EvalSnapshot(string folder, IReadOnlyList<string> files, string hash, string source)
    {
        Folder = folder;
        Files = files;
        Hash = hash;
        Source = source;
    }

    /// <summary>Staging folder holding the copied files.</summary>
    public string Folder { get; }

    /// <summary>Relative paths with '/', sorted ordinally.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>SHA-256 (lower-case hex) over the sorted relative paths and the contents.</summary>
    public string Hash { get; }

    /// <summary>"git tracked files" or "folder" (when the project is not a git repository or has no tracked files).</summary>
    public string Source { get; }

    /// <summary>Lists the project's files, copies them into <paramref name="stagingFolder"/> and hashes the copies.</summary>
    public static async Task<EvalSnapshot> CreateAsync(string projectRoot, string stagingFolder, CancellationToken ct)
    {
        var root = Path.GetFullPath(projectRoot);
        var (files, source) = await ListFilesAsync(root, ct).ConfigureAwait(false);
        Directory.CreateDirectory(stagingFolder);
        var copied = new List<string>();
        foreach (var relative in files)
        {
            var from = Path.Combine(root, relative);
            if (!File.Exists(from))
            {
                continue; // tracked but deleted in the working folder
            }

            var to = Path.Combine(stagingFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, overwrite: true);
            copied.Add(relative);
        }

        copied.Sort(StringComparer.Ordinal);
        return new EvalSnapshot(Path.GetFullPath(stagingFolder), copied, ComputeHash(stagingFolder, copied), source);
    }

    /// <summary>True for paths that never go into a snapshot.</summary>
    public static bool IsExcluded(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        if (ExcludedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    /// <summary>SHA-256 over each path (UTF-8), a zero byte, the content length and the content, in the given order.</summary>
    public static string ComputeHash(string folder, IReadOnlyList<string> sortedRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(sortedRelativePaths);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var relative in sortedRelativePaths)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            var content = File.ReadAllBytes(Path.Combine(folder, relative));
            BitConverter.TryWriteBytes(length, (long)content.Length);
            hash.AppendData(length);
            hash.AppendData(content);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Copies the snapshot into <paramref name="repositoryFolder"/>, runs <c>git init -b &lt;baseBranch&gt;</c>
    /// and commits every file under <see cref="Author"/> at <see cref="CommitDate"/>. Returns the commit hash.
    /// </summary>
    public async Task<string> CreateRepositoryAsync(string repositoryFolder, string baseBranch, CancellationToken ct)
    {
        Directory.CreateDirectory(repositoryFolder);
        foreach (var relative in Files)
        {
            var to = Path.Combine(repositoryFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(Folder, relative), to, overwrite: true);
        }

        var env = new Dictionary<string, string?>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["LC_ALL"] = "C",
            ["GIT_AUTHOR_NAME"] = Author.Name,
            ["GIT_AUTHOR_EMAIL"] = Author.Email,
            ["GIT_COMMITTER_NAME"] = Author.Name,
            ["GIT_COMMITTER_EMAIL"] = Author.Email,
            ["GIT_AUTHOR_DATE"] = CommitDate,
            ["GIT_COMMITTER_DATE"] = CommitDate,
        };
        await GitAsync(repositoryFolder, env, ct, "init", "--quiet", "-b", baseBranch).ConfigureAwait(false);
        // --force: a tracked file that matches .gitignore is still part of the project.
        await GitAsync(repositoryFolder, env, ct, "-c", "core.autocrlf=false", "add", "--all", "--force").ConfigureAwait(false);
        await GitAsync(repositoryFolder, env, ct,
            "-c", "user.name=" + Author.Name, "-c", "user.email=" + Author.Email, "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false",
            "commit", "--quiet", "--no-edit", "--allow-empty", "-m", "Eval snapshot").ConfigureAwait(false);
        return (await GitAsync(repositoryFolder, env, ct, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
    }

    private static async Task<(List<string> Files, string Source)> ListFilesAsync(string root, CancellationToken ct)
    {
        if (await GitClient.FindRepositoryRootAsync(root, ct).ConfigureAwait(false) is not null)
        {
            // Paths come out relative to the project folder, also when it is a subfolder of the repository.
            var result = await ProcessRunner.RunAsync("git", ["-c", "core.quotepath=false", "ls-files", "-z", "--cached", "--full-name", "--", "."], root, GitTimeout,
                new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" }, ct).ConfigureAwait(false);
            if (result.Succeeded)
            {
                var prefix = await RepositoryPrefixAsync(root, ct).ConfigureAwait(false);
                var tracked = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim('\r', '\n'))
                    .Where(p => p.Length > 0 && p.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(p => p[prefix.Length..])
                    .Where(p => !IsExcluded(p))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (tracked.Count > 0)
                {
                    return (tracked, "git tracked files");
                }
            }
        }

        var files = new List<string>();
        Walk(root, string.Empty, files);
        return (files, "folder");
    }

    private static async Task<string> RepositoryPrefixAsync(string root, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("git", ["rev-parse", "--show-prefix"], root, GitTimeout, null, ct).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : string.Empty;
    }

    private static void Walk(string folder, string relative, List<string> files)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var path = relative + Path.GetFileName(file);
            if (!IsExcluded(path))
            {
                files.Add(path);
            }
        }

        foreach (var child in Directory.EnumerateDirectories(folder))
        {
            var name = Path.GetFileName(child);
            if (ExcludedFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                || new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            var path = relative + name + "/";
            if (IsExcluded(path))
            {
                continue;
            }

            Walk(child, path, files);
        }
    }

    private static async Task<string> GitAsync(string folder, IReadOnlyDictionary<string, string?> env, CancellationToken ct, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync("git", arguments, folder, GitTimeout, env, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new GitException($"git {string.Join(' ', arguments)} failed (exit code {result.ExitCode}): {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }
}
