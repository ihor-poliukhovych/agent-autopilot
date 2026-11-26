using DevAutopilot.Core.Config;

namespace DevAutopilot.AzureDevOps;

/// <summary>How the module gets its token.</summary>
public enum AzureDevOpsAuth
{
    /// <summary>A personal access token from a user environment variable.</summary>
    Pat,

    /// <summary>An access token from the Azure CLI (<c>az account get-access-token</c>).</summary>
    AzureCli,

    /// <summary>A Microsoft Entra service principal (client credentials); tenant, client id and secret come from environment variables.</summary>
    ServicePrincipal,
}

/// <summary>Where triage results go when the work items come from Azure DevOps.</summary>
public enum TriageRecording
{
    /// <summary>A triage tag and a comment on the work item, plus the local files (the default).</summary>
    Remote,

    /// <summary>Only the local files in <c>.autopilot/out/triage/</c>; nothing is written to Azure DevOps.</summary>
    Local,
}

/// <summary>
/// Settings of the Azure DevOps module, read from <c>modules.workItems.settings</c> or
/// <c>modules.publisher.settings</c> in <c>autopilot.json</c>. Both blocks use the same keys.
/// </summary>
public sealed record AzureDevOpsSettings
{
    public const string DefaultTokenVariable = "AUTOPILOT_ADO_PAT";
    public const string DefaultRemote = "origin";
    public const string DefaultTagPrefix = "autopilot:";
    public const string RequiredTokenVariablePrefix = "AUTOPILOT_";
    public const string DefaultTenantVariable = "AUTOPILOT_ADO_TENANT_ID";
    public const string DefaultClientIdVariable = "AUTOPILOT_ADO_CLIENT_ID";
    public const string DefaultClientSecretVariable = "AUTOPILOT_ADO_CLIENT_SECRET";

    /// <summary>The tag a person adds to a work item so that the default query picks it up.</summary>
    public const string DefaultQueryTag = "dev-autopilot";

    /// <summary>Open items in the project tagged "dev-autopilot", newest first. The client asks for at most 50.</summary>
    public const string DefaultQuery =
        "SELECT [System.Id] FROM WorkItems " +
        "WHERE [System.TeamProject] = @project " +
        "AND [System.State] NOT IN ('Closed', 'Done', 'Removed') " +
        "AND [System.Tags] CONTAINS '" + DefaultQueryTag + "' " +
        "ORDER BY [System.CreatedDate] DESC";

    /// <summary>For example <c>https://dev.azure.com/contoso</c>, without a trailing slash.</summary>
    public required Uri OrganizationUrl { get; init; }

    /// <summary>Project name or id.</summary>
    public required string Project { get; init; }

    /// <summary>Repository name or id. Needed by the publisher and the repository checks.</summary>
    public string? Repository { get; init; }

    public string Remote { get; init; } = DefaultRemote;

    public string Query { get; init; } = DefaultQuery;

    public string TagPrefix { get; init; } = DefaultTagPrefix;

    public AzureDevOpsAuth Auth { get; init; } = AzureDevOpsAuth.Pat;

    public string TokenVariable { get; init; } = DefaultTokenVariable;

    /// <summary>For <see cref="AzureDevOpsAuth.ServicePrincipal"/>: names of the variables, never their values.</summary>
    public string TenantVariable { get; init; } = DefaultTenantVariable;

    public string ClientIdVariable { get; init; } = DefaultClientIdVariable;

    public string ClientSecretVariable { get; init; } = DefaultClientSecretVariable;

    /// <summary>
    /// <c>remote</c> (default): triage writes a tag and a comment on the work item. <c>local</c>: triage
    /// results stay in <c>.autopilot/out/triage/</c> and nothing is written to Azure DevOps.
    /// </summary>
    public TriageRecording RecordTriage { get; init; } = TriageRecording.Remote;

    /// <summary>True when the query is the built-in one, so <see cref="DefaultQueryTag"/> is the tag that selects items.</summary>
    public bool UsesDefaultQuery => string.Equals(Query, DefaultQuery, StringComparison.Ordinal);

    /// <summary>One line for doctor: where triage results go.</summary>
    public string RecordTriageDescription => RecordTriage == TriageRecording.Local
        ? "local: triage results stay in .autopilot/out/triage/; nothing is written to Azure DevOps"
        : $"remote: triage adds a {TagPrefix}agent-ready or {TagPrefix}needs-human tag and a comment to each work item (set \"recordTriage\": \"local\" to write nothing)";

    /// <summary>
    /// Reads and checks the settings block. Every problem is listed in one
    /// <see cref="AzureDevOpsSettingsException"/>, in plain words.
    /// </summary>
    public static AzureDevOpsSettings FromModule(ModuleConfig module, bool requireRepository)
    {
        ArgumentNullException.ThrowIfNull(module);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in module.Settings)
        {
            values[key] = value;
        }

