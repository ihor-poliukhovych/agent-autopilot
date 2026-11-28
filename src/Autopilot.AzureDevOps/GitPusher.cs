using Autopilot.Core.Process;

namespace Autopilot.AzureDevOps;

/// <summary>Pushes one agent branch to a remote.</summary>
public interface IGitPusher
{
    Task PushAsync(string repositoryPath, string remote, string branch, string branchPrefix, AzureDevOpsCredential credential, CancellationToken ct);

    /// <summary>
    /// The name to publish <paramref name="branch"/> under. The same name unless the remote already has a branch of
    /// that name that is not an ancestor of it (an earlier attempt that was pushed): then a free <c>-attempt-N</c> name,
    /// and the local branch is renamed to match. Nothing on the remote is changed.
    /// </summary>
    Task<string> ResolveBranchAsync(string repositoryPath, string remote, string branch, string branchPrefix, AzureDevOpsCredential credential, CancellationToken ct) =>
        Task.FromResult(branch);
}

/// <summary>The push or the pull request was refused or failed. The message never contains the token.</summary>
public sealed class AzureDevOpsPublishException(string message) : Exception(message);

/// <summary>
/// Pushes with the git command line. The token goes to git only through the environment of that one
/// process (<c>GIT_CONFIG_COUNT</c> / <c>GIT_CONFIG_KEY_n</c> / <c>GIT_CONFIG_VALUE_n</c> setting
/// <c>http.extraHeader</c>), so it is not on the command line and never written to a git config file.
/// The push is never forced, runs no hooks, and only the agent branch can be pushed.
/// </summary>
public sealed class GitPusher(Uri organizationUrl) : IGitPusher
{
    private static readonly TimeSpan PushTimeout = TimeSpan.FromMinutes(10);

