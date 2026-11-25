# Roadmap

Ideas that are planned but not built. Order may change.

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
