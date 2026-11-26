using System.CommandLine;
using System.Text;
using System.Text.RegularExpressions;
using DevAutopilot.AzureDevOps;
using DevAutopilot.Core.Config;
using DevAutopilot.Core.Facts;
using DevAutopilot.Core.Git;
using DevAutopilot.Core.Local;
using DevAutopilot.Core.Process;
using DevAutopilot.Core.Prompts;
using DevAutopilot.Diagnostics;

namespace DevAutopilot.Cli.Commands;

public enum CheckStatus
{
    Ok,
    Fail,
    Skip,

    /// <summary>Works, but a person should know something. Does not fail doctor.</summary>
    Warn,
}

/// <summary>One doctor line: what was checked, the result, and what to do when it failed.</summary>
public sealed record CheckResult(CheckStatus Status, string Name, string Detail, string? Fix = null);

/// <summary>How doctor runs.</summary>
public sealed record DoctorOptions
{
    /// <summary>Do not run the build and test commands.</summary>
    public bool SkipBuild { get; init; }

    /// <summary>
    /// Run the build and test commands in the project folder instead of on a clean checkout of the base
    /// commit (the default, which is what the gate does).
    /// </summary>
    public bool InPlace { get; init; }

    /// <summary>False skips the dotnet, git and claude version checks.</summary>
    public bool CheckTools { get; init; } = true;

    /// <summary>Folder for the clean checkout. Null means the per-user default outside the project.</summary>
    public string? WorktreeRoot { get; init; }
}

