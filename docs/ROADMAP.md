# Roadmap

Ideas that are planned but not built. Order may change.

## Next (in this order)

1. **Backend for a task pool and background runs** (services the UI calls, plus CLI):
   - `autopilot list [--json]`: open work items with stored triage (stale when the text
     changed), last run, pool membership; optional `Url`, `State`, `AssignedTo` on work items.
   - Pool in `.autopilot/out/pool.json`: `pool add/remove/list/move/clear/run [--limit N]`;
     one item at a time, stop on infrastructure errors, `POOL-REPORT.md` with done,
     rejected, needs-human, not reached; one run per project (lock file with PID).
   - Jobs: detached `autopilot implement <id> --job <id>` with `job.json` (status, current
     step) and `log.txt`; `job list/show/cancel`.
   - Night schedule: `schedule set --at HH:mm [--limit N] --yes`, `show`, `remove`; Windows
     Task Scheduler per user, crontab line elsewhere; without `--yes` only print the plan.
   - Settings: `merge: { by: "human" }` (only value; stated in drafts), `autoQueue:
     { enabled: false, maxRisk: "low" }`, Azure DevOps `auth: "service-principal"`
     (Entra client credentials, ids and secret from `AUTOPILOT_*` variables), `identityName`.
   - `ProjectSettingsService`: read, validate and save `autopilot.json` (keep unknown keys);
     read and write `context` files for a person.
2. **Local web UI** (`autopilot ui`, 127.0.0.1 only, one-time key in the URL):
   tasks (triage, Start, add to pool), pool (order, run now, run at night with
   confirmation), runs (live step, history, gate report, diff), project settings (who
   merges, where task state lives, agent identity, models, limits, risk paths),
   context documents editor.
3. **Verify and publish:** VERIFY.md (secret scan, forbidden words over files and history,
   licences, fresh clone and install by GETTING-STARTED), private GitHub repository,
   owner review, then public.
4. Harder eval cases, so the eval set can tell configurations apart.

## Write and manage tasks inside Agent Autopilot

Some teams have no tracker they want to connect, or want to try the autopilot before
connecting one. They should be able to create a project, write tasks, add comments
and see the agent's results without leaving the tool.

Direction:

- Keep local task files (`.autopilot/tasks/*.md`) as the single source of truth, so
  tasks are versioned in git together with the code and need no server.
- First a CLI: `autopilot task add`, `task list`, `task show <id>`, `task comment <id>`,
  `task close <id>`. Comments and the agent's results (triage reason, gate report,
  draft link) are appended to the task's history.
- Then a small local web UI (`autopilot ui`, served on localhost only) over the same
  files: a task board, a task page with comments, triage result, runs, gate facts and
  the diff.

## Project context, fully local

A project can be run with no tracker at all, in a local workspace outside the
repository (so nothing is added to a repository you do not own), and carry its own
context that every agent role reads before working:

- `context/architecture.md`, `context/conventions.md`, `context/glossary.md`,
  `context/decisions/` (architecture decision records).
- The agent can read the context but never change it.
- The local web UI edits these documents directly, next to tasks and comments.

## Trackers as sync adapters, not separate worlds

Instead of every tracker being a full module with its own behaviour, local tasks become
the hub and each tracker is a thin adapter that imports items and exports results
(labels, comments, draft pull requests). One internal model means a new tracker is
mostly field mapping.

- Azure DevOps: work items in, triage labels and comments out, draft pull requests.
- GitHub: issues in, labels and comments out, draft pull requests.
- Others later (Jira, Linear, GitLab) on the same adapter interface.

## Other

- A stronger sandbox for build and test runs (container or a separate OS user).
- An API-key runner next to the Claude Code subscription runner.
- Running several work items in parallel.
