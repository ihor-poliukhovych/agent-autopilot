using System.CommandLine;
using Autopilot.Core.Config;
using Autopilot.Core.Git;
using Autopilot.Core.Local;

namespace Autopilot.Cli.Commands;

/// <summary><c>autopilot init [--force] [--base-branch &lt;name&gt;]</c>: creates <c>.autopilot/</c> with defaults adapted to the project.</summary>
public static class InitCommand
{
    public const string GitIgnoreEntry = ".autopilot/out/";

    public static Command Create(Option<string> projectOption, CliContext context)
    {
        var force = new Option<bool>("--force") { Description = "Overwrite existing Dev Autopilot files." };
        var baseBranch = new Option<string?>("--base-branch")
        {
            Description = "Branch agent work starts from and drafts target. Default: main, else master, else the current branch.",
        };
        var command = new Command("init", "Create .autopilot/ in the project: settings, an example task and a README.") { force, baseBranch };
        command.SetAction(async (parse, ct) =>
        {
            var project = CliApp.ResolveProject(parse.GetValue(projectOption));
            return await RunAsync(project, parse.GetValue(force), context.Out, context.Error, ct, parse.GetValue(baseBranch)).ConfigureAwait(false);
        });
        return command;
    }

    public static async Task<int> RunAsync(string projectRoot, bool force, TextWriter output, TextWriter error, CancellationToken ct, string? baseBranch = null)
    {
        if (baseBranch is not null && string.IsNullOrWhiteSpace(baseBranch))
        {
            await error.WriteLineAsync("--base-branch needs a branch name, for example --base-branch main.").ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        var configPath = ConfigLoader.ConfigPath(projectRoot);
        if (File.Exists(configPath) && !force)
        {
            await error.WriteLineAsync($"{configPath} already exists. Nothing was changed. Use --force to overwrite the Dev Autopilot files.").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var config = await BuildDefaultsAsync(projectRoot, ct, baseBranch?.Trim()).ConfigureAwait(false);
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
        await output.WriteLineAsync(config.WritablePaths.Count == 0
            ? "Writable:      (empty) No src/ or tests/ folder and no project folder was found. Fill in writablePaths before you run the agent; 'autopilot doctor' says what is missing."
            : $"Writable:      {string.Join(", ", config.WritablePaths)}").ConfigureAwait(false);
        await output.WriteLineAsync($"Base branch:   {config.BaseBranch} ({await DescribeBaseBranchAsync(projectRoot, config.BaseBranch, baseBranch is not null, ct).ConfigureAwait(false)})").ConfigureAwait(false);
        await output.WriteLineAsync("               To use another branch: 'autopilot init --force --base-branch <name>', or edit baseBranch in .autopilot/autopilot.json.").ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync("Next: check the settings in .autopilot/autopilot.json, then run 'autopilot doctor'.").ConfigureAwait(false);
        return ExitCodes.Ok;
    }

    public static async Task<AutopilotConfig> BuildDefaultsAsync(string projectRoot, CancellationToken ct, string? baseBranch = null)
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
            // Never "**": the folders that hold a project file, or nothing (validation then says what to fill in).
            writable.AddRange(ProjectFolders(projectRoot).Select(f => f + "/**"));
        }

        return defaults with
        {
            BaseBranch = baseBranch ?? await DetectBaseBranchAsync(projectRoot, ct).ConfigureAwait(false) ?? defaults.BaseBranch,
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
        await File.AppendAllTextAsync(path, $"{prefix}# Dev Autopilot run results{Environment.NewLine}{GitIgnoreEntry}{Environment.NewLine}", ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Folders skipped when looking for project files.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "artifacts", "TestResults",
    };

    /// <summary>
    /// Project-relative folders (forward slashes, sorted) that hold a <c>*.csproj</c>, outside build output and
    /// dot folders. A folder inside one already listed is left out; a project file at the root is not listed.
    /// </summary>
    public static IReadOnlyList<string> ProjectFolders(string projectRoot)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(projectRoot);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            IEnumerable<string> subfolders;
            try
            {
                if (!string.Equals(folder, projectRoot, StringComparison.Ordinal) && Directory.EnumerateFiles(folder, "*.csproj").Any())
                {
                    found.Add(Path.GetRelativePath(projectRoot, folder).Replace('\\', '/'));
                    continue;
                }

                subfolders = Directory.EnumerateDirectories(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subfolders)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || SkippedFolders.Contains(name) || new DirectoryInfo(sub).LinkTarget is not null)
                {
                    continue;
                }

                pending.Push(sub);
            }
        }

        return found.Order(StringComparer.OrdinalIgnoreCase).ToList();
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

        // A clone that has only origin/main (for example a fresh worktree or a detached checkout).
        foreach (var candidate in new[] { "main", "master" })
        {
            if (await git.ResolveBranchAsync(candidate, ct).ConfigureAwait(false) is { RemoteOnly: true })
            {
                return candidate;
            }
        }

        return await git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
    }

    /// <summary>How the base branch was chosen and whether it exists, for the init summary.</summary>
    private static async Task<string> DescribeBaseBranchAsync(string projectRoot, string branch, bool given, CancellationToken ct)
    {
        var how = given ? "from --base-branch" : "detected: main, else master, else the current branch";
        if (await GitClient.FindRepositoryRootAsync(projectRoot, ct).ConfigureAwait(false) is null)
        {
            return $"{how}; not a git repository yet";
        }

        var resolved = await new GitClient(projectRoot).ResolveBranchAsync(branch, ct).ConfigureAwait(false);
        return resolved switch
        {
            null => $"{how}; it does not exist yet",
            { RemoteOnly: true } => $"{how}; only {resolved.ShortRef} exists, which is used. To track it locally: git branch {resolved.Name} {resolved.ShortRef}",
            _ => how,
        };
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

        Settings and inputs for Dev Autopilot in this project. The agent can never write to this
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
          `.autopilot/**` is always protected, whatever this list says. `init` lists `src/**` and `tests/**`,
          or else the folders that hold a project file; when it found none the list is empty and must be filled in.
        - `context`: project rule files or folders (for example `docs/agent-rules.md`, `docs/decisions/`). Their
          text is read from the base commit and added to the triage, implementer and reviewer prompts under
          "Project rules and context" (about 40 KB in total). The agent can never write them.
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
          Azure DevOps settings and the agent identity setup are in docs/AZURE-DEVOPS.md of Dev Autopilot.
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
