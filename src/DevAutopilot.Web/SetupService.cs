using System.Text.RegularExpressions;
using DevAutopilot.Core;
using DevAutopilot.Core.Analysis;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Workspace;

namespace DevAutopilot.Web;

/// <summary>What Analyze returns: the draft with every detected value filled in, and the evidence behind each value.</summary>
public sealed record AnalyzeResult(ProjectDraft Draft, ProjectAnalysis Analysis);

/// <summary>The result of the checks of one wizard step.</summary>
public sealed record StepCheckResult(int Step, bool Passed, IReadOnlyList<CheckLine> Checks);

/// <summary>
/// The logic behind the add-project wizard and the settings page: defaults for a folder, the draft, the
/// checks of each step (the same code as <c>autopilot doctor</c>), and saving. Endpoints only call this.
/// </summary>
public sealed partial class SetupService(WebUiOptions options)
{
    public const int LastStep = 6;
    public const int PreviewPageSize = 10;

    private static readonly IReadOnlyDictionary<int, string[]> StepChecks = new Dictionary<int, string[]>
    {
        [1] = ["folder", "git repository", "base branch"],
        [2] = ["build command", "test command"],
        [3] = ["writable paths", "context"],
        [4] = ["task source", "environment", "work items", "publisher", "local tasks", "azure devops"],
        [5] = ["identity", "config", "dotnet", "git", "claude"],
    };

    /// <summary>A draft with the values found for a folder (build and test commands, paths, base branch).</summary>
    public async Task<ProjectDraft> DefaultsAsync(string folder, string? baseBranch, CancellationToken ct)
    {
        var path = FullFolder(folder);
        var config = await options.Checker.DetectDefaultsAsync(path, baseBranch, ct).ConfigureAwait(false);
        if (config.WritablePaths.Count == 0)
        {
            config = config with { WritablePaths = new AutopilotConfig().WritablePaths };
        }

        var key = SuggestKey(path);
        return ProjectDraft.FromConfig(config, key, key, path, ProjectRegistry.LocalSource);
    }

    /// <summary>
    /// Analyzes a folder (read-only, no model, no network) and returns a complete draft plus the evidence. When the
    /// folder is inside a repository the whole repository is used, so the draft always names the repository's top folder.
    /// </summary>
    public async Task<AnalyzeResult> AnalyzeAsync(string folder, CancellationToken ct)
    {
        var analysis = await ProjectAnalyzer.AnalyzeAsync(FullFolder(folder), ct).ConfigureAwait(false);
        var config = AnalysisApplier.ToConfig(analysis);
        var draft = ProjectDraft.FromConfig(config, analysis.Key.Value, analysis.DisplayName.Value, analysis.Folder, analysis.TaskSource.Value);
        return new AnalyzeResult(draft, analysis);
    }

    /// <summary>The draft of a registered project, read from its settings file.</summary>
    public ProjectDraft Load(ProjectScope scope)
    {
        var view = scope.Settings.Read();
        return ProjectDraft.FromConfig(view.Config, scope.Project.Key, scope.Project.DisplayName, scope.Project.RepositoryFolder, scope.Project.TaskSource);
    }

    /// <summary>Runs the checks of one step (1 to 5) or all of them (6).</summary>
    public async Task<StepCheckResult> CheckAsync(int step, ProjectDraft draft, CancellationToken ct)
    {
        if (step is < 1 or > LastStep)
        {
            throw new ArgumentException($"There is no step {step}.");
        }

        var lines = new List<CheckLine>();
        var early = Early(step, draft);
        lines.AddRange(early);
        var folder = SafeFolder(draft.RepositoryFolder);
        if (folder is not null && !early.Any(l => l.Status == CheckLine.Fail && l.Name == "folder"))
        {
            var useSource = step is 4 or LastStep;
            var checkDraft = useSource ? draft : draft with { TaskSource = ProjectRegistry.LocalSource };
            var all = await RunDoctorAsync(checkDraft, folder, skipBuild: step is not (2 or LastStep), ct).ConfigureAwait(false);
            // In the steps that run the build, a broken config is shown too: the build and test checks are skipped while it is broken.
            lines.AddRange(step == LastStep ? all : all.Where(l => Belongs(step, l.Name) || (step == 2 && l.Name == "config" && l.Status == CheckLine.Fail)));
        }

        // A check that was skipped did not prove anything, so it does not count as passed.
        var passed = lines.All(l => l.Status != CheckLine.Fail)
            && Required(step).All(name => lines.Any(l => l.Name == name && l.Status is CheckLine.Ok or CheckLine.Warn));
        return new StepCheckResult(step, passed, lines);
    }

