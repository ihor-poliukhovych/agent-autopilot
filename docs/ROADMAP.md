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
1b. **Azure DevOps complete.** *(Done, see D-015.)* Query preview with paging, item detail with
   comments and links, idempotent write-back (tags, comments, `stateMap`, assignee, link to the
   pull request), draft pull request reuse and description update, connection checks with fix
   hints (`autopilot ado preview|check`, also in `doctor`). Left over: the `IWorkItemTracker` adapter over
   these services (mapping in docs/AZURE-DEVOPS.md); the UI wizard now calls the same checks (see item 2).

2. **Local web UI.** *(Done, see D-016 and docs/UI.md.)* `autopilot ui` (127.0.0.1 only,
   one-time key in the link): projects and a checked add-project wizard, tasks (triage and
   staleness, add and edit, comments, Start, add to pool), the pool (order, run now, run at night
   after a confirmation that shows the plan), runs (live step, a Stop button for one job, history,
   gate report, diff, how often a person agreed with the gate), questions that need a person, and
   settings (trust, identity, models, limits, paths, context documents). Left over: the behaviour
   behind `trust` and `autoQueue`, stopping a pool run as a whole, answering needs-human questions
   of tracker tasks.
3. **Verify and publish:** VERIFY.md (secret scan, forbidden words over files and history,
   licences, fresh clone and install by GETTING-STARTED), private GitHub repository,
   owner review, then public.
4. Harder eval cases, so the eval set can tell configurations apart.

## Write and manage tasks inside Dev Autopilot

Some teams have no tracker they want to connect, or want to try the autopilot before
connecting one. They should be able to create a project, write tasks, add comments
and see the agent's results without leaving the tool.

Direction:

- *(Done, see D-014.)* Tasks live in the workspace database, `autopilot task add|list|show|comment|close|reopen|label|import-files`, with comments and the agent's results in a history. Repository-local task files still work and can be imported. The local web UI over the same service is done (see D-016).
- *(Done, see D-016.)* The local web UI (`autopilot ui`, localhost only) over the same service: a task
  page with comments, triage result, runs, gate facts and the diff.

## Project context, fully local

A project can be run with no tracker at all, in a local workspace outside the
repository (so nothing is added to a repository you do not own), and carry its own
context that every agent role reads before working:

- `context/architecture.md`, `context/conventions.md`, `context/glossary.md`,
  `context/decisions/` (architecture decision records).
- The agent can read the context but never change it.
- The local web UI edits these documents directly (Settings, *Context documents*).

## Trackers as sync adapters, not separate worlds

Instead of every tracker being a full module with its own behaviour, local tasks become
the hub and each tracker is a thin adapter that imports items and exports results
(labels, comments, draft pull requests). One internal model means a new tracker is
mostly field mapping.

- Azure DevOps: work items in, triage labels and comments out, draft pull requests.
- GitHub: issues in, labels and comments out, draft pull requests.
- Others later (Jira, Linear, GitLab) on the same adapter interface.

The interface and the sync service exist (see D-014 and docs/TRACKERS.md); the Azure DevOps adapter still has to be moved onto them.

## Gaps before a full migration

What a team running a larger, project-specific autopilot still needs before it can move
onto Dev Autopilot completely. Generic capabilities only; order inside a group is not fixed.

### P1: blocks a migration

- **Merge gate parity:** proof that the reproducing test fails before and passes after, a
  check of the pull request's thread and vote state, project rules read from the base
  branch instead of the branch under review, verdicts bound to one commit.
- **Risk classification:** a computed level from paths and diff (public contracts, stored
  data, secrets, paid resources), compared with the declared level, never lowered.
- **Rulebook for triage:** written rules with ids that every agent-ready or needs-human mark
  must cite, with accepted decision records overriding them.
- **Human-question flow:** questions on tracker tasks answered in the UI, option answers,
  answers read by author identity, re-triage after an answer.
- **Merge hand-over decision:** keep every merge with a person, or add a guarded merge mode.

### P2: soon

- **Intake sources:** a queue of signals (feedback, ratings, health checks, exceptions) with
  deduplication, masking and claim into a task.
- **Story proposals:** propose an approach, let a person approve it, then build.
- **Exception-watch style scheduled checks:** a baseline comparison that files tasks.
- **Verify after deploy:** detect that a commit is deployed, run the task's "how to check".
- **System diff page:** a broad-to-narrow page per pull request for a quick human decision.
- **Usage dashboard:** plan-limit use per day and cost per run.
- **Run-state table:** explicit steps, statuses and allowed transitions, with stalled-run
  detection.
- **Stop controls:** stop a whole pool run, one global pause switch.
- **Auto-start and budget:** the behaviour behind `autoQueue` and `trust`, with a daily use limit.
- **Pull request comment watcher:** react to new answers and votes on an open pull request.
- **Tracker state in comments:** optionally mirror triage and run state to the tracker.

### P3: later

- Adversarial attack pass with an independent judge model.
- Release-level verification after a production deploy.
- Funnel view: time to merge, first-pass rate, returns by requester.
- Generic scheduled actions beyond the night run.
- Board and event timeline views by tracker column.
- Eval cases for each capability above.
- Always-on service for the UI.

## Other

- A stronger sandbox for build and test runs (container or a separate OS user).
- An API-key runner next to the Claude Code subscription runner.
- Running several work items in parallel.
