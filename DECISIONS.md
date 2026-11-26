# Decisions

Short records of choices made while building Dev Autopilot. Newest at the bottom.
Status is one of: proposed, accepted, rejected, superseded.

## D-001 A tool, not a demo (accepted)

Dev Autopilot is a reusable tool that anyone can install and connect to their own
project, not a one-off showcase. The example project in `examples/` exists to try the
tool and to run evals.

## D-002 Written from scratch (accepted)

All code and text in this repository are written from scratch. Only general,
widely known engineering ideas are reused (worktree per task, a gate in code,
read-only diagnostics, evals with repeated runs). `VERIFY.md` records the checks
run before each change of visibility.

## D-003 Agent runs through Claude Code on a subscription (accepted)

The agent runs as Claude Code in headless mode (`claude -p`), signed in with a
Claude subscription. No API key is needed or supported for now.

Why: it works with the plan people already have, and Claude Code ships good file and
search tools. Limits are still enforced in code: per-role tool lists on the command
line, isolated settings sources, and a PreToolUse hook that checks every call.

Trade-off: runs use the subscription's usage limits, and turn and token numbers come
from Claude Code's JSON output rather than from our own API client. An API-key runner
can be added later behind the same `IAgentRunner` interface.

## D-004 Local tasks by default, integrations as modules (accepted)

With no configuration the tool reads tasks from `.autopilot/tasks/*.md` and writes
results to `.autopilot/out/`. Integrations are modules switched on in
`autopilot.json`. Azure DevOps (work items and draft pull requests) comes first,
GitHub later.

## D-005 Project settings live in the project, out of the agent's reach (accepted)

Settings are in `.autopilot/autopilot.json` inside the project. The agent cannot
write to `.autopilot/`: the hook denies it and the gate rejects any diff touching it.
Organisation-specific settings stay in the project that uses them.

## D-006 Secrets (accepted)

Secrets are read only from user environment variables with the `AUTOPILOT_` prefix,
for example `AUTOPILOT_ADO_PAT`. Never written to files or settings.

## D-007 Model per role (accepted)

The model is configurable per role in `autopilot.json`. Default: Claude Code's
default model. Eval reports name the model next to every number.

## D-008 Licence (proposed)

MIT.

## D-009 Visibility (accepted)

The GitHub repository starts private. It becomes public only after the owner has
reviewed it and the checks in `VERIFY.md` pass.

## D-010 Publishing is allowed (accepted, 2026-11-26)

The author confirmed that his work agreements allow publishing this tool, which was
written from scratch without any employer or client code or data. The repository is public.

## D-011 Name: Dev Autopilot (accepted, 2026-11-26)

Product name "Dev Autopilot", repository `dev-autopilot`, NuGet package `DevAutopilot`.
Projects and code namespaces are `DevAutopilot.*`, and the default Azure DevOps tag and
the local working folders are `dev-autopilot`. The command stays `autopilot` and the
settings folder stays `.autopilot/`, because people type them by hand.


## D-012 Project data lives in a per-user workspace (accepted, 2026-11-26)

Dev Autopilot is generic: projects are added at run time and none is built in. Settings,
tasks and run output of a project live in a per-user workspace, `%USERPROFILE%\.dev-autopilot`
(`--workspace` or `AUTOPILOT_WORKSPACE` override it), never in the project's repository.
The workspace has one SQLite database `autopilot.db` (Microsoft.Data.Sqlite, numbered
embedded SQL migrations, a `schema_version` table) holding the project registry (key,
display name, repository folder, base branch, task source `local` or `azure-devops`,
settings json), and a `projects/<key>/` folder with `autopilot.json`, `tasks/` and `out/`.
Removing a project only unlinks it; its data is deleted only with `--delete-data --yes`.
`--project` takes a registered key first and otherwise a repository folder, so the
repository-local `.autopilot/` flow keeps working unchanged. Rejected: files only (no
way to list or query projects), and a database per project (harder to list and move).


## D-013 Pool, jobs, runs and schedule live in the workspace database (accepted, 2026-11-26)

The task pool, background jobs, run history, the per-project run lock and the night schedule
are tables in `autopilot.db` (migration 002), not json files next to the project. The
logic is in services in `DevAutopilot.Core` (`PoolService`, `PoolRunner`, `JobService`,
`RunStore`, `ScheduleService`, `ProjectSettingsService`, `WorkItemListService`); the CLI only
parses arguments and prints, so the web UI calls the same services.

- One run per project: a lock row with the process id; a row whose process is gone is stale
  and is taken over. Pool runs, direct `implement` and jobs all take it.
- A job is one detached process with a row (status, current step, times) and `log.txt`.
  Cancel kills that process tree only and frees the lock it held.
- A pool run stops on infrastructure errors (signed out, usage limit, program missing), because
  every later item would fail the same way. Other failures are reported and the run goes on.
- Every run stores the gate verdict ("would have merged") and, once a person records it
  (`decide`), what they did. `stats` counts agreement; trust levels will use it later.
- The schedule uses Task Scheduler (`schtasks`, current user, no administrator rights) or one
  marked crontab line, behind `IScheduler`. Without `--yes` only the plan is printed.
- `merge.by` accepts only `human`. `autoQueue` and `trust` are parsed and validated, with no
  behaviour yet. Azure DevOps `auth: "service-principal"` takes tenant, client id and secret
  from variables named in the settings; values are never stored.

Rejected: a `pool.json` file (no safe concurrent writes, no query), and a job queue daemon
(one detached process per job is enough for a local tool).
