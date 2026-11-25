# Connect your project

A checklist. `autopilot doctor` checks each item, so you can run it after every step.

## 1. The project builds and its tests pass

- The project is a git repository, and you run the tool from its root.
- The base branch exists, as a local branch or only as a remote-tracking branch
  (`origin/<name>`). A remote-only base branch is used as it is, with a note; to track
  it locally run `git branch <name> origin/<name>`.
- There is one command that builds and one that runs the tests, and both pass on the
  base branch. Today the test command must be `dotnet test ...`, because the gate
  counts tests from the TRX files it asks `dotnet test` to write. Python and
  JavaScript projects are on the [roadmap](ROADMAP.md).

## 2. Create the settings

```powershell
cd <your project>
autopilot init                        # base branch: main, else master, else the current branch
autopilot init --base-branch develop  # or name it
```

`init` prints the base branch it chose and how to change it (`autopilot init --force
--base-branch <name>`, or edit `baseBranch`).

This writes:

| File | What it is |
|---|---|
| `.autopilot/autopilot.json` | All settings, with defaults for your project |
| `.autopilot/README.md` | What every setting means |
| `.autopilot/tasks/001-example.md` | An example task (marked done, so it is not picked up) |
| `.gitignore` line | `.autopilot/out/` stays out of git |

Commit `.autopilot/` so the settings are reviewed like code.

## 3. Check the settings

Open `.autopilot/autopilot.json` and check at least:

- `commands.build` and `commands.test`.
- `writablePaths`: where the agent may write. Keep it to source and test folders.
  `init` writes `src/**` and `tests/**` when those folders exist, else one entry per
  folder that holds a `*.csproj` (for example `Shop.Api/**`), else an empty list. It never
  writes `**`. An empty list fails `autopilot doctor` until you fill it in.
- `context`: your project's rules for agents (see the next section).
- `riskPaths`: files the agent may never change and that always go to a person,
  for example auth code, settings files, project and package files. The defaults
  cover common .NET files; add your own.
- `gate`: limits on files and lines per change.
- `roles.*.model`: the Claude model per role, for example `"sonnet"`. Empty means
  Claude Code's default.

## 4. Give the agents your project rules

If your team has rules a newcomer must read (layering, naming, "no new packages",
architecture decisions), list them in `context`:

```json
"context": ["docs/agent-rules.md", "docs/decisions/"]
```

- Each entry is a file or a folder, relative to the project root. A folder includes every
  file in it. No wildcards, no absolute paths, no `..`.
- The text is read from the **base commit**, not from your working tree, so only
  committed rules count. `autopilot doctor` fails when a path is not in the base commit
  and says to commit it.
- It is added to the triage, implementer and reviewer prompts under the heading
  "Project rules and context", at most 40 KB (40,960 characters) in total. Past that the
  text is cut, with a note naming what was left out. Binary files are skipped.
- The paths are protected: the hook denies every write to them and the gate rejects a
  change that touches them (`paths.protected`), so the agent cannot rewrite its own rules.

Triage also gets the list of `riskPaths` and protected paths, with the rule that a task
which can only be done by changing one of them goes to a person (`needs-human`, naming
the path).

## 5. Choose where tasks come from

- **Local (default):** one markdown file per task in `.autopilot/tasks/`, named
  `<id>-<slug>.md`, starting with `# Title`. Optional lines `Labels: a, b` and
  `Status: done`.
- **Azure DevOps:** see [AZURE-DEVOPS.md](AZURE-DEVOPS.md). Set `"recordTriage": "local"`
  in its settings to keep triage results only in `.autopilot/out/triage/` and write
  nothing to Azure DevOps.

With a remote source, task files in `.autopilot/tasks/` are not read; `autopilot doctor`
warns when open ones exist. To work on them for one run, add `--source local` to
`triage`, `implement` or `run`. Drafts of such a run stay local bundles.

Write tasks the way you would for a new colleague: what is wrong or wanted, and how
you will know it is done. Vague tasks are sent back to a person by triage, which is
the point.

## 6. Choose where drafts go

- **Local (default):** a bundle in `.autopilot/out/runs/<id>/`.
- **Azure DevOps:** the bundle, then a draft pull request opened by the agent's own
  identity. Set up that identity with the least rights and a branch policy that needs
  a human reviewer. See [AZURE-DEVOPS.md](AZURE-DEVOPS.md).

## 7. Optional: diagnostics

If your application writes JSON Lines logs, point the read-only diagnostics MCP server
at them so triage and the implementer can look up errors:

```json
"diagnostics": { "type": "jsonl", "settings": { "path": "logs/app.jsonl" } }
```

The path must be inside the project.

## 8. Secrets

Secrets are read only from user environment variables starting with `AUTOPILOT_`,
for example `AUTOPILOT_ADO_PAT`. They are never written to files. The agent and the
build and test runs get a clean environment that does not contain them.

## 9. Run doctor, then start small

```powershell
autopilot doctor
autopilot triage
autopilot implement <one small task>
```

By default `doctor` runs the build and test commands on a clean temporary checkout of the
base commit, the way the gate does, so a passing doctor means the gate can build. Use
`--in-place` to run them in the project folder instead, or `--skip-build` to skip them.
When one fails, doctor prints up to 20 relevant lines (error lines first) and saves the
full output to `.autopilot/out/doctor/build.log` or `test.log`.

The configured `commands.build` and `commands.test` lines are always allowed for the
implementer exactly as written, even with quotes or options such as `-p:Name=Value`
that the hook otherwise denies; extra trailing arguments must pass the normal rules.

Read the draft and the gate report before you trust the next one.

## 10. Add your own evals

Put cases in `.autopilot/evals/` (format in [EVALS.md](EVALS.md)) to measure how the
agent does on your kind of work before and after you change prompts, models or limits.

## Limits to know

The hook controls the agent's tools, but code the agent writes runs during build and
test with your user's rights. Run the tool on a machine or OS user that has no access
to secrets you care about. A stronger sandbox is on the [roadmap](ROADMAP.md).
