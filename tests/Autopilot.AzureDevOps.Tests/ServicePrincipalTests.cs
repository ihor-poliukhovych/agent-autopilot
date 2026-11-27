using System.Net;
using System.Text.Json;

namespace Autopilot.AzureDevOps.Tests;

public sealed class ServicePrincipalTests
{
    private const string Secret = "dummy-secret-value-123";

    private static string? Lookup(string name) => name switch
    {
        "AUTOPILOT_ADO_TENANT_ID" => "contoso.onmicrosoft.com",
        "AUTOPILOT_ADO_CLIENT_ID" => "11111111-1111-1111-1111-111111111111",
        "AUTOPILOT_ADO_CLIENT_SECRET" => Secret,
        _ => null,
    };

    private static ServicePrincipalTokenProvider Provider(FakeHandler handler, Func<string, string?>? lookup = null) =>
        new(new HttpClient(handler), "AUTOPILOT_ADO_TENANT_ID", "AUTOPILOT_ADO_CLIENT_ID", "AUTOPILOT_ADO_CLIENT_SECRET", lookup ?? Lookup);

    [Fact]
    public async Task Gets_a_bearer_token_with_client_credentials_for_the_azure_devops_scope()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, "https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/token", """{ "access_token": "eyJ.dummy.sig", "expires_in": 3599 }""");

        var credential = await Provider(handler).GetCredentialAsync(default);

        Assert.True(credential.IsBearer);
        Assert.Equal("Bearer eyJ.dummy.sig", credential.ToAuthorizationHeader().ToString());
        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("grant_type=client_credentials", body);
        Assert.Contains("scope=499b84ac-1321-427f-aa17-267ca6975798%2F.default", body);
    }

    [Fact]
    public async Task A_missing_variable_is_named_and_no_request_is_sent()
    {
        var handler = new FakeHandler();

        var error = await Assert.ThrowsAsync<AzureDevOpsAuthException>(() =>
            Provider(handler, name => name == "AUTOPILOT_ADO_CLIENT_SECRET" ? null : Lookup(name)).GetCredentialAsync(default));

        Assert.Contains("AUTOPILOT_ADO_CLIENT_SECRET", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_refusal_never_echoes_the_secret()
    {
        var handler = new FakeHandler().On(HttpMethod.Post, "https://login.microsoftonline.com/", $$"""{ "error": "invalid_client", "error_description": "bad {{Secret}}" }""", HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAsync<AzureDevOpsAuthException>(() => Provider(handler).GetCredentialAsync(default));

        Assert.Contains("401", error.Message);
        Assert.DoesNotContain(Secret, error.Message);
    }

    [Fact]
    public async Task A_tenant_that_could_change_the_address_is_refused()
    {
        var handler = new FakeHandler();

        await Assert.ThrowsAsync<AzureDevOpsAuthException>(() =>
            Provider(handler, name => name == "AUTOPILOT_ADO_TENANT_ID" ? "evil.example/path?x=" : Lookup(name)).GetCredentialAsync(default));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Settings_accept_service_principal_with_default_or_custom_variable_names_and_never_values()
    {
        var settings = AzureDevOpsSettings.FromModule(Ado.Module(("organizationUrl", Ado.Org), ("project", Ado.Project), ("auth", "service-principal")), requireRepository: false);
        Assert.Equal(AzureDevOpsAuth.ServicePrincipal, settings.Auth);
        Assert.Equal("AUTOPILOT_ADO_CLIENT_SECRET", settings.ClientSecretVariable);

        var custom = AzureDevOpsSettings.FromModule(Ado.Module(
            ("organizationUrl", Ado.Org), ("project", Ado.Project), ("auth", "service-principal"), ("clientSecretVariable", "AUTOPILOT_SHOP_SECRET")), false);
        Assert.Equal("AUTOPILOT_SHOP_SECRET", custom.ClientSecretVariable);
    }

    [Theory]
    [InlineData("tenantVariable", "TENANT")]
    [InlineData("clientIdVariable", "11111111-1111-1111-1111-111111111111")]
    [InlineData("clientSecretVariable", "s3cret value")]
    public void Settings_refuse_a_value_where_a_variable_name_belongs(string key, string value)
    {
        var error = Assert.Throws<AzureDevOpsSettingsException>(() => AzureDevOpsSettings.FromModule(
            Ado.Module(("organizationUrl", Ado.Org), ("project", Ado.Project), ("auth", "service-principal"), (key, value)), false));

        Assert.Contains(key, error.Message);
        Assert.Contains("never the value", error.Message);
    }

    [Fact]
    public void A_work_item_carries_link_state_and_assignee()
    {
        var item = new AdoWorkItem(42, 3, new Dictionary<string, JsonElement>
        {
            ["System.Title"] = JsonDocument.Parse("\"Fix it\"").RootElement,
            ["System.State"] = JsonDocument.Parse("\"Active\"").RootElement,
            ["System.AssignedTo"] = JsonDocument.Parse("""{ "displayName": "Ann Example", "uniqueName": "ann@example.com" }""").RootElement,
        });

        var workItem = AzureDevOpsWorkItemSource.ToWorkItem(item, Ado.Settings());

        Assert.Equal($"{Ado.Org}/Shop/_workitems/edit/42", workItem.Url);
        Assert.Equal("Active", workItem.State);
        Assert.Equal("Ann Example", workItem.AssignedTo);
        Assert.Null(AzureDevOpsWorkItemSource.ToWorkItem(item).Url);
    }
}