/// <summary><c>autopilot doctor [--skip-build] [--in-place]</c>: checks every part of the setup.</summary>
public static partial class DoctorCommand
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Most lines of a failed build or test shown in the doctor output.</summary>
    public const int MaxExcerptLines = 20;

    /// <summary>Name of the clean checkout doctor builds in (internal names start with '@').</summary>
    public const string CheckoutName = "doctor";

    public static Command Create(Option<string> projectOption, CliContext context)
    {
        var skipBuild = new Option<bool>("--skip-build") { Description = "Do not run the build and test commands." };
        var inPlace = new Option<bool>("--in-place")
        {
            Description = "Run the build and test commands in the project folder instead of on a clean checkout of the base commit (the default, like the gate).",
        };
        var command = new Command("doctor", "Check the tools, the project and the settings. Prints OK, FAIL, WARN or SKIP for each check.") { skipBuild, inPlace };
        command.SetAction(async (parse, ct) =>
        {
            var project = CliApp.ResolveProject(parse.GetValue(projectOption));
            var options = new DoctorOptions
            {
                SkipBuild = parse.GetValue(skipBuild),
                InPlace = parse.GetValue(inPlace),
                CheckTools = context.DoctorChecksTools,
                WorktreeRoot = context.WorktreeRoot,
            };
            var results = await RunChecksAsync(project, options, context.Out, ct).ConfigureAwait(false);
            var failed = results.Count(r => r.Status == CheckStatus.Fail);
            await context.Out.WriteLineAsync().ConfigureAwait(false);
            await context.Out.WriteLineAsync(failed == 0
                ? "All checks passed."
                : $"{failed} check(s) failed. Fix them and run 'autopilot doctor' again.").ConfigureAwait(false);
            return failed == 0 ? ExitCodes.Ok : ExitCodes.Failure;
        });
        return command;
    }

    /// <summary>Runs every check, printing each line as soon as it is known.</summary>
    public static Task<IReadOnlyList<CheckResult>> RunChecksAsync(string projectRoot, bool skipBuild, TextWriter output, CancellationToken ct, bool checkTools = true) =>
        RunChecksAsync(projectRoot, new DoctorOptions { SkipBuild = skipBuild, CheckTools = checkTools }, output, ct);

    /// <summary>Runs every check, printing each line as soon as it is known.</summary>
    public static async Task<IReadOnlyList<CheckResult>> RunChecksAsync(string projectRoot, DoctorOptions options, TextWriter output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var results = new List<CheckResult>();
        async Task Report(CheckResult result)
        {
            results.Add(result);
            await output.WriteLineAsync(Format(result)).ConfigureAwait(false);
        }

        if (options.CheckTools)
        {
            await Report(await ToolCheckAsync("dotnet", "dotnet --version", "Install the .NET 10 SDK from https://dot.net.", projectRoot, ct).ConfigureAwait(false)).ConfigureAwait(false);
            await Report(await ToolCheckAsync("git", "git --version", "Install git from https://git-scm.com and make sure it is on PATH.", projectRoot, ct).ConfigureAwait(false)).ConfigureAwait(false);
            await Report(await ToolCheckAsync("claude", "claude --version",
                "Install Claude Code (https://docs.claude.com/en/docs/claude-code) and sign in once by running 'claude'.", projectRoot, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }
        else
        {
            await Report(new CheckResult(CheckStatus.Skip, "tools", "dotnet, git and claude were not checked")).ConfigureAwait(false);
        }

        // Git repository.
        var repoRoot = await GitClient.FindRepositoryRootAsync(projectRoot, ct).ConfigureAwait(false);
        var isRepoRoot = repoRoot is not null && SamePath(repoRoot, projectRoot);
        if (repoRoot is null)
        {
            await Report(new CheckResult(CheckStatus.Fail, "git repository", $"{projectRoot} is not in a git repository.", "Run 'git init' and commit your code, or pass --project <repository root>.")).ConfigureAwait(false);
        }
        else if (!isRepoRoot)
        {
            await Report(new CheckResult(CheckStatus.Fail, "git repository", $"{projectRoot} is inside {repoRoot}, not its root.", $"Run Dev Autopilot from the repository root: --project \"{repoRoot}\".")).ConfigureAwait(false);
        }
        else
        {
            await Report(new CheckResult(CheckStatus.Ok, "git repository", repoRoot)).ConfigureAwait(false);
        }

        // Config.
        AutopilotConfig? config = null;
        try
        {
            var loaded = ConfigLoader.Load(projectRoot);
            var problems = ConfigLoader.Validate(loaded);
            if (problems.Count == 0)
            {
                config = loaded;
                await Report(new CheckResult(CheckStatus.Ok, "config", ConfigLoader.ConfigPath(projectRoot))).ConfigureAwait(false);
            }
            else
            {
                await Report(new CheckResult(CheckStatus.Fail, "config", string.Join(Environment.NewLine + "        ", problems), "Edit .autopilot/autopilot.json; .autopilot/README.md explains each setting.")).ConfigureAwait(false);
            }
        }
        catch (ConfigException ex)
        {
            await Report(new CheckResult(CheckStatus.Fail, "config", ex.Message, "Run 'autopilot init', or fix the JSON in .autopilot/autopilot.json.")).ConfigureAwait(false);
        }

        // Base branch: a local branch, or a remote-tracking one (accepted with a note).
        ResolvedBranch? baseBranch = null;
        GitClient? git = isRepoRoot ? new GitClient(projectRoot) : null;
        if (config is null || git is null)
        {
            await Report(new CheckResult(CheckStatus.Skip, "base branch", "needs a valid config and a git repository")).ConfigureAwait(false);
        }
        else
        {
            baseBranch = await git.ResolveBranchAsync(config.BaseBranch, ct).ConfigureAwait(false);
            if (baseBranch is null)
            {
                await Report(new CheckResult(CheckStatus.Fail, "base branch", $"branch \"{config.BaseBranch}\" does not exist, locally or as a remote-tracking branch",
                    $"Create it and commit to it, run 'git fetch' if it is on the server, or set baseBranch in .autopilot/autopilot.json.")).ConfigureAwait(false);
            }
            else if (baseBranch.RemoteNote is { } note)
            {
                await Report(new CheckResult(CheckStatus.Warn, "base branch", note, $"git branch {baseBranch.Name} {baseBranch.ShortRef}")).ConfigureAwait(false);
            }
            else
            {
                await Report(new CheckResult(CheckStatus.Ok, "base branch", $"{config.BaseBranch} ({Short(baseBranch.Commit)})")).ConfigureAwait(false);
            }
        }

        // Writable paths point at something that exists.
        if (config is not null)
        {
            var missing = config.WritablePaths.Where(p => !LiteralPrefixExists(projectRoot, p)).ToList();
            await Report(missing.Count == 0
                ? new CheckResult(CheckStatus.Ok, "writable paths", string.Join(", ", config.WritablePaths))
                : new CheckResult(CheckStatus.Fail, "writable paths", $"nothing exists for: {string.Join(", ", missing)}", "Fix writablePaths in .autopilot/autopilot.json.")).ConfigureAwait(false);

            await Report(await ContextCheckAsync(projectRoot, config, git, baseBranch, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        // Build and tests.
        foreach (var check in await BuildAndTestChecksAsync(projectRoot, config, git, baseBranch, options, ct).ConfigureAwait(false))
        {
            await Report(check).ConfigureAwait(false);
        }

        // Modules.
        if (config is null)
        {
            await Report(new CheckResult(CheckStatus.Skip, "work items", "needs a valid config")).ConfigureAwait(false);
        }
        else
        {
            var adoForItems = AzureDevOpsModule.IsSelected(config.Modules.WorkItems);
            var adoForPublisher = AzureDevOpsModule.IsSelected(config.Modules.Publisher);
            if (!adoForItems)
            {
                await Report(await WorkItemsCheckAsync(projectRoot, config.Modules.WorkItems.Type, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }
            else if (await LocalTasksCheckAsync(projectRoot, config.Modules.WorkItems.Type, ct).ConfigureAwait(false) is { } localTasks)
            {
                await Report(localTasks).ConfigureAwait(false);
            }

            if (!adoForPublisher)
            {
                await Report(PublisherCheck(config.Modules.Publisher.Type)).ConfigureAwait(false);
            }

            if (adoForItems || adoForPublisher)
            {
                foreach (var check in await AzureDevOpsChecksAsync(projectRoot, config, adoForItems, adoForPublisher, ct).ConfigureAwait(false))
                {
                    await Report(check).ConfigureAwait(false);
                }
            }

            await Report(await DiagnosticsCheckAsync(projectRoot, config.Modules.Diagnostics, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        return results;
    }

    public static string Format(CheckResult result)
    {
        var status = result.Status switch
        {
            CheckStatus.Ok => "OK  ",
            CheckStatus.Fail => "FAIL",
            CheckStatus.Warn => "WARN",
            _ => "SKIP",
        };
        var line = $"{status}  {result.Name}: {result.Detail}";
        return result.Fix is null || result.Status is not (CheckStatus.Fail or CheckStatus.Warn)
            ? line
            : $"{line}{Environment.NewLine}      Fix: {result.Fix}";
    }

    /// <summary>
    /// The lines of a build or test log a person should see first: lines that look like errors or
    /// failures (each once, in order), then the last lines of the log, at most <paramref name="max"/> in all.
    /// </summary>
    public static IReadOnlyList<string> RelevantLines(string output, int max = MaxExcerptLines)
    {
        var lines = (output ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Trim().Length > 0)
            .ToList();
        var errors = lines
            .Where(l => ErrorLine().IsMatch(l) && !NotAnError().IsMatch(l))
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();
        var tail = lines.Where(l => !errors.Contains(l, StringComparer.Ordinal)).TakeLast(max - errors.Count).ToList();
        return errors.Count == 0 || tail.Count == 0 ? [.. errors, .. tail] : [.. errors, "...", .. tail];
    }

    [GeneratedRegex(@"\berror\b|\[FAIL\]|\bfailed\b|\bexception\b|\bFAILED\b", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"\b0\s+error|\bfailed:\s*0\b|warnaserror", RegexOptions.IgnoreCase)]
    private static partial Regex NotAnError();

    private static async Task<CheckResult> ContextCheckAsync(string projectRoot, AutopilotConfig config, GitClient? git, ResolvedBranch? baseBranch, CancellationToken ct)
    {
        const string Name = "context";
        if (config.Context.Count == 0)
        {
            return new CheckResult(CheckStatus.Skip, Name, "none; list project rule files in \"context\" (for example \"docs/agent-rules.md\") to give them to every agent");
        }

        if (git is null || baseBranch is null)
        {
            return new CheckResult(CheckStatus.Skip, Name, "needs the base branch; context is read from the base commit");
        }

        var context = await ProjectContext.LoadAsync(git, baseBranch.Commit, config.Context, ct).ConfigureAwait(false);
        if (context.Missing.Count > 0)
        {
            var onlyHere = context.Missing.Where(p => File.Exists(Path.Combine(projectRoot, p)) || Directory.Exists(Path.Combine(projectRoot, p))).ToList();
            var fix = onlyHere.Count > 0
                ? $"Commit {string.Join(", ", onlyHere)} to {config.BaseBranch}: context is read from the base commit, not the working tree."
                : "Fix the paths in \"context\" in .autopilot/autopilot.json, or commit the files to the base branch.";
            return new CheckResult(CheckStatus.Fail, Name, $"not in the base commit {Short(baseBranch.Commit)}: {string.Join(", ", context.Missing)}", fix);
        }

        var detail = $"{context.Files.Count} file(s), {context.TotalChars} characters from {Short(baseBranch.Commit)}; the agent cannot write them";
        return context.Notes.Count == 0
            ? new CheckResult(CheckStatus.Ok, Name, detail)
            : new CheckResult(CheckStatus.Warn, Name, detail + Environment.NewLine + string.Join(Environment.NewLine, context.Notes.Select(n => "        " + n)),
                $"Keep the context under {ProjectContext.MaxChars} characters in total, or list fewer files.");
    }

    private static async Task<IReadOnlyList<CheckResult>> BuildAndTestChecksAsync(
        string projectRoot, AutopilotConfig? config, GitClient? git, ResolvedBranch? baseBranch, DoctorOptions options, CancellationToken ct)
    {
        if (options.SkipBuild)
        {
            return [new(CheckStatus.Skip, "build command", "--skip-build"), new(CheckStatus.Skip, "test command", "--skip-build")];
        }

        if (config is null)
        {
            return [new(CheckStatus.Skip, "build command", "needs a valid config"), new(CheckStatus.Skip, "test command", "needs a valid config")];
        }

        var logFolder = Path.Combine(ProjectPaths.OutFolder(projectRoot), "doctor");
        Directory.CreateDirectory(logFolder);
        var buildLog = Path.Combine(logFolder, "build.log");
        var testLog = Path.Combine(logFolder, "test.log");
        DeleteIfExists(buildLog);
        DeleteIfExists(testLog);

        ProcessResult build;
        ProcessResult? test;
        TrxCounts counts;
        string where;
        if (options.InPlace)
        {
            where = "in the project folder";
            build = await ShellRunner.RunAsync(config.Commands.Build, projectRoot, BuildTimeout, GateFactsCollector.CommandEnvironment, ct, config.PassEnvironment).ConfigureAwait(false);
            test = build.Succeeded
                ? await ShellRunner.RunAsync(config.Commands.Test, projectRoot, BuildTimeout, GateFactsCollector.CommandEnvironment, ct, config.PassEnvironment).ConfigureAwait(false)
                : null;
            counts = TrxCounts.None;
        }
        else
        {
            if (git is null || baseBranch is null)
            {
                const string Reason = "needs the base branch for a clean checkout; fix it, or run 'autopilot doctor --in-place'";
                return [new(CheckStatus.Skip, "build command", Reason), new(CheckStatus.Skip, "test command", Reason)];
            }

            where = $"on a clean checkout of {config.BaseBranch} ({Short(baseBranch.Commit)}), like the gate";
            var worktreeRoot = options.WorktreeRoot ?? GitClient.DefaultWorktreeRoot(projectRoot);
            var collector = new GateFactsCollector(git, config, worktreeRoot) { CommandTimeout = BuildTimeout };
            var run = await collector.BuildAndTestCommitAsync(CheckoutName, "check", baseBranch.Commit, new StringBuilder(), ct).ConfigureAwait(false);
            build = run.Build!;
            test = run.Test;
            counts = run.Counts;
        }

        var checks = new List<CheckResult>();
        await File.WriteAllTextAsync(buildLog, build.Output, ct).ConfigureAwait(false);
        checks.Add(build.Succeeded
            ? new CheckResult(CheckStatus.Ok, "build command", $"{config.Commands.Build} ({build.Duration.TotalSeconds:0}s, {where})")
            : new CheckResult(CheckStatus.Fail, "build command", FailureDetail(config.Commands.Build, build, where, buildLog),
                "Make the build pass on the base branch, or fix commands.build."));

        if (test is null)
        {
            checks.Add(new CheckResult(CheckStatus.Skip, "test command", "the build failed"));
            return checks;
        }

        await File.WriteAllTextAsync(testLog, test.Output, ct).ConfigureAwait(false);
        string countText;
        if (counts.Found)
        {
            countText = $", {counts.Total} tests, {counts.Skipped} skipped (from TRX)";
        }
        else
        {
            var summary = TestSummaryParser.Parse(test.Output);
            countText = summary.Found ? $", {summary.Total} tests, {summary.Skipped} skipped" : ", no test summary found";
        }

        checks.Add(test.Succeeded
            ? new CheckResult(CheckStatus.Ok, "test command", $"{config.Commands.Test} ({test.Duration.TotalSeconds:0}s{countText}, {where})")
            : new CheckResult(CheckStatus.Fail, "test command", FailureDetail(config.Commands.Test, test, where, testLog),
                "Make the tests pass on the base branch, or fix commands.test."));
        return checks;
    }

    private static string FailureDetail(string command, ProcessResult result, string where, string logFile)
    {
        var text = new StringBuilder($"{command} {Failure(result)} {where}");
        foreach (var line in RelevantLines(result.Output))
        {
            text.Append(Environment.NewLine).Append("        | ").Append(line);
        }

        text.Append(Environment.NewLine).Append("        Full log: ").Append(logFile);
        return text.ToString();
    }

    private static async Task<CheckResult> ToolCheckAsync(string name, string command, string fix, string workingDirectory, CancellationToken ct)
    {
        var result = await ShellRunner.RunAsync(command, workingDirectory, VersionTimeout, null, ct).ConfigureAwait(false);
        var firstLine = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return result.Succeeded
            ? new CheckResult(CheckStatus.Ok, name, firstLine ?? "installed")
            : new CheckResult(CheckStatus.Fail, name, $"'{command}' did not run", fix);
    }

    private static async Task<CheckResult> WorkItemsCheckAsync(string projectRoot, string type, CancellationToken ct)
    {
        if (!string.Equals(type, "local", StringComparison.OrdinalIgnoreCase))
        {
            return new CheckResult(CheckStatus.Fail, "work items", $"module \"{type}\" is not built yet", "Use \"local\" for modules.workItems.type for now.");
        }

        var source = new LocalWorkItemSource(projectRoot);
        if (!Directory.Exists(source.TasksFolder))
        {
            return new CheckResult(CheckStatus.Fail, "work items", $"{source.TasksFolder} does not exist", "Run 'autopilot init' or create the folder and add a task file.");
        }

        var open = await source.ListOpenAsync(ct).ConfigureAwait(false);
        return new CheckResult(CheckStatus.Ok, "work items", $"local, {open.Count} open task(s) in .autopilot/tasks");
    }

    /// <summary>A warning when the work items come from elsewhere but open local task files exist; null otherwise.</summary>
    public static async Task<CheckResult?> LocalTasksCheckAsync(string projectRoot, string type, CancellationToken ct)
    {
        var open = await new LocalWorkItemSource(projectRoot).ListOpenAsync(ct).ConfigureAwait(false);
        return open.Count == 0
            ? null
            : new CheckResult(CheckStatus.Warn, "local tasks", $"{open.Count} local task files are not read while workItems is {type}",
                "To work on them for one run, add --source local to triage, implement or run. Otherwise mark them 'Status: done' or remove them.");
    }

    private static async Task<IReadOnlyList<CheckResult>> AzureDevOpsChecksAsync(string projectRoot, AutopilotConfig config, bool forItems, bool forPublisher, CancellationToken ct)
    {
        const string Name = "azure devops";
        var module = forPublisher ? config.Modules.Publisher : config.Modules.WorkItems;
        try
        {
            using var ado = AzureDevOpsModule.Create(module, projectRoot, config.BaseBranch, config.BranchPrefix, requireRepository: forPublisher);
            var results = new List<CheckResult>();
            if (forItems)
            {
                // The work item source reads its own block; recordTriage lives there.
                var itemSettings = AzureDevOpsSettings.FromModule(config.Modules.WorkItems, requireRepository: false);
                results.Add(new CheckResult(CheckStatus.Ok, $"{Name}: triage results", itemSettings.RecordTriageDescription));
            }

            var checks = await ado.Doctor.RunAsync(ct).ConfigureAwait(false);
            results.AddRange(checks.Select(c => new CheckResult(
                c.Status switch
                {
                    AzureDevOpsCheckStatus.Ok => CheckStatus.Ok,
                    AzureDevOpsCheckStatus.Fail => CheckStatus.Fail,
                    _ => CheckStatus.Skip,
                },
                $"{Name}: {c.Name}",
                c.Detail,
                c.Fix)));
            return results;
        }
        catch (AzureDevOpsSettingsException ex)
        {
            return [new CheckResult(CheckStatus.Fail, Name, ex.Message, "Fix the module settings in .autopilot/autopilot.json; docs/AZURE-DEVOPS.md lists them.")];
        }
    }

    private static CheckResult PublisherCheck(string type) =>
        string.Equals(type, "local", StringComparison.OrdinalIgnoreCase)
            ? new CheckResult(CheckStatus.Ok, "publisher", "local, drafts go to .autopilot/out/runs/<id>/")
            : new CheckResult(CheckStatus.Fail, "publisher", $"module \"{type}\" is not built yet", "Use \"local\" for modules.publisher.type for now.");

    /// <summary>Reads the diagnostics source once: how many events, over which time range, how many bad lines.</summary>
    public static async Task<CheckResult> DiagnosticsCheckAsync(string projectRoot, ModuleConfig module, CancellationToken ct)
    {
        const string Name = "diagnostics";
        const string SettingFix = "Set modules.diagnostics.settings.path in .autopilot/autopilot.json to a JSON Lines file inside the project, or set modules.diagnostics.type to \"none\".";
        JsonlDiagnosticsSource? source;
        try
        {
            source = DiagnosticsSourceFactory.Create(projectRoot, module);
        }
        catch (DiagnosticsConfigException ex)
        {
            return new CheckResult(CheckStatus.Fail, Name, ex.Message, SettingFix);
        }

        if (source is null)
        {
            return new CheckResult(CheckStatus.Skip, Name, "off");
        }

        JsonlReadResult read;
        try
        {
            read = await source.ReadAllAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CheckResult(CheckStatus.Fail, Name, $"cannot read {source.FilePath}: {ex.Message}",
                "Create the file (one JSON object per line with timestamp, level, message, exception, route, statusCode, durationMs), or fix modules.diagnostics.settings.path.");
        }

        var skipped = read.SkippedLines == 0 ? string.Empty : $", {read.SkippedLines} unreadable line(s) skipped";
        if (read.Events.Count == 0)
        {
            return new CheckResult(CheckStatus.Fail, Name, $"no readable events in {source.FilePath}{skipped}",
                "Each line must be a JSON object with at least a \"timestamp\" (ISO 8601). Check the file, or fix modules.diagnostics.settings.path.");
        }

        var from = read.Events.Min(e => e.Timestamp).ToString("u", System.Globalization.CultureInfo.InvariantCulture);
        var to = read.Events.Max(e => e.Timestamp).ToString("u", System.Globalization.CultureInfo.InvariantCulture);
        return new CheckResult(CheckStatus.Ok, Name, $"jsonl, {read.Events.Count} event(s) from {from} to {to}{skipped} in {source.FilePath}");
    }

    private static string Failure(ProcessResult result) =>
        result.TimedOut ? "timed out" : $"failed with exit code {result.ExitCode}";

    private static string Short(string commit) => commit[..Math.Min(12, commit.Length)];

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>True when the part of a glob before its first wildcard exists, or there is no such part.</summary>
    private static bool LiteralPrefixExists(string projectRoot, string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var wildcard = normalized.IndexOfAny(['*', '?', '[', '{']);
        string literal;
        if (wildcard < 0)
        {
            literal = normalized.TrimEnd('/');
        }
        else
        {
            var beforeWildcard = normalized[..wildcard];
            var slash = beforeWildcard.LastIndexOf('/');
            literal = slash <= 0 ? string.Empty : beforeWildcard[..slash];
        }

        if (literal.Length == 0)
        {
            return true;
        }

        var path = Path.Combine(projectRoot, literal);
        return Directory.Exists(path) || File.Exists(path);
    }

    private static bool SamePath(string a, string b) => string.Equals(
        Path.GetFullPath(a).TrimEnd('\\', '/'),
        Path.GetFullPath(b).TrimEnd('\\', '/'),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