    public async Task PushAsync(string repositoryPath, string remote, string branch, string branchPrefix, AzureDevOpsCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        EnsureAgentBranch(branch, branchPrefix);
        EnsureRemoteName(remote);

        var exists = await RunAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch], null, ct).ConfigureAwait(false);
        if (!exists.Succeeded)
        {
            throw new AzureDevOpsPublishException($"The branch '{branch}' does not exist in {repositoryPath}, so there is nothing to push.");
        }

        var url = await RunAsync(repositoryPath, ["remote", "get-url", remote], null, ct).ConfigureAwait(false);
        if (!url.Succeeded)
        {
            throw new AzureDevOpsPublishException($"The git remote '{remote}' does not exist. Add it (git remote add {remote} <repository URL>) or set \"remote\" in the settings.");
        }

        EnsureRemoteHost(url.StandardOutput.Trim(), organizationUrl);

        var environment = AuthEnvironment(credential);
        var refspec = $"refs/heads/{branch}:refs/heads/{branch}";
        var push = await RunAsync(repositoryPath, ["push", "--no-verify", "--porcelain", remote, refspec], environment, ct).ConfigureAwait(false);
        if (!push.Succeeded)
        {
            var error = credential.Redact(push.StandardError.Trim());
            throw new AzureDevOpsPublishException(
                $"git push of '{branch}' to '{remote}' failed (exit code {push.ExitCode}). " +
                "Check that the agent identity has Contribute and Create branch on the repository and that the branch does not already exist with other commits." +
                (error.Length > 0 ? " git said: " + error : string.Empty));
        }
    }

    public async Task<string> ResolveBranchAsync(string repositoryPath, string remote, string branch, string branchPrefix, AzureDevOpsCredential credential, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credential);
        EnsureAgentBranch(branch, branchPrefix);
        EnsureRemoteName(remote);

        var url = await RunAsync(repositoryPath, ["remote", "get-url", remote], null, ct).ConfigureAwait(false);
        if (!url.Succeeded)
        {
            throw new AzureDevOpsPublishException($"The git remote '{remote}' does not exist. Add it (git remote add {remote} <repository URL>) or set \"remote\" in the settings.");
        }

        EnsureRemoteHost(url.StandardOutput.Trim(), organizationUrl);
        var environment = AuthEnvironment(credential);

        var remoteHeads = await ListRemoteHeadsAsync(repositoryPath, remote, [$"refs/heads/{branch}", $"refs/heads/{branch}-attempt-*"], environment, credential, ct).ConfigureAwait(false);
        if (!remoteHeads.TryGetValue(branch, out var remoteCommit))
        {
            return branch;
        }

        // Every commit of the remote branch is already in ours: a plain (never forced) push moves it forward.
        var known = await RunAsync(repositoryPath, ["cat-file", "-e", remoteCommit + "^{commit}"], null, ct).ConfigureAwait(false);
        if (known.Succeeded
            && (await RunAsync(repositoryPath, ["merge-base", "--is-ancestor", remoteCommit, "refs/heads/" + branch], null, ct).ConfigureAwait(false)).Succeeded)
        {
            return branch;
        }

        for (var attempt = 1; ; attempt++)
        {
            var candidate = $"{branch}-attempt-{attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (remoteHeads.ContainsKey(candidate)
                || (await RunAsync(repositoryPath, ["show-ref", "--verify", "--quiet", "refs/heads/" + candidate], null, ct).ConfigureAwait(false)).Succeeded)
            {
                continue;
            }

            EnsureAgentBranch(candidate, branchPrefix);
            var rename = await RunAsync(repositoryPath, ["branch", "-m", branch, candidate], null, ct).ConfigureAwait(false);
            if (!rename.Succeeded)
            {
                throw new AzureDevOpsPublishException($"The remote already has '{branch}' with other commits, and renaming the local branch to '{candidate}' failed: {rename.StandardError.Trim()}");
            }

            return candidate;
        }
    }

    private static async Task<Dictionary<string, string>> ListRemoteHeadsAsync(
        string repositoryPath, string remote, IReadOnlyList<string> patterns, IReadOnlyDictionary<string, string?> environment, AzureDevOpsCredential credential, CancellationToken ct)
    {
        var list = await RunAsync(repositoryPath, ["ls-remote", "--heads", remote, .. patterns], environment, ct).ConfigureAwait(false);
        if (!list.Succeeded)
        {
            throw new AzureDevOpsPublishException(
                $"git could not list the branches of '{remote}' (exit code {list.ExitCode}), so it is not known whether the task branch is free." +
                (credential.Redact(list.StandardError.Trim()) is { Length: > 0 } error ? " git said: " + error : string.Empty));
        }

        var heads = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in list.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', 2);
            if (parts.Length == 2 && parts[1].StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                heads[parts[1]["refs/heads/".Length..]] = parts[0];
            }
        }

        return heads;
    }

    private static Dictionary<string, string?> AuthEnvironment(AzureDevOpsCredential credential) => new()
    {
        ["GIT_CONFIG_COUNT"] = "2",
        ["GIT_CONFIG_KEY_0"] = "http.extraHeader",
        ["GIT_CONFIG_VALUE_0"] = credential.ToGitExtraHeader(),
        // No credential helper: nothing may prompt for, or store, a password during this call.
        ["GIT_CONFIG_KEY_1"] = "credential.helper",
        ["GIT_CONFIG_VALUE_1"] = string.Empty,
    };

    /// <summary>Refuses anything that is not a plain branch name under the agent prefix.</summary>
    public static void EnsureAgentBranch(string branch, string branchPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchPrefix);
        if (string.IsNullOrWhiteSpace(branch)
            || !branch.StartsWith(branchPrefix, StringComparison.Ordinal)
            || branch.Length == branchPrefix.Length)
        {
            throw new AzureDevOpsPublishException($"Refusing to push '{branch}': only agent branches starting with '{branchPrefix}' can be pushed.");
        }

        var safe = branch.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')
            && !branch.Contains("..", StringComparison.Ordinal)
            && !branch.Contains("//", StringComparison.Ordinal)
            && !branch.EndsWith('/')
            && !branch.EndsWith(".lock", StringComparison.Ordinal)
            && !branch.StartsWith('-');
        if (!safe)
        {
            throw new AzureDevOpsPublishException($"Refusing to push '{branch}': the branch name has characters that are not allowed.");
        }
    }

    private static void EnsureRemoteName(string remote)
    {
        if (string.IsNullOrWhiteSpace(remote) || remote.StartsWith('-') || remote.Contains(':', StringComparison.Ordinal))
        {
            throw new AzureDevOpsPublishException($"Refusing to push to '{remote}': it is not a remote name.");
        }
    }

    /// <summary>The token may only travel to the organisation's own host over https.</summary>
    internal static void EnsureRemoteHost(string remoteUrl, Uri organizationUrl)
    {
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            // A local path, file:// or ssh remote: git does not send http headers there.
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new AzureDevOpsPublishException("Refusing to push: the remote uses http, not https, and the token would travel unencrypted.");
        }

        if (!string.Equals(uri.Host, organizationUrl.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new AzureDevOpsPublishException(
                $"Refusing to push: the remote points to {uri.Host}, not to {organizationUrl.Host} from \"organizationUrl\", and the token must not be sent elsewhere.");
        }
    }

    private static Task<ProcessResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?>? extra, CancellationToken ct)
    {
        var environment = new Dictionary<string, string?>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["LC_ALL"] = "C",
        };
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                environment[key] = value;
            }
        }

        return ProcessRunner.RunAsync("git", arguments, workingDirectory, PushTimeout, environment, ct);
    }
}
