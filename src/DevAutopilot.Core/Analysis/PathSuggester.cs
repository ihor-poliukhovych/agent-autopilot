namespace DevAutopilot.Core.Analysis;

/// <summary>Suggests risk paths (changes that always need a person) and rule documents for the agent's context.</summary>
internal static class PathSuggester
{
    /// <summary>The prompt budget for context documents, the same as the prompt builder's.</summary>
    public const long ContextBudgetBytes = 40 * 1024;

    private sealed record RiskRule(string Pattern, string Reason, Func<IndexEntry, bool> Exists);

    private static Func<IndexEntry, bool> File(params string[] names) => e => !e.IsDirectory && names.Any(e.NameIs);

    private static Func<IndexEntry, bool> Ext(params string[] extensions) => e => !e.IsDirectory && extensions.Any(e.HasExtension);

    private static Func<IndexEntry, bool> Folder(params string[] names) => e => e.IsDirectory && names.Any(e.NameIs);

    private static Func<IndexEntry, bool> TopFolder(string name) => e => e.IsDirectory && e.Path.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static Func<IndexEntry, bool> NameLike(string prefix, string extension) =>
        e => !e.IsDirectory && e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.HasExtension(extension);

    private static readonly RiskRule[] Rules =
    [
        // Project, dependency and lock files.
        new("**/*.csproj", "project files (.csproj found)", Ext(".csproj")),
        new("**/*.fsproj", "project files (.fsproj found)", Ext(".fsproj")),
        new("**/*.sln", "solution files", Ext(".sln")),
        new("**/*.slnx", "solution files", Ext(".slnx")),
        new("**/Directory.Build.*", "shared build settings (Directory.Build.* found)", e => !e.IsDirectory && e.Name.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)),
        new("**/Directory.Packages.props", "central package versions", File("Directory.Packages.props")),
        new("**/*.props", "MSBuild props files", Ext(".props")),
        new("**/*.targets", "MSBuild targets files", Ext(".targets")),
        new("global.json", "SDK version pin", File("global.json")),
        new("**/nuget.config", "package sources", File("nuget.config")),
        new("**/packages.lock.json", "NuGet lock file", File("packages.lock.json")),
        new("**/package.json", "Node manifest (dependencies and scripts)", File("package.json")),
        new("**/package-lock.json", "npm lock file", File("package-lock.json")),
        new("**/pnpm-lock.yaml", "pnpm lock file", File("pnpm-lock.yaml")),
        new("**/yarn.lock", "yarn lock file", File("yarn.lock")),
        new("**/pyproject.toml", "Python project file", File("pyproject.toml")),
        new("**/setup.py", "Python project file", File("setup.py")),
        new("**/requirements*.txt", "Python dependency list", NameLike("requirements", ".txt")),
        new("**/poetry.lock", "Poetry lock file", File("poetry.lock")),
        new("**/uv.lock", "uv lock file", File("uv.lock")),
        new("**/Pipfile*", "Pipenv files", File("Pipfile", "Pipfile.lock")),
        new("**/go.mod", "Go module file", File("go.mod")),
        new("**/go.sum", "Go checksums", File("go.sum")),
        new("**/Cargo.toml", "Rust manifest", File("Cargo.toml")),
        new("**/Cargo.lock", "Rust lock file", File("Cargo.lock")),
        new("**/pom.xml", "Maven project file", File("pom.xml")),
        new("**/build.gradle*", "Gradle build files", e => !e.IsDirectory && e.Name.StartsWith("build.gradle", StringComparison.OrdinalIgnoreCase)),

        // Pipelines.
        new(".github/**", "CI and repository settings (.github/ found)", TopFolder(".github")),
        new(".azuredevops/**", "Azure DevOps settings (.azuredevops/ found)", TopFolder(".azuredevops")),
        new(".pipelines/**", "CI pipelines (.pipelines/ found)", TopFolder(".pipelines")),
        new(".circleci/**", "CI pipelines (.circleci/ found)", TopFolder(".circleci")),
        new("**/azure-pipelines*.yml", "Azure Pipelines definitions", NameLike("azure-pipelines", ".yml")),
        new("**/azure-pipelines*.yaml", "Azure Pipelines definitions", NameLike("azure-pipelines", ".yaml")),
        new(".gitlab-ci.yml", "CI pipeline", File(".gitlab-ci.yml")),
        new("Jenkinsfile", "CI pipeline", File("Jenkinsfile")),

