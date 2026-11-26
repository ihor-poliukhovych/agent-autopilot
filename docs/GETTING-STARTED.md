# Getting started

This takes about ten minutes. You install the tool, copy the example project, and
watch the agent work through its tasks. Nothing leaves your machine except Claude
Code's own calls.

## 1. What you need

| Tool | Check |
|---|---|
| .NET 10 SDK | `dotnet --version` shows 10.x |
| git | `git --version` |
| Claude Code, signed in with a Claude subscription | `claude --version`, then run `claude` once and sign in |

Dev Autopilot runs the agent through Claude Code in headless mode (`claude -p`).
Runs use your subscription's usage limits. API keys are not used: the tool removes
`ANTHROPIC_API_KEY` and `ANTHROPIC_AUTH_TOKEN` from the agent's environment.

## 2. Install

From a clone of this repository:

```powershell
dotnet pack src/DevAutopilot.Cli -c Release -o artifacts
dotnet tool install --global DevAutopilot --add-source artifacts
autopilot --help
```

To update later: `dotnet tool update --global DevAutopilot --add-source artifacts`.

## 3. Try the example

The example is a small library catalogue API with real defects and eight tasks
(`examples/library-catalogue`). The agent needs its own git repository, so copy it out:

```powershell
Copy-Item -Recurse examples/library-catalogue $env:TEMP/catalogue
cd $env:TEMP/catalogue
git init -b main
git add -A
git commit -m "Library catalogue example"
```

On macOS or Linux: `cp -r examples/library-catalogue /tmp/catalogue && cd /tmp/catalogue`
and the same git commands.

Check the setup:

```powershell
autopilot doctor
```

Every line should say `OK` (diagnostics, build and test included).

## 4. Run it

Sort all open tasks into "an agent can do this" and "a person must decide":

```powershell
autopilot triage
```

Then let the agent work on one task:

```powershell
autopilot implement 001
```

Or do both for every agent-ready task:

```powershell
autopilot run --limit 2
```

What happens for each task:

1. A git worktree is created outside the project on branch `autopilot/<id>`.
2. The implementer agent changes code and tests in that worktree. A hook checks every
   tool call: writes only inside `src/` and `tests/`, a short list of shell commands,
   a limit on the number of calls.
3. The reviewer agent reads the task and the diff and approves or asks for changes
   (one more round at most).
4. The merge gate builds and tests the base branch and the agent branch on clean
   checkouts and checks its rules (see [GATE.md](GATE.md)).
5. If the gate passes, a draft is written to `.autopilot/out/runs/<id>/`:
   `PULL_REQUEST.md`, `change.patch`, `gate.json`. If not, `REJECTED.md` says why.

Nothing is merged. Look at the draft, and if you like it:

```powershell
git merge autopilot/001
```

## 5. Run the evals

```powershell
autopilot eval --project <path to your clone>/examples/library-catalogue --runs 1
```

Evals copy the example into temporary repositories themselves, so they run on the
folder inside the clone. See [EVALS.md](EVALS.md).

## Next

- Connect your own project: [ADOPT.md](ADOPT.md).
- Use Azure DevOps work items and draft pull requests: [AZURE-DEVOPS.md](AZURE-DEVOPS.md).
