using Autopilot.Core.Config;
using Autopilot.Core.Workspace;

namespace Autopilot.Web;

public sealed record AdoDraft
{
    public string OrganizationUrl { get; init; } = string.Empty;
    public string Project { get; init; } = string.Empty;
    public string Repository { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
    public string Auth { get; init; } = "pat";
    public string TokenVariable { get; init; } = "AUTOPILOT_ADO_PAT";
    public string TenantVariable { get; init; } = "AUTOPILOT_ADO_TENANT_ID";
    public string ClientIdVariable { get; init; } = "AUTOPILOT_ADO_CLIENT_ID";
    public string ClientSecretVariable { get; init; } = "AUTOPILOT_ADO_CLIENT_SECRET";
}

public sealed record RoleDraft(string? Model, int MaxToolCalls);

public sealed record IdentityDraft(string Name, string Email);

/// <summary>
/// Everything the setup wizard and the settings page edit, as one object. Only names of environment
/// variables appear here, never their values.
/// </summary>
public sealed record ProjectDraft
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string RepositoryFolder { get; init; } = string.Empty;
    public string BaseBranch { get; init; } = "main";
    public string BuildCommand { get; init; } = string.Empty;
    public string TestCommand { get; init; } = string.Empty;
    public IReadOnlyList<string> WritablePaths { get; init; } = [];
    public IReadOnlyList<string> RiskPaths { get; init; } = [];
    public IReadOnlyList<string> Context { get; init; } = [];
    public string TaskSource { get; init; } = ProjectRegistry.LocalSource;
    public AdoDraft Ado { get; init; } = new();
    public IdentityDraft Identity { get; init; } = new("Autopilot", "autopilot@localhost");
    public RoleDraft Triage { get; init; } = new(null, 20);
    public RoleDraft Implementer { get; init; } = new(null, 60);
    public RoleDraft Reviewer { get; init; } = new(null, 30);
    public int MaxFilesChanged { get; init; } = 6;
    public int MaxSourceLinesChanged { get; init; } = 150;
    public int MaxTestLinesChanged { get; init; } = 300;
    public bool RequireTestChange { get; init; } = true;
    public string Trust { get; init; } = TrustLevels.Ask;

    /// <summary>Always "human": the agent never merges.</summary>
    public string MergeBy => MergeConfig.Human;

    public static ProjectDraft FromConfig(AutopilotConfig config, string key, string displayName, string repositoryFolder, string taskSource)
    {
        var settings = config.Modules.WorkItems.Settings;
        string Get(string name, string fallback = "") => settings.TryGetValue(name, out var v) ? v : fallback;
        return new ProjectDraft
        {
            Key = key,
            DisplayName = displayName,
            RepositoryFolder = repositoryFolder,
            BaseBranch = config.BaseBranch,
            BuildCommand = config.Commands.Build,
            TestCommand = config.Commands.Test,
            WritablePaths = config.WritablePaths,
            RiskPaths = config.RiskPaths,
            Context = config.Context,
            TaskSource = taskSource,
            Ado = new AdoDraft
            {
                OrganizationUrl = Get("organizationUrl"),
                Project = Get("project"),
                Repository = Get("repository"),
                Query = Get("query"),
                Auth = Get("auth", "pat"),
                TokenVariable = Get("tokenVariable", "AUTOPILOT_ADO_PAT"),
                TenantVariable = Get("tenantVariable", "AUTOPILOT_ADO_TENANT_ID"),
                ClientIdVariable = Get("clientIdVariable", "AUTOPILOT_ADO_CLIENT_ID"),
                ClientSecretVariable = Get("clientSecretVariable", "AUTOPILOT_ADO_CLIENT_SECRET"),
            },
            Identity = new IdentityDraft(config.Identity?.Name ?? "Autopilot", config.Identity?.Email ?? "autopilot@localhost"),
            Triage = new RoleDraft(config.Roles.Triage.Model, config.Roles.Triage.MaxToolCalls),
            Implementer = new RoleDraft(config.Roles.Implementer.Model, config.Roles.Implementer.MaxToolCalls),
            Reviewer = new RoleDraft(config.Roles.Reviewer.Model, config.Roles.Reviewer.MaxToolCalls),
            MaxFilesChanged = config.Gate.MaxFilesChanged,
            MaxSourceLinesChanged = config.Gate.MaxSourceLinesChanged,
            MaxTestLinesChanged = config.Gate.MaxTestLinesChanged,
            RequireTestChange = config.Gate.RequireTestChange,
            Trust = config.Trust,
        };
    }

    /// <summary>Applies the draft onto <paramref name="current"/>; settings the draft does not cover are kept.</summary>
    public AutopilotConfig ApplyTo(AutopilotConfig current)
    {
        static string? Model(string? m) => string.IsNullOrWhiteSpace(m) ? null : m.Trim();
        static IReadOnlyList<string> Clean(IEnumerable<string> lines) =>
            [.. lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal)];

        ModuleConfig workItems;
        ModuleConfig publisher;
        if (TaskSource == ProjectRegistry.AzureDevOpsSource)
        {
            var settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["organizationUrl"] = Ado.OrganizationUrl.Trim(),
                ["project"] = Ado.Project.Trim(),
                ["repository"] = Ado.Repository.Trim(),
                ["auth"] = Ado.Auth.Trim(),
                ["tokenVariable"] = Ado.TokenVariable.Trim(),
                ["tenantVariable"] = Ado.TenantVariable.Trim(),
                ["clientIdVariable"] = Ado.ClientIdVariable.Trim(),
                ["clientSecretVariable"] = Ado.ClientSecretVariable.Trim(),
            };
            if (!string.IsNullOrWhiteSpace(Ado.Query))
            {
                settings["query"] = Ado.Query.Trim();
            }

            // Keep settings the page does not show (remote, tagPrefix, recordTriage, ...).
            foreach (var (name, value) in current.Modules.WorkItems.Settings)
            {
                settings.TryAdd(name, value);
            }

            workItems = new ModuleConfig { Type = "azure-devops", Settings = settings };
            publisher = new ModuleConfig { Type = "azure-devops", Settings = settings };
        }
        else
        {
            workItems = new ModuleConfig { Type = "local" };
            publisher = new ModuleConfig { Type = "local" };
        }

        return current with
        {
            BaseBranch = BaseBranch.Trim(),
            Commands = new CommandsConfig { Build = BuildCommand.Trim(), Test = TestCommand.Trim() },
            WritablePaths = Clean(WritablePaths),
            RiskPaths = Clean(RiskPaths),
            Context = Clean(Context),
            Identity = new IdentityConfig { Name = Identity.Name.Trim(), Email = Identity.Email.Trim() },
            Trust = Trust,
            Gate = current.Gate with
            {
                MaxFilesChanged = MaxFilesChanged,
                MaxSourceLinesChanged = MaxSourceLinesChanged,
                MaxTestLinesChanged = MaxTestLinesChanged,
                RequireTestChange = RequireTestChange,
            },
            Roles = new RolesConfig
            {
                Triage = current.Roles.Triage with { Model = Model(Triage.Model), MaxToolCalls = Triage.MaxToolCalls },
                Implementer = current.Roles.Implementer with { Model = Model(Implementer.Model), MaxToolCalls = Implementer.MaxToolCalls },
                Reviewer = current.Roles.Reviewer with { Model = Model(Reviewer.Model), MaxToolCalls = Reviewer.MaxToolCalls },
            },
            Modules = current.Modules with { WorkItems = workItems, Publisher = publisher },
        };
    }
}
