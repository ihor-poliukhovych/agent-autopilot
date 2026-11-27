using System.Globalization;
using Autopilot.Core.Git;

namespace Autopilot.Core.Pool;

/// <summary>
/// Cleans up after a run that died (process killed, crash, reboot): the task branch it created and the
/// worktree that has it checked out. Only the pool calls it, and only after it holds the task's lock, so no
/// live run owns the task. Work is never thrown away: a branch whose commits are all in the base branch is
/// deleted, any other is renamed to <c>&lt;branch&gt;-attempt-N</c> so a person can still look at it.
/// </summary>
public sealed class RunLeftovers(GitClient git, string branchPrefix, Func<IReadOnlyList<string>> baseBranches)
{
    /// <summary>Returns a sentence about what was done, or null when nothing was left over.</summary>
    public async Task<string?> ReleaseAsync(string workItemId, CancellationToken ct)
    {
        var branch = branchPrefix + workItemId;
        if (!await git.BranchExistsAsync(branch, ct).ConfigureAwait(false))
        {
            return null;
        }

        foreach (var worktree in await git.WorktreesOfBranchAsync(branch, ct).ConfigureAwait(false))
        {
            await git.RemoveWorktreeAsync(worktree, ct).ConfigureAwait(false);
        }

        foreach (var name in baseBranches())
        {
            var baseRef = await git.ResolveBranchAsync(name, ct).ConfigureAwait(false);
            if (baseRef is not null && await git.IsAncestorAsync(branch, baseRef.Ref, ct).ConfigureAwait(false))
            {
                await git.DeleteBranchAsync(branch, ct).ConfigureAwait(false);
                return $"Removed the branch {branch} left over from an earlier run that did not finish; it held nothing that {name} lacks.";
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            var kept = $"{branch}-attempt-{attempt.ToString(CultureInfo.InvariantCulture)}";
            if (!await git.BranchExistsAsync(kept, ct).ConfigureAwait(false))
            {
                await git.RenameBranchAsync(branch, kept, ct).ConfigureAwait(false);
                return $"Kept the branch {branch} left over from an earlier run as {kept} and started the task on a new one.";
            }
        }
    }
}
