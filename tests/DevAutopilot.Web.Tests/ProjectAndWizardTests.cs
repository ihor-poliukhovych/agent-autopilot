using System.Net;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Workspace;

namespace DevAutopilot.Web.Tests;

public sealed class ProjectAndWizardTests
{
    [Fact]
    public async Task A_project_is_created_from_the_wizard_draft_and_listed()
    {
        await using var ui = new UiFixture();
        var created = await ui.CreateProjectAsync();
        Assert.Equal("demo", created.GetProperty("key").GetString());

        var (_, state) = await ui.GetAsync("/api/state");
        Assert.Equal("demo", state.GetProperty("projects")[0].GetProperty("key").GetString());
        Assert.True(state.GetProperty("azureDevOps").GetBoolean());

        var record = new ProjectRegistry(ui.Workspace).Get("demo")!;
        Assert.Equal("local", record.TaskSource);
        var config = ConfigLoader.Load(ui.RepoFolder, ui.Workspace.ProjectFolder("demo"));
        Assert.Equal("make test", config.Commands.Test);
        Assert.Equal(MergeConfig.Human, config.Merge.By);
        // Nothing is written into the repository.
        Assert.Equal([".git"], Directory.GetFileSystemEntries(ui.RepoFolder).Select(p => Path.GetFileName(p)!).ToArray());
    }