    /// <summary>Shows items from Azure DevOps for the draft's settings, so a person sees the query finds the right work.</summary>
    public async Task<WorkItemPreviewPage> PreviewAsync(ProjectDraft draft, int skip, CancellationToken ct)
    {
        if (options.AzureDevOps is null)
        {
            throw new ConflictException("Azure DevOps is not available in this build.");
        }

        var secrets = SecretProblems(draft);
        if (secrets.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", secrets));
        }

        var folder = SafeFolder(draft.RepositoryFolder) ?? throw new ArgumentException("Choose the repository folder first (step 1).");
        var config = draft with { TaskSource = ProjectRegistry.AzureDevOpsSource };
        var module = config.ApplyTo(new AutopilotConfig()).Modules.WorkItems;
        return await options.AzureDevOps.PreviewAsync(module, folder, Math.Max(0, skip), PreviewPageSize, ct).ConfigureAwait(false);
    }

    /// <summary>Registers the project and writes its settings. Fails with every problem named when a check fails.</summary>
    public async Task<ProjectDraft> CreateAsync(ProjectDraft draft, CancellationToken ct)
    {
        var folder = FullFolder(draft.RepositoryFolder);
        var registry = new ProjectRegistry(options.Workspace);
        var problems = new List<string>(SecretProblems(draft));
        var config = draft.ApplyTo(new AutopilotConfig());
        problems.AddRange(ConfigLoader.Validate(config));
        problems.AddRange(registry.Validate(new NewProject(draft.Key, folder, NameOf(draft), draft.BaseBranch.Trim(), draft.TaskSource)));
        if (problems.Count > 0)
        {
            throw new SetupRefusedException(problems);
        }

        // Cheap checks again on the server: a client cannot skip them. The build and tests were checked in step 2.
        var result = await CheckAsync(5, draft with { RepositoryFolder = folder }, ct).ConfigureAwait(false);
        if (!result.Passed)
        {
            throw new SetupRefusedException([.. result.Checks.Where(c => c.Status == CheckLine.Fail).Select(c => $"{c.Name}: {c.Detail}")]);
        }

        var record = registry.Add(new NewProject(draft.Key, folder, NameOf(draft), draft.BaseBranch.Trim(), draft.TaskSource));
        ConfigLoader.Save(config, record.RepositoryFolder, options.Workspace.ProjectFolder(record.Key));
        return Load(new ProjectScope(options, record));
    }

    /// <summary>Saves a project's settings and its registry entry from the draft. Nothing is written when it is not valid.</summary>
    public ProjectDraft Save(ProjectScope scope, ProjectDraft draft)
    {
        var problems = new List<string>(SecretProblems(draft));
        var current = ConfigLoader.Load(scope.Project.RepositoryFolder, scope.DataFolder);
        var config = draft.ApplyTo(current);
        problems.AddRange(ConfigLoader.Validate(config));
        if (draft.TaskSource is not (ProjectRegistry.LocalSource or ProjectRegistry.AzureDevOpsSource))
        {
            problems.Add($"The task source must be \"{ProjectRegistry.LocalSource}\" or \"{ProjectRegistry.AzureDevOpsSource}\".");
        }

        if (problems.Count > 0)
        {
            throw new SetupRefusedException(problems);
        }

        new ProjectRegistry(options.Workspace).Update(scope.Project.Key, new ProjectUpdate(
            DisplayName: NameOf(draft), BaseBranch: config.BaseBranch, TaskSource: draft.TaskSource));
        ConfigLoader.Save(config, scope.Project.RepositoryFolder, scope.DataFolder);
        return Load(new ProjectScope(options, new ProjectRegistry(options.Workspace).Get(scope.Project.Key)!));
    }

