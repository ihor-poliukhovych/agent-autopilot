using Autopilot.Core.Analysis;
using Autopilot.Core.Config;
using Autopilot.Core.Process;

namespace Autopilot.Core.Tests;

/// <summary>A temp folder with files, optionally a real git repository. Deleted on dispose.</summary>
internal sealed class AnalyzerFolder : IDisposable
{
    public AnalyzerFolder(string name = "sample-app")
    {
        Root = Path.Combine(Path.GetTempPath(), "autopilot-analyzer-" + Guid.NewGuid().ToString("N")[..10], name);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public AnalyzerFolder File(string relativePath, string content = "")
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return this;
    }

    public AnalyzerFolder Git(params string[] arguments)
    {
        var result = ProcessRunner.RunAsync("git", ["-c", "user.name=Test", "-c", "user.email=test@example.com", .. arguments], Root).GetAwaiter().GetResult();
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        return this;
    }

    /// <summary>git init with the given first branch and one empty commit.</summary>
    public AnalyzerFolder Repository(string branch = "main")
    {
        Git("init", "-q", "-b", branch);
        return Git("commit", "-q", "--allow-empty", "-m", "start");
    }

    public Task<ProjectAnalysis> AnalyzeAsync() => ProjectAnalyzer.AnalyzeAsync(Root);

