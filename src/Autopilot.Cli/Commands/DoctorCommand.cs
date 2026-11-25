using System.CommandLine;
using Autopilot.Core.Config;
using Autopilot.Core.Facts;
using Autopilot.Core.Git;
using Autopilot.Core.Local;
using Autopilot.Core.Process;
using Autopilot.Diagnostics;

namespace Autopilot.Cli.Commands;

public enum CheckStatus
{
    Ok,
    Fail,
    Skip,
}

/// <summary>One doctor line: what was checked, the result, and what to do when it failed.</summary>
public sealed record CheckResult(CheckStatus Status, string Name, string Detail, string? Fix = null);

/// <summary><c>autopilot doctor [--skip-build]</c>: checks every part of the setup.</summary>
public static class DoctorCommand
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(30);

    public static Command Create(Option<string> projectOption, CliContext context)
    {
        var skipBuild = new Option<bool>("--skip-build") { Description = "Do not run the build and test commands." };
        var command = new Command("doctor", "Check the tools, the project and the settings. Prints OK, FAIL or SKIP for each check.") { skipBuild };
        command.SetAction(async (parse, ct) =>
        {
            var project = CliApp.ResolveProject(parse.GetValue(projectOption));
            var results = await RunChecksAsync(project, parse.GetValue(skipBuild), context.Out, ct, context.DoctorChecksTools).ConfigureAwait(false);
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
    public static async Task<IReadOnlyList<CheckResult>> RunChecksAsync(string projectRoot, bool skipBuild, TextWriter output, CancellationToken ct, bool checkTools = true)
    {
        var results = new List<CheckResult>();
        async Task Report(CheckResult result)
        {
            results.Add(result);
            await output.WriteLineAsync(Format(result)).ConfigureAwait(false);
        }

        if (checkTools)
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
            await Report(new CheckResult(CheckStatus.Fail, "git repository", $"{projectRoot} is inside {repoRoot}, not its root.", $"Run Agent Autopilot from the repository root: --project \"{repoRoot}\".")).ConfigureAwait(false);
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

        // Base branch.
        if (config is null || !isRepoRoot)
        {
            await Report(new CheckResult(CheckStatus.Skip, "base branch", "needs a valid config and a git repository")).ConfigureAwait(false);
        }
        else if (await new GitClient(projectRoot).BranchExistsAsync(config.BaseBranch, ct).ConfigureAwait(false))
        {
            await Report(new CheckResult(CheckStatus.Ok, "base branch", config.BaseBranch)).ConfigureAwait(false);
        }
        else
        {
            await Report(new CheckResult(CheckStatus.Fail, "base branch", $"branch \"{config.BaseBranch}\" does not exist", "Create it and commit to it, or set baseBranch in .autopilot/autopilot.json.")).ConfigureAwait(false);
        }

        // Writable paths point at something that exists.
        if (config is not null)
        {
            var missing = config.WritablePaths.Where(p => !LiteralPrefixExists(projectRoot, p)).ToList();
            await Report(missing.Count == 0
                ? new CheckResult(CheckStatus.Ok, "writable paths", string.Join(", ", config.WritablePaths))
                : new CheckResult(CheckStatus.Fail, "writable paths", $"nothing exists for: {string.Join(", ", missing)}", "Fix writablePaths in .autopilot/autopilot.json.")).ConfigureAwait(false);
        }

        // Build and tests.
        if (skipBuild)
        {
            await Report(new CheckResult(CheckStatus.Skip, "build command", "--skip-build")).ConfigureAwait(false);
            await Report(new CheckResult(CheckStatus.Skip, "test command", "--skip-build")).ConfigureAwait(false);
        }
        else if (config is null)
        {
            await Report(new CheckResult(CheckStatus.Skip, "build command", "needs a valid config")).ConfigureAwait(false);
            await Report(new CheckResult(CheckStatus.Skip, "test command", "needs a valid config")).ConfigureAwait(false);
        }
        else
        {
            var build = await ShellRunner.RunAsync(config.Commands.Build, projectRoot, BuildTimeout, GateFactsCollector.CommandEnvironment, ct, config.PassEnvironment).ConfigureAwait(false);
            await Report(build.Succeeded
                ? new CheckResult(CheckStatus.Ok, "build command", $"{config.Commands.Build} ({build.Duration.TotalSeconds:0}s)")
                : new CheckResult(CheckStatus.Fail, "build command", $"{config.Commands.Build} {Failure(build)}", "Make the build pass on the base branch, or fix commands.build.")).ConfigureAwait(false);

            if (!build.Succeeded)
            {
                await Report(new CheckResult(CheckStatus.Skip, "test command", "the build failed")).ConfigureAwait(false);
            }
            else
            {
                var test = await ShellRunner.RunAsync(config.Commands.Test, projectRoot, BuildTimeout, GateFactsCollector.CommandEnvironment, ct, config.PassEnvironment).ConfigureAwait(false);
                var summary = TestSummaryParser.Parse(test.Output);
                var counts = summary.Found ? $", {summary.Total} tests, {summary.Skipped} skipped" : ", no test summary found";
                await Report(test.Succeeded
                    ? new CheckResult(CheckStatus.Ok, "test command", $"{config.Commands.Test} ({test.Duration.TotalSeconds:0}s{counts})")
                    : new CheckResult(CheckStatus.Fail, "test command", $"{config.Commands.Test} {Failure(test)}", "Make the tests pass on the base branch, or fix commands.test.")).ConfigureAwait(false);
            }
        }

        // Modules.
        if (config is null)
        {
            await Report(new CheckResult(CheckStatus.Skip, "work items", "needs a valid config")).ConfigureAwait(false);
        }
        else
        {
            await Report(await WorkItemsCheckAsync(projectRoot, config.Modules.WorkItems.Type, ct).ConfigureAwait(false)).ConfigureAwait(false);
            await Report(PublisherCheck(config.Modules.Publisher.Type)).ConfigureAwait(false);
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
            _ => "SKIP",
        };
        var line = $"{status}  {result.Name}: {result.Detail}";
        return result.Fix is null || result.Status != CheckStatus.Fail ? line : $"{line}{Environment.NewLine}      Fix: {result.Fix}";
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