    /// <summary>Problems that must be fixed before any check runs, in plain words.</summary>
    private List<CheckLine> Early(int step, ProjectDraft draft)
    {
        var lines = new List<CheckLine>();
        if (step is 1 or LastStep)
        {
            if (string.IsNullOrWhiteSpace(draft.RepositoryFolder) || SafeFolder(draft.RepositoryFolder) is null)
            {
                lines.Add(new CheckLine(CheckLine.Fail, "folder", $"There is no folder \"{draft.RepositoryFolder}\".",
                    "Type the full path of the folder that holds the repository (the one with the .git folder inside)."));
            }
            else
            {
                lines.Add(new CheckLine(CheckLine.Ok, "folder", FullFolder(draft.RepositoryFolder)));
            }

            if (string.IsNullOrWhiteSpace(draft.Key) || !ProjectRegistry.IsValidKey(draft.Key))
            {
                lines.Add(new CheckLine(CheckLine.Fail, "project key", ProjectRegistry.InvalidKeyMessage(draft.Key), "Use a short name such as \"shop-api\"."));
            }
        }

        if (step is 3 or LastStep && draft.WritablePaths.All(string.IsNullOrWhiteSpace))
        {
            lines.Add(new CheckLine(CheckLine.Fail, "writable paths", "No path is listed, so the agent could not change anything.",
                "Add the folders the agent may change, one pattern per line, for example src/** and tests/**."));
        }

        if (step is 4 or LastStep)
        {
            lines.AddRange(TaskSourceLines(draft));
        }

        if (step is 5 or LastStep)
        {
            var config = draft.ApplyTo(new AutopilotConfig());
            var problems = ConfigLoader.Validate(config).Where(p => p.StartsWith("identity", StringComparison.Ordinal)).ToList();
            lines.Add(problems.Count == 0
                ? new CheckLine(CheckLine.Ok, "identity", $"{draft.Identity.Name} <{draft.Identity.Email}>")
                : new CheckLine(CheckLine.Fail, "identity", string.Join(" ", problems), "Give a short name and an address without spaces."));
        }

        return lines;
    }

    private IEnumerable<CheckLine> TaskSourceLines(ProjectDraft draft)
    {
        if (draft.TaskSource == ProjectRegistry.LocalSource)
        {
            yield return new CheckLine(CheckLine.Ok, "task source", "Local tasks: you write them here, nothing is sent anywhere.");
            yield break;
        }

        if (draft.TaskSource != ProjectRegistry.AzureDevOpsSource)
        {
            yield return new CheckLine(CheckLine.Fail, "task source", $"\"{draft.TaskSource}\" is not a task source.", "Choose local tasks or Azure DevOps.");
            yield break;
        }

        foreach (var problem in SecretProblems(draft))
        {
            yield return new CheckLine(CheckLine.Fail, "environment", problem,
                "Type only the NAME of the environment variable. Set its value in your operating system, never here.");
        }

        var names = draft.Ado.Auth switch
        {
            "service-principal" => new[] { draft.Ado.TenantVariable, draft.Ado.ClientIdVariable, draft.Ado.ClientSecretVariable },
            "az" => [],
            _ => new[] { draft.Ado.TokenVariable },
        };
        foreach (var name in names.Where(n => VariableNamePattern().IsMatch(n)))
        {
            var isSet = !string.IsNullOrEmpty(options.Environment(name));
            yield return isSet
                ? new CheckLine(CheckLine.Ok, "environment", $"{name} is set on this computer (its value is not read into the UI)")
                : new CheckLine(CheckLine.Fail, "environment", $"{name} is not set for the process that runs the UI.",
                    $"Set the variable {name} to your access token in your operating system, then start 'autopilot ui' again.");
        }
    }

    /// <summary>Environment variable fields must hold names. A pasted token would be stored in the settings file, so it is refused.</summary>
    public static IReadOnlyList<string> SecretProblems(ProjectDraft draft)
    {
        if (draft.TaskSource != ProjectRegistry.AzureDevOpsSource)
        {
            return [];
        }

        var problems = new List<string>();
        foreach (var (label, value) in new[]
        {
            ("token variable", draft.Ado.TokenVariable), ("tenant variable", draft.Ado.TenantVariable),
            ("client id variable", draft.Ado.ClientIdVariable), ("client secret variable", draft.Ado.ClientSecretVariable),
        })
        {
            if (!VariableNamePattern().IsMatch(value ?? string.Empty))
            {
                problems.Add($"The {label} must be an environment variable NAME that starts with AUTOPILOT_ (for example AUTOPILOT_ADO_PAT), not a token or password.");
            }
        }

        return problems;
    }