    public void Dispose()
    {
        var top = Path.GetDirectoryName(Root)!;
        foreach (var file in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
        {
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(top, recursive: true);
    }
}

public sealed class RemoteUrlTests
{
    public static TheoryData<string, string, string?, string?, string?> Table => new()
    {
        { "https://dev.azure.com/contoso/Shop/_git/shop-api", RemoteUrl.AzureDevOps, "https://dev.azure.com/contoso", "Shop", "shop-api" },
        { "https://contoso@dev.azure.com/contoso/Shop/_git/shop-api", RemoteUrl.AzureDevOps, "https://dev.azure.com/contoso", "Shop", "shop-api" },
        { "https://dev.azure.com/contoso/My%20Project/_git/my%20repo", RemoteUrl.AzureDevOps, "https://dev.azure.com/contoso", "My Project", "my repo" },
        { "https://dev.azure.com/contoso/_git/shop-api", RemoteUrl.AzureDevOps, "https://dev.azure.com/contoso", "shop-api", "shop-api" },
        { "git@ssh.dev.azure.com:v3/contoso/Shop/shop-api", RemoteUrl.AzureDevOps, "https://dev.azure.com/contoso", "Shop", "shop-api" },
        { "ssh://git@ssh.dev.azure.com/v3/contoso/Shop/shop-api", RemoteUrl.AzureDevOps, "https://dev.azure.com/contoso", "Shop", "shop-api" },
        { "https://contoso.visualstudio.com/Shop/_git/shop-api", RemoteUrl.AzureDevOps, "https://contoso.visualstudio.com", "Shop", "shop-api" },
        { "https://contoso.visualstudio.com/DefaultCollection/Shop/_git/shop-api", RemoteUrl.AzureDevOps, "https://contoso.visualstudio.com", "Shop", "shop-api" },
        { "contoso@vs-ssh.visualstudio.com:v3/contoso/Shop/shop-api", RemoteUrl.AzureDevOps, "https://dev.azure.com/contoso", "Shop", "shop-api" },
        { "https://github.com/acme/widget.git", RemoteUrl.GitHub, null, "acme", "widget" },
        { "git@github.com:acme/widget.git", RemoteUrl.GitHub, null, "acme", "widget" },
        { "ssh://git@github.com/acme/widget", RemoteUrl.GitHub, null, "acme", "widget" },
        { "https://gitlab.example.com/group/sub/repo.git", RemoteUrl.Other, null, null, "repo" },
        { "git@example.com:team/service.git", RemoteUrl.Other, null, null, "service" },
        { "C:/code/some-repo", RemoteUrl.Other, null, null, "some-repo" },
        { "/srv/git/lib.git", RemoteUrl.Other, null, null, "lib" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Addresses_are_read(string url, string kind, string? organizationUrl, string? project, string? repository)
    {
        var remote = RemoteUrl.Parse("origin", url);
        Assert.Equal(kind, remote.Kind);
        Assert.Equal(organizationUrl, remote.OrganizationUrl);
        Assert.Equal(project, remote.Project);
        Assert.Equal(repository, remote.Repository);
    }

    [Fact]
    public void Credentials_are_removed_from_the_stored_address()
    {
        var remote = RemoteUrl.Parse("origin", "https://user:secret-token@dev.azure.com/contoso/Shop/_git/shop-api");
        Assert.DoesNotContain("secret-token", remote.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("user", remote.Url, StringComparison.Ordinal);
        Assert.Equal("https://dev.azure.com/contoso/Shop/_git/shop-api", remote.Url);
    }
}

public sealed class ProjectAnalyzerTests
{
    [Fact]
    public async Task A_dotnet_solution_gives_build_and_test_commands_and_paths()
    {
        using var folder = new AnalyzerFolder("shop");
        folder.File("Shop.sln", "").File("src/Shop.Api/Shop.Api.csproj", "<Project/>").File("tests/Shop.Api.Tests/Shop.Api.Tests.csproj", "<Project/>")
            .File("Directory.Build.props", "<Project/>").File("src/Shop.Api/appsettings.json", "{}").Repository();

        var a = await folder.AnalyzeAsync();

        Assert.Equal("dotnet build \"Shop.sln\"", a.Build?.Value);
        Assert.Equal("dotnet test \"Shop.sln\"", a.Test?.Value);
        Assert.Equal(Confidence.High, a.Build?.Confidence);
        Assert.Contains("Shop.sln", a.Build?.Evidence, StringComparison.Ordinal);
        Assert.Equal(["src/**", "tests/**"], a.WritablePaths.Select(p => p.Value).Order(StringComparer.Ordinal).ToArray());
        var risk = a.RiskPaths.Select(p => p.Value).ToList();
        Assert.Contains("**/*.csproj", risk);
        Assert.Contains("**/Directory.Build.*", risk);
        Assert.Contains("**/appsettings*.json", risk);
        Assert.Contains(".claude/**", risk);
        Assert.Equal("main", a.BaseBranch.Value);
        Assert.Equal("shop", a.Key.Value);
        Assert.DoesNotContain(a.NeedsInput, n => n.Field is "buildCommand" or "testCommand" or "writablePaths");
    }

    [Fact]
    public async Task A_dotnet_folder_without_a_solution_uses_the_project_files()
    {
        using var folder = new AnalyzerFolder();
        folder.File("App/App.csproj", "<Project/>").File("App.Tests/App.Tests.csproj", "<Project/>").Repository();

        var a = await folder.AnalyzeAsync();

        Assert.Equal("dotnet build \"App/App.csproj\"", a.Build?.Value);
        Assert.Equal("dotnet test \"App.Tests/App.Tests.csproj\"", a.Test?.Value);
        Assert.NotEqual(Confidence.High, a.Build?.Confidence);
        Assert.Equal(["App.Tests/**", "App/**"], a.WritablePaths.Select(p => p.Value).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_node_project_installs_then_runs_its_scripts()
    {
        using var folder = new AnalyzerFolder("web");
        folder.File("package.json", """{ "scripts": { "build": "tsc", "test": "vitest run" } }""").File("package-lock.json", "{}").File("src/index.ts").File("test/index.test.ts").Repository();

        var a = await folder.AnalyzeAsync();

        Assert.Equal("npm ci && npm run build", a.Build?.Value);
        Assert.Equal("npm ci && npm test", a.Test?.Value);
        Assert.Contains("package.json", a.Build?.Evidence, StringComparison.Ordinal);
        Assert.Contains("src/**", a.WritablePaths.Select(p => p.Value));
        Assert.Contains("test/**", a.WritablePaths.Select(p => p.Value));
        Assert.Contains("**/package-lock.json", a.RiskPaths.Select(p => p.Value));
    }

    [Fact]
    public async Task Node_uses_the_package_manager_of_the_lock_file_and_ignores_the_placeholder_test_script()
    {
        using var folder = new AnalyzerFolder();
        folder.File("package.json", """{ "scripts": { "build": "vite build", "test": "echo \"Error: no test specified\" && exit 1" } }""").File("pnpm-lock.yaml").Repository();

        var a = await folder.AnalyzeAsync();

        Assert.Equal("pnpm install --frozen-lockfile && pnpm run build", a.Build?.Value);
        Assert.Null(a.Test);
        Assert.Contains(a.NeedsInput, n => n.Field == "testCommand");
    }

    [Fact]
    public async Task A_python_project_with_pytest_gets_a_pytest_command_and_a_low_confidence_build()
    {
        using var folder = new AnalyzerFolder();
        folder.File("pyproject.toml", "[tool.pytest.ini_options]\ntestpaths = [\"tests\"]\n").File("src/app/__init__.py").File("tests/test_app.py").Repository();

        var a = await folder.AnalyzeAsync();

        Assert.Equal("python -m pytest", a.Test?.Value);
        Assert.Equal(Confidence.High, a.Test?.Confidence);
        Assert.Equal("python -m compileall -q src", a.Build?.Value);
        Assert.Equal(Confidence.Low, a.Build?.Confidence);
        Assert.Contains(a.NeedsInput, n => n.Field == "buildCommand");
        Assert.Equal(["src/**", "tests/**"], a.WritablePaths.Select(p => p.Value).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("**/pyproject.toml", a.RiskPaths.Select(p => p.Value));
        Assert.Contains(a.Warnings, w => w.Contains("dependencies", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_python_project_without_pytest_evidence_falls_back_to_unittest_with_low_confidence()
    {
        using var folder = new AnalyzerFolder();
        folder.File("requirements.txt", "requests\n").File("tests/test_x.py").Repository();

        var a = await folder.AnalyzeAsync();

        Assert.Equal("python -m unittest discover", a.Test?.Value);
        Assert.Equal(Confidence.Low, a.Test?.Confidence);
    }

    [Theory]
    [InlineData("go.mod", "go build ./...", "go test ./...")]
    [InlineData("Cargo.toml", "cargo build", "cargo test")]
    [InlineData("pom.xml", "mvn -B -DskipTests package", "mvn -B test")]
    public async Task Other_stacks_get_their_standard_commands(string manifest, string build, string test)
    {
        using var folder = new AnalyzerFolder();
        folder.File(manifest).Repository();

        var a = await folder.AnalyzeAsync();

        Assert.Equal(build, a.Build?.Value);
        Assert.Equal(test, a.Test?.Value);
    }

    [Fact]
    public async Task A_mixed_monorepo_picks_the_root_stack_and_lists_the_others_as_alternatives()
    {
        using var folder = new AnalyzerFolder("platform");
        folder.File("Platform.slnx").File("src/Api/Api.csproj").File("web/package.json", """{ "scripts": { "build": "vite build", "test": "vitest" } }""")
            .File("tools/report/pyproject.toml", "[project]\nname='report'\n").File("tools/report/tests/test_r.py").Repository();

        var a = await folder.AnalyzeAsync();

        Assert.Equal(["dotnet:.", "node:web", "python:tools/report"], a.Stacks.Select(s => $"{s.Kind}:{s.Path}").ToArray());
        Assert.Equal("dotnet build \"Platform.slnx\"", a.Build?.Value);
        Assert.Equal(Confidence.Medium, a.Build?.Confidence);
        Assert.Contains("npm install --prefix web && npm run build --prefix web", a.Build?.Alternatives ?? []);
        Assert.Contains(a.WritablePaths, p => p.Value == "src/**");
        Assert.Contains(a.WritablePaths, p => p.Value == "web/src/**" || p.Value == "tools/report/tests/**");
    }

    [Fact]
    public async Task A_folder_that_is_not_a_git_repository_is_analyzed_and_flagged()
    {
        using var folder = new AnalyzerFolder("loose-files");
        folder.File("package.json", """{ "scripts": { "build": "tsc" } }""").File("src/a.ts");

        var a = await folder.AnalyzeAsync();

        Assert.False(a.IsGitRepository);
        Assert.Null(a.GitRoot);
        Assert.Equal("main", a.BaseBranch.Value);
        Assert.Equal(Confidence.Low, a.BaseBranch.Confidence);
        Assert.Contains(a.NeedsInput, n => n.Field == "gitRepository");
        Assert.Equal("loose-files", a.Key.Value);
        Assert.Empty(a.Remotes);
        Assert.Equal("local", a.TaskSource.Value);
    }

    [Fact]
    public async Task A_folder_with_no_known_project_files_asks_for_the_commands()
    {
        using var folder = new AnalyzerFolder();
        folder.File("notes.txt", "hello").Repository();

        var a = await folder.AnalyzeAsync();

        Assert.Empty(a.Stacks);
        Assert.Null(a.Build);
        Assert.Null(a.Test);
        Assert.Contains(a.NeedsInput, n => n.Field == "buildCommand");
        Assert.Contains(a.NeedsInput, n => n.Field == "writablePaths");
    }

    [Fact]
    public async Task A_folder_inside_a_repository_analyzes_the_whole_repository()
    {
        using var folder = new AnalyzerFolder();
        folder.File("App.sln").File("src/App/App.csproj").Repository();

        var a = await ProjectAnalyzer.AnalyzeAsync(Path.Combine(folder.Root, "src", "App"));

        Assert.Equal(folder.Root, a.Folder, ignoreCase: true);
        Assert.Contains(a.Warnings, w => w.Contains("inside the git repository", StringComparison.Ordinal));
        Assert.Equal("dotnet build \"App.sln\"", a.Build?.Value);
    }

    [Fact]
    public async Task The_base_branch_follows_origin_head_then_main_master_develop()
    {
        using var originHead = new AnalyzerFolder();
        originHead.Repository("trunk").Git("update-ref", "refs/remotes/origin/develop", "HEAD").Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/develop");
        var a = await originHead.AnalyzeAsync();
        Assert.Equal("develop", a.BaseBranch.Value);
        Assert.Equal(Confidence.High, a.BaseBranch.Confidence);
        Assert.Contains("origin/HEAD", a.BaseBranch.Evidence, StringComparison.Ordinal);

        using var master = new AnalyzerFolder();
        master.Repository("master").Git("branch", "release/1.0");
        var m = await master.AnalyzeAsync();
        Assert.Equal("master", m.BaseBranch.Value);
        Assert.Equal(["master", "release/1.0"], m.BaseBranchCandidates.ToArray());

        using var develop = new AnalyzerFolder();
        develop.Repository("develop");
        var d = await develop.AnalyzeAsync();
        Assert.Equal("develop", d.BaseBranch.Value);
        Assert.Equal(Confidence.Medium, d.BaseBranch.Confidence);

        using var feature = new AnalyzerFolder();
        feature.Repository("feature/x");
        var f = await feature.AnalyzeAsync();
        Assert.Equal("feature/x", f.BaseBranch.Value);
        Assert.Equal(Confidence.Low, f.BaseBranch.Confidence);
        Assert.Contains(f.NeedsInput, n => n.Field == "baseBranch");
    }

    [Fact]
    public async Task An_azure_devops_remote_prefills_the_organization_project_and_repository()
    {
        using var folder = new AnalyzerFolder("checkout");
        folder.Repository().Git("remote", "add", "origin", "https://contoso@dev.azure.com/contoso/Shop/_git/shop-api");

        var a = await folder.AnalyzeAsync();

        Assert.Equal("azure-devops", a.TaskSource.Value);
        Assert.Equal("https://dev.azure.com/contoso", a.AzureDevOps?.OrganizationUrl.Value);
        Assert.Equal("Shop", a.AzureDevOps?.Project.Value);
        Assert.Equal("shop-api", a.AzureDevOps?.Repository.Value);
        Assert.Contains("origin", a.AzureDevOps?.Repository.Evidence, StringComparison.Ordinal);
        Assert.Equal("shop-api", a.Key.Value);
        Assert.Contains(a.NeedsInput, n => n.Field == "ado.tokenVariable");
        Assert.DoesNotContain("contoso@", a.Remotes[0].Url, StringComparison.Ordinal);

        var config = AnalysisApplier.ToConfig(a);
        Assert.Equal("azure-devops", config.Modules.WorkItems.Type);
        Assert.Equal("Shop", config.Modules.WorkItems.Settings["project"]);
    }

    [Fact]
    public async Task A_github_remote_suggests_local_tasks_and_names_the_project_after_the_repository()
    {
        using var folder = new AnalyzerFolder("checkout");
        folder.Repository().Git("remote", "add", "origin", "git@github.com:acme/widget.git");

        var a = await folder.AnalyzeAsync();

        Assert.Equal("local", a.TaskSource.Value);
        Assert.Contains("GitHub", a.TaskSource.Evidence, StringComparison.Ordinal);
        Assert.Null(a.AzureDevOps);
        Assert.Equal("widget", a.Key.Value);
    }

    [Fact]
    public async Task Rule_documents_ci_infrastructure_and_migrations_are_found()
    {
        using var folder = new AnalyzerFolder();
        folder.File("CLAUDE.md", "rules").File("AGENTS.md", "rules").File("docs/decisions/0001-use-x.md", "decision").File(".claude/skills/review/SKILL.md", "skill")
            .File(".claude/rules/style.md", "style").File(".github/workflows/ci.yml").File("azure-pipelines.yml").File("src/Data/Migrations/001.cs")
            .File("infra/main.tf").File("Dockerfile").File("pnpm-lock.yaml").File("package.json", "{}").File("src/Auth/Login.cs").Repository();

        var a = await folder.AnalyzeAsync();

        var context = a.Context.ToDictionary(c => c.Value, c => c.Confidence);
        Assert.Equal(Confidence.High, context["CLAUDE.md"]);
        Assert.Equal(Confidence.High, context["AGENTS.md"]);
        Assert.Equal(Confidence.High, context[".claude/rules"]);
        Assert.Equal(Confidence.Medium, context[".claude/skills"]);
        Assert.Equal(Confidence.Medium, context["docs/decisions"]);
        var risk = a.RiskPaths.Select(r => r.Value).ToList();
        Assert.Contains(".github/**", risk);
        Assert.Contains("**/azure-pipelines*.yml", risk);
        Assert.Contains("**/Migrations/**", risk);
        Assert.Contains("**/*.tf", risk);
        Assert.Contains("infra/**", risk);
        Assert.Contains("**/Dockerfile*", risk);
        Assert.Contains("**/pnpm-lock.yaml", risk);
        Assert.Contains("**/*Auth*/**", risk);
        Assert.All(a.RiskPaths, r => Assert.False(string.IsNullOrWhiteSpace(r.Evidence)));
    }

    [Fact]
    public async Task Analysis_changes_nothing_in_the_folder()
    {
        using var folder = new AnalyzerFolder();
        folder.File("App.sln").File("src/App/App.csproj").File("CLAUDE.md", "rules").Repository();
        string Snapshot() => string.Join('\n', Directory.EnumerateFiles(folder.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Order().Select(f => $"{f}|{new FileInfo(f).Length}|{File.GetLastWriteTimeUtc(f):O}"));
        var before = Snapshot();

        await folder.AnalyzeAsync();

        Assert.Equal(before, Snapshot());
        Assert.False(Directory.Exists(Path.Combine(folder.Root, ".autopilot")));
    }

    [Fact]
    public async Task A_missing_folder_is_refused_with_a_clear_message()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => ProjectAnalyzer.AnalyzeAsync(Path.Combine(Path.GetTempPath(), "autopilot-missing-" + Guid.NewGuid().ToString("N"))));
        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectAnalyzer.AnalyzeAsync("  "));
    }

    [Fact]
    public async Task Build_output_dependency_folders_and_links_are_not_scanned()
    {
        using var folder = new AnalyzerFolder();
        folder.File("App.sln").File("src/App/App.csproj").File("node_modules/dep/package.json", "{}").File("src/App/obj/Generated.csproj").File("venv/pyproject.toml").Repository();

        var a = await folder.AnalyzeAsync();

        Assert.Equal(["dotnet:."], a.Stacks.Select(s => $"{s.Kind}:{s.Path}").ToArray());
    }

    [Fact]
    public async Task The_detected_settings_pass_the_config_validation_and_the_json_names_the_evidence()
    {
        using var folder = new AnalyzerFolder();
        folder.File("App.sln").File("src/App/App.csproj").File("tests/App.Tests/App.Tests.csproj").File("CLAUDE.md", "rules").Repository();

        var a = await folder.AnalyzeAsync();
        var config = AnalysisApplier.ToConfig(a);

        Assert.Empty(ConfigLoader.Validate(config));
        Assert.Contains("CLAUDE.md", config.Context);
        var json = a.ToJson();
        Assert.Contains("\"confidence\": \"high\"", json, StringComparison.Ordinal);
        Assert.Contains("\"evidence\"", json, StringComparison.Ordinal);
        Assert.Contains("dotnet build", AnalysisApplier.Describe(a), StringComparison.Ordinal);
    }
}