        // Settings, configuration and secrets.
        new("**/appsettings*.json", "application settings (appsettings*.json found)", NameLike("appsettings", ".json")),
        new("**/web.config", "web server settings", File("web.config")),
        new("**/launchSettings.json", "launch profiles", File("launchSettings.json")),
        new("**/.env*", "environment files", e => !e.IsDirectory && e.Name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)),
        new("**/*.pem", "key and certificate files", Ext(".pem")),
        new("**/*.pfx", "key and certificate files", Ext(".pfx")),
        new("**/*.key", "key files", Ext(".key")),

        // Databases and infrastructure.
        new("**/Migrations/**", "database migrations (Migrations/ found)", Folder("Migrations")),
        new("**/migrations/**", "database migrations (migrations/ found)", e => e.IsDirectory && e.Name == "migrations"),
        new("**/alembic/**", "database migrations (alembic/ found)", Folder("alembic")),
        new("**/*.tf", "Terraform files", Ext(".tf")),
        new("**/*.bicep", "Bicep files", Ext(".bicep")),
        new("**/Dockerfile*", "container images", e => !e.IsDirectory && e.Name.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)),
        new("**/docker-compose*.yml", "container setup", NameLike("docker-compose", ".yml")),
        new("infra/**", "infrastructure (infra/ found)", TopFolder("infra")),
        new("terraform/**", "infrastructure (terraform/ found)", TopFolder("terraform")),
        new("deploy/**", "deployment (deploy/ found)", TopFolder("deploy")),
        new("helm/**", "deployment (helm/ found)", TopFolder("helm")),
        new("k8s/**", "deployment (k8s/ found)", TopFolder("k8s")),
        new("charts/**", "deployment (charts/ found)", TopFolder("charts")),
    ];

    /// <summary>Patterns that are always listed: the agent must never touch its own rules, or authentication and secrets code.</summary>
    private static readonly (string Pattern, string Reason)[] Always =
    [
        (".claude/**", "agent settings, skills and rules: the agent must not change its own rules"),
        ("**/*Auth*/**", "authentication code and folders (convention; listed even when none exists yet)"),
        ("**/*Auth*.*", "authentication code (convention)"),
        ("**/*Security*/**", "security code and folders (convention)"),
        ("**/*Security*.*", "security code (convention)"),
        ("**/*Secret*/**", "secrets folders (convention)"),
        ("**/*Credential*/**", "credential folders (convention)"),
    ];

    public static IReadOnlyList<Detected> RiskPaths(FolderIndex index)
    {
        var result = new List<Detected>();
        foreach (var (pattern, reason) in Always)
        {
            var present = pattern == ".claude/**" && index.DirectoryExists(".claude");
            result.Add(new Detected(pattern, present ? ".claude/ found: " + reason : reason, present ? Confidence.High : Confidence.Medium));
        }

        foreach (var rule in Rules)
        {
            var first = index.Entries.FirstOrDefault(rule.Exists);
            if (first is not null)
            {
                result.Add(new Detected(rule.Pattern, $"{rule.Reason}, for example {first.Path}", Confidence.High));
            }
        }

        return [.. result.DistinctBy(d => d.Value, StringComparer.Ordinal)];
    }

    private sealed record ContextRule(string Path, bool Folder, string Reason, Confidence Confidence);

    private static readonly ContextRule[] ContextRules =
    [
        new("CLAUDE.md", false, "project instructions for Claude Code", Confidence.High),
        new("AGENTS.md", false, "project instructions for coding agents", Confidence.High),
        new(".claude/rules", true, "rule documents for Claude Code", Confidence.High),
        new(".claude/skills", true, "Claude Code skills of the project", Confidence.Medium),
        new(".github/copilot-instructions.md", false, "project instructions for coding assistants", Confidence.Medium),
        new("docs/decisions", true, "decision records", Confidence.Medium),
        new("docs/adr", true, "architecture decision records", Confidence.Medium),
        new("docs/architecture", true, "architecture documents", Confidence.Medium),
        new("docs/rules", true, "project rules", Confidence.Medium),
        new("docs/ARCHITECTURE.md", false, "architecture overview", Confidence.Medium),
        new("ARCHITECTURE.md", false, "architecture overview", Confidence.Medium),
        new("DECISIONS.md", false, "decision log", Confidence.Medium),
        new("DESIGN.md", false, "design notes", Confidence.Medium),
        new("CONTRIBUTING.md", false, "contribution rules", Confidence.Low),
    ];

    public static IReadOnlyList<Detected> Context(FolderIndex index, List<string> warnings)
    {
        var found = new List<(string Path, string Reason, Confidence Confidence, long Size)>();
        foreach (var rule in ContextRules)
        {
            if (rule.Folder ? index.DirectoryExists(rule.Path) : index.FileExists(rule.Path))
            {
                var size = rule.Folder
                    ? index.Files.Where(f => f.Path.StartsWith(rule.Path + "/", StringComparison.OrdinalIgnoreCase)).Sum(f => f.Length)
                    : index.Entries.First(e => e.Path.Equals(rule.Path, StringComparison.OrdinalIgnoreCase)).Length;
                found.Add((rule.Path, rule.Reason, rule.Confidence, size));
            }
        }

        // CLAUDE.md files of sub-projects (a monorepo keeps one per package).
        foreach (var nested in index.Files.Where(f => f.NameIs("CLAUDE.md") && f.Parent != "." && f.Path.Count(c => c == '/') <= 2 && !f.Path.StartsWith(".claude/", StringComparison.Ordinal)))
        {
            found.Add((nested.Path, "project instructions of a sub-project", Confidence.Medium, nested.Length));
        }

        var result = new List<Detected>();
        long total = 0;
        foreach (var (path, reason, confidence, size) in found.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            total += size;
            var over = total > ContextBudgetBytes;
            result.Add(new Detected(path, FormattableString.Invariant($"{reason} ({size / 1024.0:0.#} KB)") + (over ? "; over the 40 KB prompt budget, so only the first documents are read" : string.Empty), over ? Confidence.Low : confidence));
        }

        if (total > ContextBudgetBytes)
        {
            warnings.Add($"The context documents together are about {total / 1024} KB; the agents read about 40 KB. Keep only the rule documents that matter.");
        }

        return result;
    }
}
