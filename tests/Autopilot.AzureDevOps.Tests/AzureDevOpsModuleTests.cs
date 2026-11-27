using Autopilot.Core.Config;
using Autopilot.Core.Process;

namespace Autopilot.AzureDevOps.Tests;

public class AzureDevOpsModuleTests
{
    [Fact]
    public void Empty_settings_list_every_missing_value_in_plain_words()
    {
        var ex = Assert.Throws<AzureDevOpsSettingsException>(() => AzureDevOpsSettings.FromModule(new ModuleConfig { Type = "azure-devops" }, requireRepository: true));

        Assert.Equal(3, ex.Problems.Count);
        Assert.Contains("organizationUrl", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"project\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"repository\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https://dev.azure.com/contoso", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_apply_when_only_the_required_values_are_set()
    {
        var settings = AzureDevOpsSettings.FromModule(
            Ado.Module(("organizationUrl", "https://dev.azure.com/contoso/"), ("project", "Shop")), requireRepository: false);

        Assert.Equal("https://dev.azure.com/contoso", settings.OrganizationUrl.AbsoluteUri.TrimEnd('/'));
        Assert.Null(settings.Repository);
        Assert.Equal("origin", settings.Remote);
        Assert.Equal("autopilot:", settings.TagPrefix);
        Assert.Equal(AzureDevOpsAuth.Pat, settings.Auth);
        Assert.Equal("AUTOPILOT_ADO_PAT", settings.TokenVariable);
        Assert.Equal(AzureDevOpsSettings.DefaultQuery, settings.Query);
    }

    [Fact]
    public void The_query_tag_is_a_setting_and_an_older_stored_tag_keeps_selecting_its_items()
    {
        var older = AzureDevOpsSettings.FromModule(
            Ado.Module(("organizationUrl", "https://dev.azure.com/contoso"), ("project", "Shop"), ("queryTag", "dev-autopilot")), requireRepository: false);

        Assert.Equal("dev-autopilot", older.QueryTag);
        Assert.Contains("[System.Tags] CONTAINS 'dev-autopilot'", older.Query, StringComparison.Ordinal);
        Assert.True(older.UsesDefaultQuery);

        var custom = AzureDevOpsSettings.FromModule(
            Ado.Module(("organizationUrl", "https://dev.azure.com/contoso"), ("project", "Shop"), ("query", "SELECT [System.Id] FROM WorkItems"), ("queryTag", "x")), requireRepository: false);
        Assert.Equal("SELECT [System.Id] FROM WorkItems", custom.Query);
        Assert.False(custom.UsesDefaultQuery);

        Assert.Throws<AzureDevOpsSettingsException>(() => AzureDevOpsSettings.FromModule(
            Ado.Module(("organizationUrl", "https://dev.azure.com/contoso"), ("project", "Shop"), ("queryTag", "a'b")), requireRepository: false));
    }

    [Fact]
    public void All_settings_are_read_case_insensitively()
    {
        var settings = AzureDevOpsSettings.FromModule(Ado.Module(
            ("OrganizationUrl", "https://dev.azure.com/contoso"),
            ("project", "Shop"),
            ("repository", "shop-api"),
            ("remote", "ado"),
            ("query", "SELECT [System.Id] FROM WorkItems"),
            ("tagPrefix", "ai/"),
            ("auth", "az"),
            ("tokenVariable", "AUTOPILOT_SHOP_PAT")), requireRepository: true);

        Assert.Equal(("shop-api", "ado", "ai/", AzureDevOpsAuth.AzureCli, "AUTOPILOT_SHOP_PAT"),
            (settings.Repository, settings.Remote, settings.TagPrefix, settings.Auth, settings.TokenVariable));
        Assert.Equal("SELECT [System.Id] FROM WorkItems", settings.Query);
    }

    [Theory]
    [InlineData("organizationUrl", "http://dev.azure.com/contoso", "https")]
    [InlineData("organizationUrl", "dev.azure.com/contoso", "https")]
    [InlineData("organizationUrl", "https://user:pw@dev.azure.com/contoso", "user name")]
    [InlineData("tokenVariable", "ADO_PAT", "AUTOPILOT_")]
    [InlineData("tokenVariable", "AUTOPILOT_", "AUTOPILOT_")]
    [InlineData("auth", "password", "\"pat\"")]
    [InlineData("remote", "--upload-pack=x", "git remote")]
    [InlineData("tagPrefix", "a;b", "';'")]
    public void Wrong_values_are_explained(string key, string value, string expected)
    {
        var values = new Dictionary<string, string>
        {
            ["organizationUrl"] = "https://dev.azure.com/contoso",
            ["project"] = "Shop",
            ["repository"] = "shop-api",
            [key] = value,
        };

        var ex = Assert.Throws<AzureDevOpsSettingsException>(() =>
            AzureDevOpsSettings.FromModule(new ModuleConfig { Type = "azure-devops", Settings = values }, requireRepository: true));

        Assert.Single(ex.Problems);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_builds_source_publisher_and_doctor_that_share_one_client()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson());
        using var module = AzureDevOpsModule.Create(
            Ado.Module(("organizationUrl", Ado.Org), ("project", "Shop"), ("repository", "shop-api")),
            projectRoot: Path.GetTempPath(),
            baseBranch: "main",
            options: new AzureDevOpsModuleOptions
            {
                Handler = handler,
                EnvironmentLookup = name => name == "AUTOPILOT_ADO_PAT" ? Ado.Token : null,
            });

        Assert.Empty(await module.WorkItems.ListOpenAsync(default));
        Assert.IsType<AzureDevOpsPublisher>(module.CreatePublisher());
        Assert.NotNull(module.Doctor);
        Assert.Equal("Basic " + Ado.TokenBase64, handler.Requests[0].Authorization);
        Assert.True(AzureDevOpsModule.IsSelected(new ModuleConfig { Type = "azure-devops" }));
        Assert.False(AzureDevOpsModule.IsSelected(new ModuleConfig { Type = "local" }));
    }

    [Fact]
    public void Work_items_only_module_does_not_need_a_repository_but_its_publisher_does()
    {
        using var module = AzureDevOpsModule.Create(
            Ado.Module(("organizationUrl", Ado.Org), ("project", "Shop")),
            Path.GetTempPath(), "main", requireRepository: false,
            options: new AzureDevOpsModuleOptions { Handler = new FakeHandler() });

        Assert.NotNull(module.WorkItems);
        Assert.Throws<AzureDevOpsSettingsException>(() => module.CreatePublisher());
    }

    [Fact]
    public async Task Missing_environment_variable_says_how_to_set_it()
    {
        var provider = new EnvironmentTokenProvider("AUTOPILOT_SHOP_PAT", _ => "  ");

        var ex = await Assert.ThrowsAsync<AzureDevOpsAuthException>(() => provider.GetCredentialAsync(default));

        Assert.Contains("AUTOPILOT_SHOP_PAT", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'User'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Environment_provider_only_accepts_autopilot_variables() =>
        Assert.Throws<ArgumentException>(() => new EnvironmentTokenProvider("SYSTEM_ACCESSTOKEN"));

    [Fact]
    public async Task Azure_cli_provider_asks_for_the_azure_devops_resource_and_returns_a_bearer_token()
    {
        IReadOnlyList<string>? seen = null;
        var provider = new AzureCliTokenProvider((args, _) =>
        {
            seen = args;
            return Task.FromResult(new ProcessResult(0, "eyJhbGciOi.dummy.sig\n", string.Empty, string.Empty, false, TimeSpan.Zero));
        });

        var credential = await provider.GetCredentialAsync(default);

        Assert.True(credential.IsBearer);
        Assert.Equal("Bearer eyJhbGciOi.dummy.sig", credential.ToAuthorizationHeader().ToString());
        Assert.Equal(["account", "get-access-token", "--resource", "499b84ac-1321-427f-aa17-267ca6975798", "--query", "accessToken", "-o", "tsv"], seen);
    }

    [Fact]
    public async Task Azure_cli_failure_says_to_sign_in()
    {
        var provider = new AzureCliTokenProvider((_, _) =>
            Task.FromResult(new ProcessResult(1, string.Empty, "Please run 'az login' to setup account.", string.Empty, false, TimeSpan.Zero)));

        var ex = await Assert.ThrowsAsync<AzureDevOpsAuthException>(() => provider.GetCredentialAsync(default));

        Assert.Contains("az login", ex.Message, StringComparison.Ordinal);
    }
}
