using DevAutopilot.Core;

namespace DevAutopilot.AzureDevOps.Tests;

/// <summary><c>recordTriage</c>: "remote" writes a tag and a comment, "local" writes nothing to Azure DevOps.</summary>
public class ReadOnlyTriageTests
{
    private const string BatchJson = """
        {"count":1,"value":[{"id":42,"rev":5,"fields":{"System.Id":42,"System.Title":"Return 404 for unknown products","System.Description":"<p>500 today.</p>","System.Tags":"dev-autopilot"},"url":"https://dev.azure.com/contoso/_apis/wit/workItems/42"}]}
        """;

    private static readonly TriageResult Ready = new(TriageLabel.AgentReady, "Small.", ["GET /products/999 returns 404"], RiskLevel.Low);

    private static FakeHandler Handler() => new FakeHandler()
        .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson(42))
        .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workitemsbatch", BatchJson)
        .On(HttpMethod.Patch, $"{Ado.Org}/Shop/_apis/wit/workitems/42", "{\"id\":42,\"rev\":6,\"fields\":{}}")
        .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/workItems/42/comments", "{\"workItemId\":42,\"id\":1,\"version\":1,\"text\":\"x\"}");

    /// <summary>Requests that change something in Azure DevOps: every PATCH, and every POST except the two read endpoints.</summary>
    private static List<RecordedRequest> Writes(FakeHandler handler) => handler.Requests
        .Where(r => r.Method == HttpMethod.Patch
            || r.Method == HttpMethod.Put
            || r.Method == HttpMethod.Delete
            || (r.Method == HttpMethod.Post
                && !r.Uri.AbsolutePath.EndsWith("/_apis/wit/wiql", StringComparison.Ordinal)
                && !r.Uri.AbsolutePath.EndsWith("/_apis/wit/workitemsbatch", StringComparison.Ordinal)))
        .ToList();

    [Fact]
    public void RecordTriage_defaults_to_remote_and_reads_local()
    {
        var defaults = AzureDevOpsSettings.FromModule(Ado.Module(("organizationUrl", Ado.Org), ("project", Ado.Project)), requireRepository: false);
        var local = AzureDevOpsSettings.FromModule(Ado.Module(("organizationUrl", Ado.Org), ("project", Ado.Project), ("recordTriage", "Local")), requireRepository: false);

        Assert.Equal(TriageRecording.Remote, defaults.RecordTriage);
        Assert.Equal(TriageRecording.Local, local.RecordTriage);
        Assert.StartsWith("remote:", defaults.RecordTriageDescription, StringComparison.Ordinal);
        Assert.StartsWith("local:", local.RecordTriageDescription, StringComparison.Ordinal);
        Assert.Contains("nothing is written to Azure DevOps", local.RecordTriageDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_recordTriage_value_is_a_settings_problem()
    {
        var ex = Assert.Throws<AzureDevOpsSettingsException>(() =>
            AzureDevOpsSettings.FromModule(Ado.Module(("organizationUrl", Ado.Org), ("project", Ado.Project), ("recordTriage", "both")), requireRepository: false));

        Assert.Contains("\"recordTriage\" must be \"remote\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Local_mode_sends_no_request_at_all_when_recording_triage()
    {
        var handler = Handler();
        var source = new AzureDevOpsWorkItemSource(Ado.Client(handler), Ado.Settings() with { RecordTriage = TriageRecording.Local });

        await source.RecordTriageAsync("42", Ready, default);
        await source.RecordTriageAsync("42", Ready with { Label = TriageLabel.NeedsHuman }, default);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Local_mode_reads_items_but_never_patches_or_posts_a_comment()
    {
        var handler = Handler();
        using var module = AzureDevOpsModule.Create(
            Ado.Module(("organizationUrl", Ado.Org), ("project", Ado.Project), ("recordTriage", "local")),
            Path.GetTempPath(), "main", requireRepository: false,
            options: new AzureDevOpsModuleOptions { Handler = handler, Tokens = new FixedTokenProvider(Ado.Token) });

        var items = await module.WorkItems.ListOpenAsync(default);
        var item = await module.WorkItems.GetAsync("42", default);
        await module.WorkItems.RecordTriageAsync("42", Ready, default);

        Assert.Single(items);
        Assert.NotNull(item);
        Assert.NotEmpty(handler.Requests);
        Assert.Empty(Writes(handler));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.AbsolutePath.Contains("/comments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remote_mode_still_writes_the_tag_and_the_comment()
    {
        var handler = Handler();
        var source = new AzureDevOpsWorkItemSource(Ado.Client(handler), Ado.Settings());

        await source.RecordTriageAsync("42", Ready, default);

        var writes = Writes(handler);
        Assert.Contains(writes, r => r.Method == HttpMethod.Patch);
        Assert.Contains(writes, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_query_selects_the_dev_autopilot_tag()
    {
        Assert.Equal("dev-autopilot", AzureDevOpsSettings.DefaultQueryTag);
        Assert.Contains("[System.Tags] CONTAINS 'dev-autopilot'", AzureDevOpsSettings.DefaultQuery, StringComparison.Ordinal);
        Assert.True(Ado.Settings().UsesDefaultQuery);
        Assert.False((Ado.Settings() with { Query = "SELECT [System.Id] FROM WorkItems" }).UsesDefaultQuery);
    }

    [Fact]
    public async Task Doctor_names_the_custom_query_when_one_is_set()
    {
        var handler = new FakeHandler()
            .On(HttpMethod.Get, $"{Ado.Org}/_apis/projects/Shop", Ado.ProjectJson)
            .On(HttpMethod.Post, $"{Ado.Org}/Shop/_apis/wit/wiql", Ado.WiqlJson());
        var settings = Ado.Settings(repository: null) with { Query = "SELECT [System.Id] FROM WorkItems WHERE [System.Tags] CONTAINS 'mine'" };

        var checks = await new AzureDevOpsDoctor(Ado.Client(handler), settings, "main").RunAsync(default);

        var query = Assert.Single(checks, c => c.Name == "Work item query");
        Assert.Equal("runs, but no item matches yet (custom query)", query.Detail);
    }
}
