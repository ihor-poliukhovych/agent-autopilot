namespace DevAutopilot.Core.Config;

/// <summary>
/// Project settings, read from <c>.autopilot/autopilot.json</c> in the project root.
/// Every property has a default so that an almost empty file is valid.
/// </summary>
public sealed record AutopilotConfig
{
    public const string FolderName = ".autopilot";
    public const string FileName = "autopilot.json";

    public int Version { get; init; } = 1;

    /// <summary>Branch that agent branches start from and that draft PRs target.</summary>
    public string BaseBranch { get; init; } = "main";

    /// <summary>Prefix for agent branches, followed by the work item id.</summary>
    public string BranchPrefix { get; init; } = "autopilot/";

    public CommandsConfig Commands { get; init; } = new();

    /// <summary>Glob patterns (relative to the project root) the implementer may write to.</summary>
    public IReadOnlyList<string> WritablePaths { get; init; } = ["src/**", "tests/**"];

    /// <summary>
    /// Glob patterns that make a change high risk; such a change always goes to a person. The agent can
    /// never write these files either: the hook treats them like protected paths.
    /// </summary>
    public IReadOnlyList<string> RiskPaths { get; init; } =
    [
        "**/*Auth*/**", "**/*Auth*.cs", "**/*Security*/**", "**/*Security*.cs",
        "**/appsettings*.json", "**/*.csproj", "**/Directory.Build.*", "**/Directory.Packages.props",
        "**/*.props", "**/*.targets",
    ];

    /// <summary>
    /// Extra environment variable names passed to Claude Code and to build and test commands, on top of
    /// the built-in allow-list. Names starting with ANTHROPIC_ or AUTOPILOT_ are never passed.
    /// </summary>
    public IReadOnlyList<string> PassEnvironment { get; init; } = [];

    /// <summary>
    /// Project rules and context for the agents: project-relative files or folders (for example
    /// <c>docs/agent-rules.md</c> or <c>docs/decisions/</c>). Their text is read from the base commit and
    /// added to the triage, implementer and reviewer prompts. The agent can never write these paths.
    /// </summary>
    public IReadOnlyList<string> Context { get; init; } = [];

    public GateConfig Gate { get; init; } = new();

    public RolesConfig Roles { get; init; } = new();

    public ModulesConfig Modules { get; init; } = new();
}

public sealed record CommandsConfig
{
    public string Build { get; init; } = "dotnet build";
    public string Test { get; init; } = "dotnet test";
}

public sealed record GateConfig
{
    public int MaxFilesChanged { get; init; } = 6;

    /// <summary>Added plus removed lines in files that are not tests.</summary>
    public int MaxSourceLinesChanged { get; init; } = 150;

    /// <summary>Added plus removed lines in test files.</summary>
    public int MaxTestLinesChanged { get; init; } = 300;

    /// <summary>Glob patterns that mark a file as a test file.</summary>
    public IReadOnlyList<string> TestPaths { get; init; } = ["tests/**", "**/*Tests/**", "**/*Tests.cs", "**/*Test.cs"];

    /// <summary>When non-test code changed, at least one test file must change too.</summary>
    public bool RequireTestChange { get; init; } = true;
}

public sealed record RolesConfig
{
    public RoleConfig Triage { get; init; } = RoleConfig.TriageDefault;
    public RoleConfig Implementer { get; init; } = RoleConfig.ImplementerDefault;
    public RoleConfig Reviewer { get; init; } = RoleConfig.ReviewerDefault;

    public RoleConfig For(AgentRole role) => role switch
    {
        AgentRole.Triage => Triage,
        AgentRole.Implementer => Implementer,
        AgentRole.Reviewer => Reviewer,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}

public sealed record RoleConfig
{
    /// <summary>Claude Code model name or alias. Null means Claude Code's default.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tool calls; the hook denies every call after this.</summary>
    public int MaxToolCalls { get; init; } = 40;

    /// <summary>Claude Code tool names the role may use, for example Read, Grep, Glob, Edit, Write, Bash.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Shell commands the role may run through the Bash tool. A command is allowed when it
    /// equals an entry or starts with an entry followed by a space. Empty means no shell.
    /// </summary>
    public IReadOnlyList<string> AllowedCommands { get; init; } = [];

    public static RoleConfig TriageDefault { get; } = new()
    {
        MaxToolCalls = 20,
        AllowedTools = ["Read", "Grep", "Glob"],
    };

    public static RoleConfig ImplementerDefault { get; } = new()
    {
        MaxToolCalls = 60,
        AllowedTools = ["Read", "Grep", "Glob", "Edit", "Write", "Bash"],
        AllowedCommands = ["dotnet build", "dotnet test", "git diff", "git status"],
    };

    public static RoleConfig ReviewerDefault { get; } = new()
    {
        MaxToolCalls = 30,
        AllowedTools = ["Read", "Grep", "Glob", "Bash"],
        AllowedCommands = ["git diff", "git log", "git status"],
    };
}

public enum AgentRole
{
    Triage,
    Implementer,
    Reviewer,
}

public sealed record ModulesConfig
{
    public ModuleConfig WorkItems { get; init; } = new() { Type = "local" };
    public ModuleConfig Publisher { get; init; } = new() { Type = "local" };
    public ModuleConfig Diagnostics { get; init; } = new() { Type = "none" };
}

/// <summary>A module choice plus free-form settings that only that module reads.</summary>
public sealed record ModuleConfig
{
    public string Type { get; init; } = "none";
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}