    private async Task<IReadOnlyList<CheckLine>> RunDoctorAsync(ProjectDraft draft, string folder, bool skipBuild, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "autopilot-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var current = TryLoadExisting(draft.Key) ?? new AutopilotConfig();
            var config = draft.ApplyTo(current);
            Directory.CreateDirectory(Path.Combine(temp, "tasks"));
            Directory.CreateDirectory(Path.Combine(temp, "out"));
            ConfigLoader.Save(config, folder, temp);
            var lines = await options.Checker.RunAsync(new SetupCheckRequest(folder, temp, skipBuild), ct).ConfigureAwait(false);
            return [.. lines.Select(l => l with
            {
                Detail = l.Name == "config" && l.Status == CheckLine.Ok ? "The settings are valid." : DropLogPointer(l.Detail),
                Fix = PlainFix(l),
            })];
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
        }
    }

    private AutopilotConfig? TryLoadExisting(string key)
    {
        var record = string.IsNullOrWhiteSpace(key) || !ProjectRegistry.IsValidKey(key) ? null : new ProjectRegistry(options.Workspace).Get(key);
        if (record is null)
        {
            return null;
        }

        try
        {
            return ConfigLoader.Load(record.RepositoryFolder, options.Workspace.ProjectFolder(key));
        }
        catch (ConfigException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Required(int step) => step switch
    {
        1 => ["folder", "git repository", "base branch"],
        2 => ["build command", "test command"],
        3 => ["writable paths"],
        5 => ["identity", "config"],
        LastStep => ["folder", "git repository", "base branch", "build command", "test command", "writable paths", "identity", "config"],
        _ => [],
    };

    private static bool Belongs(int step, string name) =>
        StepChecks.TryGetValue(step, out var names)
        && names.Any(n => name.Equals(n, StringComparison.Ordinal) || (n == "azure devops" && name.StartsWith("azure devops", StringComparison.Ordinal)));

    /// <summary>Doctor's fixes name files and commands; the page speaks in terms of its own fields.</summary>
    private static string? PlainFix(CheckLine line)
    {
        if (line.Status is not (CheckLine.Fail or CheckLine.Warn) || line.Fix is null)
        {
            return line.Fix;
        }

        return line.Name switch
        {
            "git repository" => "Choose the folder that has the .git folder inside (the top of the repository), and make sure it is committed.",
            "base branch" => "Use the name of a branch that exists in that repository, for example main. If it only exists on the server, run 'git fetch' first.",
            "writable paths" => "Change the writable paths above so that each one points at a folder or file that exists in the repository.",
            "context" => "Commit the listed context files to the base branch, or remove them from the context list.",
            "build command" => "Make the build pass on the base branch, or change the build command above.",
            "test command" => "Make the tests pass on the base branch, or change the test command above.",
            "config" => "Fix the value the line above names. It may be in an earlier step; the build and tests cannot be checked until the settings are valid.",
            _ when MentionsFiles(line.Fix) && line.Name.StartsWith("azure devops", StringComparison.Ordinal) =>
                "Fix the Azure DevOps values in this step (organization, project, repository, query or the variable names), then check again.",
            _ when MentionsFiles(line.Fix) => "Fix the value in this step and run the check again.",
            _ => line.Fix,
        };
    }

    /// <summary>The checks write their logs into a temporary folder that is deleted afterwards, so a pointer to a log file would lead nowhere.</summary>
    private static string DropLogPointer(string detail) =>
        string.Join('\n', detail.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => !line.TrimStart().StartsWith("Full log:", StringComparison.Ordinal)));

    /// <summary>True when a fix names the settings file or a command line, which the page does not show.</summary>
    private static bool MentionsFiles(string fix) =>
        fix.Contains(".autopilot", StringComparison.Ordinal) || fix.Contains("'autopilot", StringComparison.Ordinal)
        || fix.Contains("--project", StringComparison.Ordinal) || fix.Contains("commands.", StringComparison.Ordinal);

    private static string NameOf(ProjectDraft draft) => string.IsNullOrWhiteSpace(draft.DisplayName) ? draft.Key : draft.DisplayName.Trim();

    private static string FullFolder(string folder) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? throw new ArgumentException("Choose the repository folder first.") : folder.Trim());

    private static string? SafeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(folder.Trim());
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string SuggestKey(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/')).ToLowerInvariant();
        name = Regex.Replace(name, "[^a-z0-9_-]+", "-").Trim('-', '_');
        if (name.Length > ProjectRegistry.MaxKeyLength)
        {
            name = name[..ProjectRegistry.MaxKeyLength];
        }

        return ProjectRegistry.IsValidKey(name) ? name : "project";
    }

    [GeneratedRegex("^AUTOPILOT_[A-Za-z0-9_]{1,80}$")]
    private static partial Regex VariableNamePattern();
}

/// <summary>The setup was refused. <see cref="Problems"/> lists every reason in plain words (HTTP 400).</summary>
public sealed class SetupRefusedException(IReadOnlyList<string> problems)
    : Exception("The project was not saved: " + string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