        var problems = new List<string>();
        string? Get(string key) => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        Uri? organization = null;
        var orgText = Get("organizationUrl");
        if (orgText is null)
        {
            problems.Add("Set \"organizationUrl\" to your organisation's address, for example \"https://dev.azure.com/contoso\".");
        }
        else if (!Uri.TryCreate(orgText.TrimEnd('/'), UriKind.Absolute, out organization)
            || organization.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(organization.Query)
            || !string.IsNullOrEmpty(organization.Fragment)
            || !string.IsNullOrEmpty(organization.UserInfo))
        {
            problems.Add($"\"organizationUrl\" must be an https address such as \"https://dev.azure.com/contoso\", without a query or user name; got \"{orgText}\".");
            organization = null;
        }

        var project = Get("project");
        if (project is null)
        {
            problems.Add("Set \"project\" to the Azure DevOps project name, for example \"Shop\".");
        }

        var repository = Get("repository");
        if (repository is null && requireRepository)
        {
            problems.Add("Set \"repository\" to the name of the Git repository the agent pushes to, for example \"shop-api\".");
        }

        var auth = AzureDevOpsAuth.Pat;
        switch (Get("auth")?.ToLowerInvariant())
        {
            case null:
            case "pat":
                break;
            case "az":
                auth = AzureDevOpsAuth.AzureCli;
                break;
            case "service-principal":
                auth = AzureDevOpsAuth.ServicePrincipal;
                break;
            default:
                problems.Add($"\"auth\" must be \"pat\" (token from a user environment variable), \"az\" (Azure CLI sign-in) or \"service-principal\" (Entra client credentials); got \"{Get("auth")}\".");
                break;
        }

        var tokenVariable = Get("tokenVariable") ?? DefaultTokenVariable;
        if (!tokenVariable.StartsWith(RequiredTokenVariablePrefix, StringComparison.Ordinal)
            || tokenVariable.Length == RequiredTokenVariablePrefix.Length
            || !tokenVariable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            problems.Add($"\"tokenVariable\" must be an environment variable name that starts with {RequiredTokenVariablePrefix} and has only letters, digits and '_', for example \"{DefaultTokenVariable}\"; got \"{tokenVariable}\".");
        }

        string VariableName(string key, string fallback)
        {
            var name = Get(key) ?? fallback;
            if (!name.StartsWith(RequiredTokenVariablePrefix, StringComparison.Ordinal)
                || name.Length == RequiredTokenVariablePrefix.Length
                || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                problems.Add($"\"{key}\" must be an environment variable name that starts with {RequiredTokenVariablePrefix} and has only letters, digits and '_', for example \"{fallback}\"; got \"{name}\". Put the name here, never the value.");
            }

            return name;
        }

        var tenantVariable = VariableName("tenantVariable", DefaultTenantVariable);
        var clientIdVariable = VariableName("clientIdVariable", DefaultClientIdVariable);
        var clientSecretVariable = VariableName("clientSecretVariable", DefaultClientSecretVariable);

        var remote = Get("remote") ?? DefaultRemote;
        if (remote.StartsWith('-') || !remote.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
        {
            problems.Add($"\"remote\" must be the name of a git remote such as \"origin\"; got \"{remote}\".");
        }

        var recordTriage = TriageRecording.Remote;
        switch (Get("recordTriage")?.ToLowerInvariant())
        {
            case null:
            case "remote":
                break;
            case "local":
                recordTriage = TriageRecording.Local;
                break;
            default:
                problems.Add($"\"recordTriage\" must be \"remote\" (a tag and a comment on the work item) or \"local\" (only .autopilot/out/triage/, nothing written to Azure DevOps); got \"{Get("recordTriage")}\".");
                break;
        }

        var tagPrefix = Get("tagPrefix") ?? DefaultTagPrefix;
        if (tagPrefix.Contains(';', StringComparison.Ordinal))
        {
            problems.Add("\"tagPrefix\" cannot contain ';' because Azure DevOps separates tags with it.");
        }

        if (problems.Count > 0)
        {
            throw new AzureDevOpsSettingsException(problems);
        }

        return new AzureDevOpsSettings
        {
            OrganizationUrl = organization!,
            Project = project!,
            Repository = repository,
            Remote = remote,
            Query = Get("query") ?? DefaultQuery,
            TagPrefix = tagPrefix,
            Auth = auth,
            TokenVariable = tokenVariable,
            TenantVariable = tenantVariable,
            ClientIdVariable = clientIdVariable,
            ClientSecretVariable = clientSecretVariable,
            RecordTriage = recordTriage,
        };
    }
}

/// <summary>The settings block is incomplete or wrong. <see cref="Exception.Message"/> lists every problem.</summary>
public sealed class AzureDevOpsSettingsException(IReadOnlyList<string> problems)
    : Exception("The Azure DevOps settings in .autopilot/autopilot.json need fixing:" + Environment.NewLine
        + string.Join(Environment.NewLine, problems.Select(p => "- " + p)))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
