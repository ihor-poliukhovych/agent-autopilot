# Connect your project

A checklist. `autopilot doctor` checks each item, so you can run it after every step.

## 1. The project builds and its tests pass

- The project is a git repository, and you run the tool from its root.
- The base branch (default `main`) exists.
- There is one command that builds and one that runs the tests, and both pass on the
  base branch. Today the test command must be `dotnet test ...`, because the gate
  counts tests from the TRX files it asks `dotnet test` to write. Python and
  JavaScript projects are on the [roadmap](ROADMAP.md).

## 2. Create the settings

```powershell
cd <your project>
autopilot init
```

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
- `riskPaths`: files the agent may never change and that always go to a person,
  for example auth code, settings files, project and package files. The defaults
  cover common .NET files; add your own.
- `gate`: limits on files and lines per change.
- `roles.*.model`: the Claude model per role, for example `"sonnet"`. Empty means
  Claude Code's default.

## 4. Choose where tasks come from

- **Local (default):** one markdown file per task in `.autopilot/tasks/`, named
  `<id>-<slug>.md`, starting with `# Title`. Optional lines `Labels: a, b` and
  `Status: done`.
- **Azure DevOps:** see [AZURE-DEVOPS.md](AZURE-DEVOPS.md).

Write tasks the way you would for a new colleague: what is wrong or wanted, and how
you will know it is done. Vague tasks are sent back to a person by triage, which is
the point.

## 5. Choose where drafts go

- **Local (default):** a bundle in `.autopilot/out/runs/<id>/`.
- **Azure DevOps:** the bundle, then a draft pull request opened by the agent's own
  identity. Set up that identity with the least rights and a branch policy that needs
  a human reviewer. See [AZURE-DEVOPS.md](AZURE-DEVOPS.md).

## 6. Optional: diagnostics

If your application writes JSON Lines logs, point the read-only diagnostics MCP server
at them so triage and the implementer can look up errors:

```json
"diagnostics": { "type": "jsonl", "settings": { "path": "logs/app.jsonl" } }
```

The path must be inside the project.

## 7. Secrets

Secrets are read only from user environment variables starting with `AUTOPILOT_`,
for example `AUTOPILOT_ADO_PAT`. They are never written to files. The agent and the
build and test runs get a clean environment that does not contain them.

## 8. Run doctor, then start small

```powershell
autopilot doctor
autopilot triage
autopilot implement <one small task>
```

Read the draft and the gate report before you trust the next one.

## 9. Add your own evals

Put cases in `.autopilot/evals/` (format in [EVALS.md](EVALS.md)) to measure how the
agent does on your kind of work before and after you change prompts, models or limits.

## Limits to know

The hook controls the agent's tools, but code the agent writes runs during build and
test with your user's rights. Run the tool on a machine or OS user that has no access
to secrets you care about. A stronger sandbox is on the [roadmap](ROADMAP.md).
