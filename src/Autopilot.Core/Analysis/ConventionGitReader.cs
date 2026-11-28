using Autopilot.Core.Git;

namespace Autopilot.Core.Analysis;

/// <summary>Thin git reader: collects the detector input from a folder using git only.</summary>
public static class ConventionGitReader
{
    public const int MaxCommits = 100;

    /// <param name="folder">The working folder of the repository.</param>
    /// <param name="listPullRequestTitles">Optional tracker call. When it is null or throws, PR titles are skipped.</param>
    public static async Task<ConventionInput> ReadAsync(
        string folder, Func<CancellationToken, Task<IReadOnlyList<string>>>? listPullRequestTitles = null, CancellationToken ct = default)
    {
        var branches = Lines(await GitClient.RunAsync(folder, ["branch", "-r", "--format=%(refname:short)"], ct).ConfigureAwait(false))
            .Where(b => !b.EndsWith("/HEAD", StringComparison.Ordinal) && b.Contains('/'))
            .Select(b => b[(b.IndexOf('/') + 1)..])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var subjects = Lines(await GitClient.RunAsync(folder, ["log", $"-n{MaxCommits}", "--format=%s"], ct).ConfigureAwait(false));
        var files = Lines(await GitClient.RunAsync(folder, ["ls-files"], ct).ConfigureAwait(false));

        var head = await GitClient.RunAsync(folder, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], ct).ConfigureAwait(false);
        var defaultBranch = head.Succeeded ? head.StandardOutput.Trim().Replace("origin/", string.Empty, StringComparison.Ordinal) : null;
        var remote = await GitClient.RunAsync(folder, ["remote", "get-url", "origin"], ct).ConfigureAwait(false);

        string? packageJson = null;
        var packagePath = Path.Combine(folder, "package.json");
        if (File.Exists(packagePath))
        {
            packageJson = await File.ReadAllTextAsync(packagePath, ct).ConfigureAwait(false);
        }

        IReadOnlyList<string>? prTitles = null;
        if (listPullRequestTitles is not null)
        {
            try
            {
                prTitles = await listPullRequestTitles(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                prTitles = null;
            }
        }

        return new ConventionInput(branches, subjects, prTitles, files, defaultBranch, remote.Succeeded ? remote.StandardOutput.Trim() : null, packageJson);
    }

    private static List<string> Lines(Process.ProcessResult result) =>
        !result.Succeeded
            ? []
            : result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
