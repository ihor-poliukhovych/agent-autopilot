using Autopilot.Core;
using Autopilot.Core.Config;

namespace Autopilot.AzureDevOps;

/// <summary>Test seams for <see cref="AzureDevOpsModule.Create"/>. Leave null in real use.</summary>
public sealed record AzureDevOpsModuleOptions
{
    /// <summary>HTTP handler (tests pass a fake one). Default: a new <see cref="HttpClientHandler"/>.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Token provider. Default: from the <c>auth</c> and <c>tokenVariable</c> settings.</summary>
    public IAzureDevOpsTokenProvider? Tokens { get; init; }

    /// <summary>Environment lookup for the PAT provider.</summary>
    public Func<string, string?>? EnvironmentLookup { get; init; }

    /// <summary>Delay used between retries.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    public IGitPusher? Pusher { get; init; }
}

/// <summary>
/// Builds the Azure DevOps work item source, publisher and doctor from one settings block.
/// Owns the HTTP client; dispose it when the command ends.
/// </summary>
public sealed class AzureDevOpsModule : IDisposable
{
    /// <summary>The value of <c>modules.workItems.type</c> / <c>modules.publisher.type</c> that selects this module.</summary>
    public const string TypeName = "azure-devops";

    private readonly HttpClient _http;
    private readonly string _projectRoot;
    private readonly string _baseBranch;
    private readonly string _branchPrefix;
    private readonly IGitPusher? _pusher;

    private AzureDevOpsModule(HttpClient http, AzureDevOpsClient client, AzureDevOpsSettings settings, string projectRoot, string baseBranch, string branchPrefix, IGitPusher? pusher)
    {
        _http = http;
        Client = client;
        Settings = settings;
        _projectRoot = projectRoot;
        _baseBranch = baseBranch;
        _branchPrefix = branchPrefix;
        _pusher = pusher;
        WorkItems = new AzureDevOpsWorkItemSource(client, settings);
        Doctor = new AzureDevOpsDoctor(client, settings, baseBranch);
    }

    public AzureDevOpsSettings Settings { get; }

    public AzureDevOpsClient Client { get; }

    public IWorkItemSource WorkItems { get; }

    public AzureDevOpsDoctor Doctor { get; }

    /// <summary>The draft pull request publisher. Throws <see cref="AzureDevOpsSettingsException"/> when "repository" is not set.</summary>
    public IPublisher CreatePublisher() =>
        new AzureDevOpsPublisher(Client, Settings, _projectRoot, _baseBranch, _branchPrefix, _pusher);

    /// <summary>True when <paramref name="module"/> selects Azure DevOps.</summary>
    public static bool IsSelected(ModuleConfig module) =>
        module is not null && (string.Equals(module.Type, TypeName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(module.Type, "azuredevops", StringComparison.OrdinalIgnoreCase)
            || string.Equals(module.Type, "ado", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Validates the settings (every missing or wrong value in one <see cref="AzureDevOpsSettingsException"/>)
    /// and builds the module.
    /// </summary>
    /// <param name="requireRepository">True when the publisher will be used; then "repository" must be set.</param>
    public static AzureDevOpsModule Create(
        ModuleConfig module,
        string projectRoot,
        string baseBranch,
        string branchPrefix = "autopilot/",
        bool requireRepository = true,
        AzureDevOpsModuleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchPrefix);
        options ??= new AzureDevOpsModuleOptions();

        var settings = AzureDevOpsSettings.FromModule(module, requireRepository);
        IAzureDevOpsTokenProvider tokens = options.Tokens ?? (settings.Auth == AzureDevOpsAuth.AzureCli
            ? new AzureCliTokenProvider()
            : new EnvironmentTokenProvider(settings.TokenVariable, options.EnvironmentLookup));

        var http = options.Handler is null
            ? new HttpClient()
            : new HttpClient(options.Handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(100);
        var client = new AzureDevOpsClient(http, settings.OrganizationUrl, settings.Project, tokens, options.Delay);
        return new AzureDevOpsModule(http, client, settings, Path.GetFullPath(projectRoot), baseBranch, branchPrefix, options.Pusher);
    }

    public void Dispose() => _http.Dispose();
}
