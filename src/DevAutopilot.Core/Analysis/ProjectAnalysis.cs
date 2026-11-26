using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevAutopilot.Core.Analysis;

/// <summary>
/// Everything <see cref="ProjectAnalyzer"/> found out about a folder. Every value says which file or rule
/// produced it and how sure the analyzer is, so a person can check it before it is used.
/// </summary>
public sealed record ProjectAnalysis
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The folder that was analyzed (full path).</summary>
    public required string Folder { get; init; }

    public bool IsGitRepository { get; init; }

    /// <summary>The top-level folder of the git repository. This is where the project's settings and worktrees point.</summary>
    public Detected? GitRoot { get; init; }

    public IReadOnlyList<RemoteInfo> Remotes { get; init; } = [];

    public required Detected BaseBranch { get; init; }

    /// <summary>Every branch that could be the base branch, best first.</summary>
    public IReadOnlyList<string> BaseBranchCandidates { get; init; } = [];

    public IReadOnlyList<DetectedStack> Stacks { get; init; } = [];

    public Detected? Build { get; init; }

    public Detected? Test { get; init; }

    public IReadOnlyList<Detected> WritablePaths { get; init; } = [];

    public IReadOnlyList<Detected> RiskPaths { get; init; } = [];

    /// <summary>Rule and context documents of the project, best first.</summary>
    public IReadOnlyList<Detected> Context { get; init; } = [];

    /// <summary><c>local</c> or <c>azure-devops</c>.</summary>
    public required Detected TaskSource { get; init; }

    /// <summary>Set when a remote points at Azure DevOps.</summary>
    public AzureDevOpsGuess? AzureDevOps { get; init; }

    public required Detected Key { get; init; }

    public required Detected DisplayName { get; init; }

    /// <summary>What only the person can give or confirm.</summary>
    public IReadOnlyList<NeedsInput> NeedsInput { get; init; } = [];

    /// <summary>Things that are worth knowing but do not stop anything.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>Azure DevOps settings read from a remote's address.</summary>
public sealed record AzureDevOpsGuess(Detected OrganizationUrl, Detected Project, Detected Repository);
