using System.CommandLine;
using Autopilot.Core.Config;
using Autopilot.Core.Git;
using Autopilot.Core.Local;

namespace Autopilot.Cli.Commands;

/// <summary><c>autopilot init [--force]</c>: creates <c>.autopilot/</c> with defaults adapted to the project.</summary>
public static class InitCommand
{
    public const string GitIgnoreEntry = ".autopilot/out/";

    public static Command Create(Option<string> projectOption, CliContext context)
    {
        var force = new Option<bool>("--force") { Description = "Overwrite existing Agent Autopilot files." };
        var command = new Command("init", "Create .autopilot/ in the project: settings, an example task and a README.") { force };
        command.SetAction(async (parse, ct) =>
        {
            var project = CliApp.ResolveProject(parse.GetValue(projectOption));
            return await RunAsync(project, parse.GetValue(force), context.Out, context.Error, ct).ConfigureAwait(false);
        });
        return command;
    }

    public static async Task<int> RunAsync(string projectRoot, bool force, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var configPath = ConfigLoader.ConfigPath(projectRoot);
        if (File.Exists(configPath) && !force)
        {
            await error.WriteLineAsync($"{configPath} already exists. Nothing was changed. Use --force to overwrite the Agent Autopilot files.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var config = await BuildDefaultsAsync(projectRoot, ct).ConfigureAwait(false);
        ConfigLoader.Save(config, projectRoot);
        await output.WriteLineAsync($"Wrote {configPath}").ConfigureAwait(false);

        var readmePath = Path.Combine(ProjectPaths.AutopilotFolder(projectRoot), "README.md");
        await File.WriteAllTextAsync(readmePath, ReadmeText, ct).ConfigureAwait(false);
        await output.WriteLineAsync($"Wrote {readmePath}").ConfigureAwait(false);

        var tasks = ProjectPaths.TasksFolder(projectRoot);
        Directory.CreateDirectory(tasks);
        var examplePath = Path.Combine(tasks, "001-example.md");
        if (force || !File.Exists(examplePath))
        {
            await File.WriteAllTextAsync(examplePath, ExampleTask, ct).ConfigureAwait(false);
            await output.WriteLineAsync($"Wrote {examplePath}").ConfigureAwait(false);
        }

        if (await EnsureGitIgnoreAsync(projectRoot, ct).ConfigureAwait(false))
        {
            await output.WriteLineAsync($"Added {GitIgnoreEntry} to .gitignore").ConfigureAwait(false);
        }

        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync($"Build command: {config.Commands.Build}").ConfigureAwait(false);
        await output.WriteLineAsync($"Test command:  {config.Commands.Test}").ConfigureAwait(false);
        await output.WriteLineAsync($"Writable:      {string.Join(", ", config.WritablePaths)}").ConfigureAwait(false);
        await output.WriteLineAsync($"Base branch:   {config.BaseBranch}").ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync("Next: check the settings in .autopilot/autopilot.json, then run 'autopilot doctor'.").ConfigureAwait(false);
        return ExitCodes.Ok;
    }

    public static async Task<AutopilotConfig> BuildDefaultsAsync(string projectRoot, CancellationToken ct)
    {
        var defaults = new AutopilotConfig();

        var solution = Directory.EnumerateFiles(projectRoot, "*.slnx")
            .Concat(Directory.EnumerateFiles(projectRoot, "*.sln"))
            .Select(Path.GetFileName)
            .FirstOrDefault();
        var commands = solution is null
            ? defaults.Commands
            : new CommandsConfig { Build = $"dotnet build \"{solution}\"", Test = $"dotnet test \"{solution}\"" };

        var writable = new List<string>();
        foreach (var folder in new[] { "src", "tests" })
        {
            if (Directory.Exists(Path.Combine(projectRoot, folder)))
            {
                writable.Add(folder + "/**");
            }
        }

        if (writable.Count == 0)
        {
            // Everything; .autopilot/** stays protected by the hook and the gate whatever this says.
            writable.Add("**");
        }

        return defaults with
        {
            BaseBranch = await DetectBaseBranchAsync(projectRoot, ct).ConfigureAwait(false) ?? defaults.BaseBranch,
            Commands = commands,
            WritablePaths = writable,
        };
    }

    /// <summary>Adds <c>.autopilot/out/</c> to the project's .gitignore once. Returns true when it changed the file.</summary>
    public static async Task<bool> EnsureGitIgnoreAsync(string projectRoot, CancellationToken ct)
    {
        var path = Path.Combine(projectRoot, ".gitignore");
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : string.Empty;
        var lines = existing.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim());
        if (lines.Any(l => l is ".autopilot/out/" or ".autopilot/out" or "/.autopilot/out/" or "/.autopilot/out" or "**/.autopilot/out/"))
        {
            return false;
        }

        var prefix = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : Environment.NewLine;
        await File.AppendAllTextAsync(path, $"{prefix}# Agent Autopilot run results{Environment.NewLine}{GitIgnoreEntry}{Environment.NewLine}", ct).ConfigureAwait(false);
        return true;
    }

    private static async Task<string?> DetectBaseBranchAsync(string projectRoot, CancellationToken ct)
    {
        if (await GitClient.FindRepositoryRootAsync(projectRoot, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var git = new GitClient(projectRoot);
        foreach (var candidate in new[] { "main", "master" })
        {
            if (await git.BranchExistsAsync(candidate, ct).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return await git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
    }

    private const string ExampleTask = """
        # Example: return 404 for a missing book

        Status: done
        Labels: example

        Replace this file with a real task. One file per work item: the id is the part of the
        file name before the first '-', the title is the first '# ' heading.

        A good task says what is wrong, where, and how to tell it is fixed, for example:

        GET /books/{id} returns 500 when the book does not exist. It should return 404 with an
        empty body.

        Acceptance criteria:
        - GET /books/999 on an empty catalogue returns 404.
        - Existing tests still pass.

        Remove the "Status: done" line to make the task open. "Status: done" or "Status: closed"
        hides a task from triage and run.
        """;

    private const string ReadmeText = """
        # .autopilot

        Settings and inputs for Agent Autopilot in this project. The agent can never write to this
        folder: the PreToolUse hook denies the write, and the merge gate rejects any change that
        touches it.

        - `autopilot.json`: the settings below.
        - `tasks/`: local work items, one markdown file each (see `tasks/001-example.md`).
        - `out/`: run results (triage, diffs, gate reports, evidence). Git-ignored.

        Check the setup with `autopilot doctor`. Then `autopilot triage`, `autopilot implement <id>`,
        or `autopilot run` for both.

        ## Settings in autopilot.json

        The file is JSON; comments (`//`) and trailing commas are allowed. Every key is optional.

        - `baseBranch`: the branch agent work starts from and drafts target.
        - `branchPrefix`: agent branches are named `<branchPrefix><work item id>`.
        - `commands.build`, `commands.test`: run in the project root by `doctor`, by the agent, and
          by the gate on a clean checkout of the base commit and of the agent's branch.
        - `writablePaths`: glob patterns (relative to the project root) the implementer may write.
          `.autopilot/**` is always protected, whatever this list says.
        - `riskPaths`: glob patterns that make a change high risk. Such a change is rejected and goes to a person.
          The agent can never write these files (project files, `*.props`, `*.targets`, settings, auth code).
        - `passEnvironment`: extra environment variable names passed to Claude Code and to build and test
          commands. Everything else is removed except a short built-in list (PATH, TEMP, the user folders,
          `DOTNET_*`, `NUGET_*`, `MSBUILD*`, ...). `ANTHROPIC_*` and `AUTOPILOT_*` are never passed.
        - `commands.test`: when it starts with `dotnet test`, the gate adds a TRX logger and counts tests
          from the TRX files. With any other test command the gate cannot count tests and rejects the change.
        - `gate.maxFilesChanged`: most files one change may touch.
        - `gate.maxSourceLinesChanged`: most added plus removed lines outside test files.
        - `gate.maxTestLinesChanged`: most added plus removed lines in test files.
        - `gate.testPaths`: glob patterns that mark a file as a test file.
        - `gate.requireTestChange`: when non-test code changed, a test file must change too.
        - `roles.triage`, `roles.implementer`, `roles.reviewer`:
          - `model`: Claude Code model name or alias; null for Claude Code's default.
          - `maxToolCalls`: the hook denies every tool call after this many.
          - `allowedTools`: Claude Code tools the role may use (Read, Grep, Glob, Edit, Write, Bash).
          - `allowedCommands`: shell commands the role may run through Bash. A command is allowed
            when it equals an entry or starts with an entry followed by a space.
          A role you write only partly keeps the defaults for the keys you leave out.
        - `modules.workItems.type`: `local` (this folder's `tasks/`) or `azure-devops` (work items from a query).
        - `modules.publisher.type`: `local` (a bundle in `out/runs/<id>/`) or `azure-devops` (the bundle, then a draft pull request).
          Azure DevOps settings and the agent identity setup are in docs/AZURE-DEVOPS.md of Agent Autopilot.
        - `modules.diagnostics.type`: `none` or `jsonl`. With `jsonl`, set `settings.path` to a JSON Lines log
          file inside the project; triage and implementer then get the read-only `mcp__diagnostics__*` tools.
        - `modules.*.settings`: free-form strings that only that module reads.

        Secrets never go in this file. Modules read them from user environment variables that
        start with `AUTOPILOT_`.

        ## Where worktrees live

        Agent worktrees are created outside the project, under your local application data folder
        (`agent-autopilot/worktrees/`), so that the agent does not read this project's or its parent
        folders' CLAUDE.md files. Branches stay in this repository; worktrees are removed after each run.
        """;
}
