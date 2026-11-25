using System.Security.Cryptography;
using System.Text;
using Autopilot.Core.Gate;
using Autopilot.Core.Process;

namespace Autopilot.Core.Git;

/// <summary>The name and email the harness commits under.</summary>
public sealed record GitIdentity(string Name, string Email)
{
    public static GitIdentity Default { get; } = new("Agent Autopilot", "autopilot@localhost");
}

/// <summary>Thin wrapper over the git command line for one repository.</summary>
public sealed class GitClient
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    public GitClient(string repositoryPath, GitIdentity? identity = null)
    {
        RepositoryPath = Path.GetFullPath(repositoryPath);
        Identity = identity ?? GitIdentity.Default;
    }

    /// <summary>Any folder inside the repository; commands run from here.</summary>
    public string RepositoryPath { get; }

    public GitIdentity Identity { get; }

    /// <summary>Returns the top-level folder of the repository that contains <paramref name="path"/>, or null.</summary>
    public static async Task<string?> FindRepositoryRootAsync(string path, CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        var result = await RunGitAsync(path, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false);
        return result.Succeeded ? Path.GetFullPath(result.StandardOutput.Trim()) : null;
    }

    /// <summary>
    /// Default folder for this repository's worktrees. It is outside the project on purpose: Claude Code
    /// reads CLAUDE.md files from parent folders, and the agent must not see the project's own ones.
    /// </summary>
    public static string DefaultWorktreeRoot(string repositoryRoot)
    {
        var full = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var key = OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(name))
        {
            name = "repo";
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "agent-autopilot",
            "worktrees",
            $"{name}-{hash}");
    }

    public async Task<string> GetRepositoryRootAsync(CancellationToken ct = default) =>
        Path.GetFullPath((await RunCheckedAsync(RepositoryPath, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false)).Trim());

    /// <summary>Full commit hash of a revision (branch, tag, HEAD, hash).</summary>
    public async Task<string> GetCommitAsync(string revision, CancellationToken ct = default) =>
        (await RunCheckedAsync(RepositoryPath, ["rev-parse", "--verify", "--quiet", revision + "^{commit}"], ct).ConfigureAwait(false)).Trim();

    public async Task<string?> TryGetCommitAsync(string revision, CancellationToken ct = default)
    {
        var result = await RunGitAsync(RepositoryPath, ["rev-parse", "--verify", "--quiet", revision + "^{commit}"], ct).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    public async Task<string?> GetCurrentBranchAsync(CancellationToken ct = default)
    {
        var result = await RunGitAsync(RepositoryPath, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    public async Task<bool> BranchExistsAsync(string branch, CancellationToken ct = default)
    {
        var result = await RunGitAsync(RepositoryPath, ["show-ref", "--verify", "--quiet", "refs/heads/" + branch], ct).ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <summary>Runs <c>git worktree add -b &lt;branch&gt; &lt;path&gt; &lt;base&gt;</c>.</summary>
    public async Task AddWorktreeAsync(string path, string newBranch, string baseRevision, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await RunCheckedAsync(RepositoryPath, ["worktree", "add", "-b", newBranch, path, baseRevision], ct).ConfigureAwait(false);
    }

    /// <summary>Checks out a commit into a new worktree without creating a branch.</summary>
    public async Task AddDetachedWorktreeAsync(string path, string revision, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await RunCheckedAsync(RepositoryPath, ["worktree", "add", "--detach", path, revision], ct).ConfigureAwait(false);
    }

    /// <summary>Removes a worktree (even with local changes), deletes its folder and prunes git's records.</summary>
    public async Task RemoveWorktreeAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        await RunGitAsync(RepositoryPath, ["worktree", "remove", "--force", "--force", full], ct).ConfigureAwait(false);
        if (Directory.Exists(full))
        {
            await DeleteDirectoryAsync(full).ConfigureAwait(false);
        }

        await RunGitAsync(RepositoryPath, ["worktree", "prune"], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stages everything in the worktree and commits it under <see cref="Identity"/>.
    /// Returns the new commit hash, or null when there was nothing to commit.
    /// </summary>
    public async Task<string?> CommitAllAsync(string worktreePath, string message, CancellationToken ct = default)
    {
        await RunCheckedAsync(worktreePath, ["add", "--all"], ct).ConfigureAwait(false);
        var status = await RunCheckedAsync(worktreePath, ["status", "--porcelain"], ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        await RunCheckedAsync(
            worktreePath,
            [
                "-c", "user.name=" + Identity.Name,
                "-c", "user.email=" + Identity.Email,
                "-c", "commit.gpgsign=false",
                "commit", "--quiet", "--no-edit", "-m", message,
            ],
            ct,
            new Dictionary<string, string?>
            {
                ["GIT_AUTHOR_NAME"] = Identity.Name,
                ["GIT_AUTHOR_EMAIL"] = Identity.Email,
                ["GIT_COMMITTER_NAME"] = Identity.Name,
                ["GIT_COMMITTER_EMAIL"] = Identity.Email,
            }).ConfigureAwait(false);

        return (await RunCheckedAsync(worktreePath, ["rev-parse", "HEAD"], ct).ConfigureAwait(false)).Trim();
    }

    /// <summary>Unified diff between two revisions, with rename detection.</summary>
    public Task<string> GetDiffAsync(string baseRevision, string headRevision, CancellationToken ct = default) =>
        RunCheckedAsync(RepositoryPath, ["-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff", "-M", baseRevision, headRevision], ct);

    /// <summary>Every changed file between two revisions with added and removed line counts.</summary>
    public async Task<IReadOnlyList<FileChange>> GetChangesAsync(string baseRevision, string headRevision, CancellationToken ct = default)
    {
        var numstat = await RunCheckedAsync(RepositoryPath, ["diff", "--no-color", "--no-ext-diff", "--numstat", "-M", "-z", baseRevision, headRevision], ct).ConfigureAwait(false);
        var nameStatus = await RunCheckedAsync(RepositoryPath, ["diff", "--no-color", "--no-ext-diff", "--name-status", "-M", "-z", baseRevision, headRevision], ct).ConfigureAwait(false);
        return ParseChanges(numstat, nameStatus);
    }

    /// <summary>Joins <c>--numstat -z</c> and <c>--name-status -z</c> output into file changes.</summary>
    public static IReadOnlyList<FileChange> ParseChanges(string numstatZ, string nameStatusZ)
    {
        var lines = new Dictionary<string, (int Added, int Removed, bool Binary)>(StringComparer.Ordinal);
        var tokens = numstatZ.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i].TrimStart('\n', '\r');
            if (token.Length == 0)
            {
                continue;
            }

            var parts = token.Split('\t', 3);
            if (parts.Length < 3)
            {
                continue;
            }

            var binary = parts[0] == "-" || parts[1] == "-"; // numstat shows "-" for binary files
            var added = int.TryParse(parts[0], out var a) ? a : 0;
            var removed = int.TryParse(parts[1], out var r) ? r : 0;
            string path;
            if (parts[2].Length == 0 && i + 2 < tokens.Length)
            {
                // Rename or copy: an empty path, then the old and the new path.
                path = tokens[i + 2];
                i += 2;
            }
            else
            {
                path = parts[2];
            }

            lines[path] = (added, removed, binary);
        }

        var changes = new List<FileChange>();
        var items = nameStatusZ.Split('\0');
        for (var i = 0; i < items.Length; i++)
        {
            var status = items[i].Trim('\n', '\r');
            if (status.Length == 0)
            {
                continue;
            }

            var letter = status[0];
            string path;
            string? oldPath = null;
            if (letter is 'R' or 'C')
            {
                if (i + 2 >= items.Length)
                {
                    break;
                }

                oldPath = items[i + 1];
                path = items[i + 2];
                i += 2;
            }
            else
            {
                if (i + 1 >= items.Length)
                {
                    break;
                }

                path = items[i + 1];
                i += 1;
            }

            var kind = letter switch
            {
                'A' => FileChangeKind.Added,
                'D' => FileChangeKind.Deleted,
                'R' => FileChangeKind.Renamed,
                'C' => FileChangeKind.Added,
                _ => FileChangeKind.Modified,
            };
            if (letter == 'C')
            {
                oldPath = null;
            }

            lines.TryGetValue(path, out var counts);
            changes.Add(new FileChange(path, kind, counts.Added, counts.Removed, oldPath, counts.Binary));
        }

        return changes;
    }

    private async Task<string> RunCheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var result = await RunGitAsync(workingDirectory, arguments, ct, environment).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new GitException(
                $"git {string.Join(' ', arguments)} failed (exit code {result.ExitCode}): {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    private static Task<ProcessResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken ct,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var env = new Dictionary<string, string?>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["LC_ALL"] = "C",
        };
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                env[key] = value;
            }
        }

        return ProcessRunner.RunAsync("git", arguments, workingDirectory, GitTimeout, env, ct);
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                await Task.Delay(200 * attempt).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>A git command failed. The message includes git's own error text.</summary>
public sealed class GitException(string message) : Exception(message);