    [Fact]
    public async Task A_duplicate_key_and_a_bad_key_are_refused_with_plain_words()
    {
        await using var ui = new UiFixture();
        await ui.CreateProjectAsync();
        var (status, body) = await ui.PostAsync("/api/projects", ui.Draft("demo"));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("already exists", body.GetProperty("error").GetString(), StringComparison.Ordinal);

        var (badStatus, bad) = await ui.PostAsync("/api/projects", ui.Draft("Bad Key"));
        Assert.Equal(HttpStatusCode.BadRequest, badStatus);
        Assert.Contains("lower-case", bad.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_is_refused_when_a_check_fails_even_if_the_page_did_not_run_it()
    {
        await using var ui = new UiFixture();
        ui.Checker.Lines = _ => [new CheckLine(CheckLine.Fail, "config", "commands.build is empty", null)];
        var (status, body) = await ui.PostAsync("/api/projects", ui.Draft());
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("config", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(new ProjectRegistry(ui.Workspace).List());
    }

    [Fact]
    public async Task A_step_shows_only_its_own_checks_and_a_plain_fix_when_it_fails()
    {
        await using var ui = new UiFixture();
        ui.Checker.Lines = r =>
        [
            new CheckLine(CheckLine.Ok, "git repository", r.RepositoryFolder),
            new CheckLine(CheckLine.Fail, "base branch", "branch \"dev\" does not exist", "Create it, or set baseBranch in .autopilot/autopilot.json."),
            new CheckLine(CheckLine.Ok, "build command", "make"),
        ];
        var (status, body) = await ui.PostAsync("/api/wizard/check", new { step = 1, draft = ui.Draft() });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("passed").GetBoolean());
        var names = body.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("base branch", names);
        Assert.DoesNotContain("build command", names);
        var branch = body.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "base branch");
        var fix = branch.GetProperty("fix").GetString()!;
        Assert.DoesNotContain("autopilot.json", fix, StringComparison.Ordinal);
        Assert.Contains("branch", fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_build_check_runs_in_step_2_and_not_in_other_steps_and_uses_a_temp_copy_of_the_draft()
    {
        await using var ui = new UiFixture();
        await ui.PostAsync("/api/wizard/check", new { step = 1, draft = ui.Draft() });
        await ui.PostAsync("/api/wizard/check", new { step = 2, draft = ui.Draft() });
        Assert.True(ui.Checker.Requests[0].SkipBuild);
        Assert.False(ui.Checker.Requests[1].SkipBuild);
        Assert.False(Directory.Exists(ui.Checker.Requests[1].DataFolder), "the temp folder is removed afterwards");
    }

    [Fact]
    public async Task A_missing_folder_fails_step_1_without_calling_doctor()
    {
        await using var ui = new UiFixture();
        var draft = new { key = "demo", repositoryFolder = Path.Combine(ui.RepoFolder, "nope"), baseBranch = "main" };
        var (_, body) = await ui.PostAsync("/api/wizard/check", new { step = 1, draft });
        Assert.False(body.GetProperty("passed").GetBoolean());
        Assert.Empty(ui.Checker.Requests);
        Assert.Contains("Type the full path", body.GetProperty("checks")[0].GetProperty("fix").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Defaults_come_from_the_folder()
    {
        await using var ui = new UiFixture();
        var (status, body) = await ui.PostAsync("/api/wizard/defaults", new { folder = ui.RepoFolder, baseBranch = "trunk" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("trunk", body.GetProperty("baseBranch").GetString());
        Assert.Equal("make", body.GetProperty("buildCommand").GetString());
        Assert.Equal("repo", body.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Azure_DevOps_settings_keep_only_variable_names_and_a_pasted_token_is_refused()
    {
        await using var ui = new UiFixture();
        var ado = new { organizationUrl = "https://dev.azure.com/acme", project = "Shop", repository = "shop", auth = "pat", tokenVariable = "AUTOPILOT_ADO_PAT" };
        var (ok, _) = await ui.PostAsync("/api/projects", ui.Draft("shop", "azure-devops", ado));
        Assert.Equal(HttpStatusCode.Created, ok);
        var config = ConfigLoader.Load(ui.RepoFolder, ui.Workspace.ProjectFolder("shop"));
        Assert.Equal("azure-devops", config.Modules.WorkItems.Type);
        Assert.Equal("AUTOPILOT_ADO_PAT", config.Modules.WorkItems.Settings["tokenVariable"]);

        var pasted = new { organizationUrl = "https://dev.azure.com/acme", project = "Shop", repository = "shop", auth = "pat", tokenVariable = "ghp_abcdefghijklmnopqrstuvwxyz0123456789" };
        var (status, body) = await ui.PostAsync("/api/projects", ui.Draft("shop2", "azure-devops", pasted));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("NAME", body.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", File.ReadAllText(ConfigLoader.ConfigPath(ui.RepoFolder, ui.Workspace.ProjectFolder("shop"))), StringComparison.Ordinal);
        Assert.Null(new ProjectRegistry(ui.Workspace).Get("shop2"));
    }

    [Fact]
    public async Task The_Azure_DevOps_step_reports_whether_the_variable_is_set_without_reading_its_value()
    {
        await using var ui = new UiFixture();
        var ado = new { organizationUrl = "https://dev.azure.com/acme", project = "Shop", repository = "shop", auth = "pat", tokenVariable = "AUTOPILOT_ADO_PAT" };
        var (_, set) = await ui.PostAsync("/api/wizard/check", new { step = 4, draft = ui.Draft("shop", "azure-devops", ado) });
        var env = set.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "environment");
        Assert.Equal("ok", env.GetProperty("status").GetString());
        Assert.DoesNotContain("\"x\"", env.GetRawText(), StringComparison.Ordinal);

        var other = new { organizationUrl = "https://dev.azure.com/acme", project = "Shop", repository = "shop", auth = "pat", tokenVariable = "AUTOPILOT_OTHER" };
        var (_, unset) = await ui.PostAsync("/api/wizard/check", new { step = 4, draft = ui.Draft("shop", "azure-devops", other) });
        Assert.False(unset.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task The_Azure_DevOps_preview_calls_the_probe_with_the_draft_settings()
    {
        await using var ui = new UiFixture();
        var ado = new { organizationUrl = "https://dev.azure.com/acme", project = "Shop", repository = "shop", auth = "az" };
        var (status, page) = await ui.PostAsync("/api/wizard/azure-devops/preview", new { draft = ui.Draft("shop", "azure-devops", ado), skip = 10 });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Fix the login page", page.GetProperty("items")[0].GetProperty("title").GetString());
        Assert.Equal(11, page.GetProperty("total").GetInt32());
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        Assert.Equal(10, ui.Ado.SeenSkip);
        Assert.Equal("azure-devops", ui.Ado.Seen!.Type);
        Assert.Equal("Shop", ui.Ado.Seen.Settings["project"]);
    }

    [Fact]
    public async Task Settings_are_saved_through_the_same_draft_and_bad_values_change_nothing()
    {
        await using var ui = new UiFixture();
        await ui.CreateProjectAsync();
        var (_, draft) = await ui.GetAsync("/api/projects/demo/setup");
        Assert.Equal("human", draft.GetProperty("mergeBy").GetString());

        var changed = new Dictionary<string, object?>
        {
            ["buildCommand"] = "make all", ["testCommand"] = "make check", ["baseBranch"] = "main", ["trust"] = "safe",
            ["identity"] = new { name = "Robot", email = "robot@example.org" },
            ["implementer"] = new { model = "opus", maxToolCalls = 80 },
            ["writablePaths"] = new[] { "src/**", "docs/**" },
        };
        var (status, saved) = await ui.SendAsync(HttpMethod.Put, "/api/projects/demo/setup", changed);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("safe", saved.GetProperty("trust").GetString());
        var config = ConfigLoader.Load(ui.RepoFolder, ui.Workspace.ProjectFolder("demo"));
        Assert.Equal("Robot", config.Identity!.Name);
        Assert.Equal("opus", config.Roles.Implementer.Model);
        Assert.Equal(80, config.Roles.Implementer.MaxToolCalls);
        Assert.Equal(["src/**", "docs/**"], config.WritablePaths);

        var (badStatus, bad) = await ui.SendAsync(HttpMethod.Put, "/api/projects/demo/setup", new { buildCommand = "make", testCommand = "make", baseBranch = "main", trust = "everything", identity = new { name = "R", email = "no spaces@x" } });
        Assert.Equal(HttpStatusCode.BadRequest, badStatus);
        Assert.True(bad.GetProperty("problems").GetArrayLength() >= 2);
        Assert.Equal("safe", ConfigLoader.Load(ui.RepoFolder, ui.Workspace.ProjectFolder("demo")).Trust);
    }

    [Fact]
    public async Task Context_documents_are_read_and_written_only_inside_the_listed_paths()
    {
        await using var ui = new UiFixture();
        await ui.CreateProjectAsync();
        var (_, current) = await ui.GetAsync("/api/projects/demo/setup");
        var draft = System.Text.Json.Nodes.JsonNode.Parse(current.GetRawText())!;
        draft["context"] = new System.Text.Json.Nodes.JsonArray("docs/rules.md");
        var (saved, savedBody) = await ui.SendAsync(HttpMethod.Put, "/api/projects/demo/setup", draft);
        Assert.True(saved == HttpStatusCode.OK, savedBody.ToString());

        var (writeStatus, _) = await ui.SendAsync(HttpMethod.Put, "/api/projects/demo/context/file", new { path = "docs/rules.md", text = "Use tabs." });
        Assert.Equal(HttpStatusCode.OK, writeStatus);
        var (_, file) = await ui.GetAsync("/api/projects/demo/context/file?path=docs%2Frules.md");
        Assert.Equal("Use tabs.", file.GetProperty("text").GetString());
        var (_, list) = await ui.GetAsync("/api/projects/demo/context");
        Assert.Equal("docs/rules.md", list[0].GetString());

        var (outside, _) = await ui.SendAsync(HttpMethod.Put, "/api/projects/demo/context/file", new { path = "../secrets.txt", text = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, outside);
        var (unlisted, _) = await ui.SendAsync(HttpMethod.Put, "/api/projects/demo/context/file", new { path = "src/Program.cs", text = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, unlisted);
    }

    [Fact]
    public async Task An_unknown_project_is_not_found()
    {
        await using var ui = new UiFixture();
        var (status, _) = await ui.GetAsync("/api/projects/ghost/tasks");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
