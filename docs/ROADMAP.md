# Roadmap

Ideas that are planned but not built. Order may change.

## Next (in this order)

0. **Project data outside the repository.** *(Done, see D-012.)* Per-user workspace
   (`%USERPROFILE%\.dev-autopilot`, `--workspace`, `AUTOPILOT_WORKSPACE`) with a SQLite
   registry and a folder per project; `autopilot project add|list|show|remove|import-local`;
   `--project <key>`. The repository-local `.autopilot/` still works. Left over: `eval` on
   workspace projects, comments and run history in the database.

1. **Backend for a task pool and background runs.** *(Done, see D-013.)* Services in the
   core library plus CLI: `autopilot list [--json]`; the pool in SQLite
   (`pool add|remove|list|move|clear|run`, `POOL-REPORT.md`, one run per project with a PID
   lock); detached jobs (`implement <id> --detach`, `job list|show|cancel`); run history with
   the gate verdict and the person's decision (`runs`, `decide`, `stats`); the night schedule
   (`schedule set|show|remove`); settings `merge`, `autoQueue`, `trust` and Azure DevOps
   `auth: "service-principal"`; `ProjectSettingsService` for settings and context files.
   Left over for the UI step: calling these services from the web UI, and the behaviour
   behind `autoQueue` and `trust` (they are only read and checked now).
2. **Local web UI** (`autopilot ui`, 127.0.0.1 only, one-time key in the URL):
   tasks (triage, Start, add to pool), pool (order, run now, run at night with
   confirmation), runs (live step, history, gate report, diff), project settings (who
   merges, where task state lives, agent identity, models, limits, risk paths),
   context documents editor.
3. **Verify and publish:** VERIFY.md (secret scan, forbidden words over files and history,
   licences, fresh clone and install by GETTING-STARTED), private GitHub repository,
   owner review, then public.
4. Harder eval cases, so the eval set can tell configurations apart.

## Write and manage tasks inside Dev Autopilot

Some teams have no tracker they want to connect, or want to try the autopilot before
connecting one. They should be able to create a project, write tasks, add comments
and see the agent's results without leaving the tool.

Direction:

- *(Done, see D-014.)* Tasks live in the workspace database, `autopilot task add|list|show|comment|close|reopen|label|import-files`, with comments and the agent's results in a history. Repository-local task files still work and can be imported. Left over: the local web UI over the same service.
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

The interface and the sync service exist (see D-014 and docs/TRACKERS.md); the Azure DevOps adapter still has to be moved onto them.

## Other

- A stronger sandbox for build and test runs (container or a separate OS user).
- An API-key runner next to the Claude Code subscription runner.
- Running several work items in parallel.
