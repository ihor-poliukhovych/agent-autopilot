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


## D-014 Local tasks are the hub; trackers sync to them (accepted, 2026-11-26)

Tasks, comments and history of a project are tables in `autopilot.db` (migration 003), with a
number per project that is never reused. `LocalTaskService` holds the logic; `task ...` commands
only parse and print. A registered project whose task source is `local` reads its work items from
these tables, and the gate verdict, triage reason and draft location are appended to the task's
history. A tracker is a thin adapter behind `IWorkItemTracker` (read items in, write labels,
comments, state and draft link out); `TaskSyncService` matches by external id and sends each
comment and result once. See docs/TRACKERS.md.

- The repository-local `.autopilot/tasks/*.md` flow is unchanged for a plain folder. A registered
  project that still has task files in its workspace folder and no database tasks keeps reading
  the files until `task import-files` moves them, so nothing disappears silently.
- Rejected: task files as the single source (no comments or history, no safe concurrent writes,
  nothing to link to a tracker), and a database row per tracker with its own model (every tracker
  would need its own triage, list and pool code).
- The Azure DevOps module still serves its work items directly; moving it onto the sync service
  is a later step.


## D-015 Azure DevOps write-back is idempotent, draft-only and checked in words (accepted, 2026-11-26)

The Azure DevOps module is complete for read and write-back, as services behind interfaces
(`IAzureDevOpsReader`, `IAzureDevOpsWriteback`, `IAzureDevOpsPullRequests`,
`IAzureDevOpsConnectionChecker`) so a UI can call them without the CLI. Settings come from the
project's module settings (`organizationUrl`, `project`, `query`, `auth`, variable names, `stateMap`,
`maxItems`); secrets are only ever environment variable names.

- Every write reads first and changes only what differs. Comments carry a visible `ref:<kind>:<key>`
  line that later calls search for; a visible marker survives HTML sanitising, a hidden one may not.
  Tags and state go in one revision-guarded patch, retried once from a fresh read.
- Items already in a finished state are never moved by `stateMap`.
- Pull requests are drafts. The module has no call to mark ready, vote, auto-complete or complete,
  so merging stays with a person; a test checks that no request it sends can do so.
- The write check uses `validateOnly`, so connection checks never change data. The checks return
  plain words and a fix hint, and are shared by `doctor`, `autopilot ado check` and a future wizard.
- The services have their own types for now. The tracker-neutral `IWorkItemTracker` (D-014) comes from the
  local-tasks work; an adapter maps one to the other after both are merged (see docs/AZURE-DEVOPS.md).

Rejected: updating a comment in place (needs the comment id and loses history), and a hidden
HTML-comment marker (not reliably kept by Azure DevOps).

## D-016 The UI is a local minimal API with static pages (accepted, 2026-11-26)

`autopilot ui` serves `DevAutopilot.Web`: ASP.NET minimal API endpoints and plain HTML, CSS and
JavaScript modules compiled into the assembly. No Node toolchain, no framework, no outside
requests, so the tool stays one package that works offline. Endpoints only call services; the
rules stay in the core library and in `SetupService`, `TaskOperations`, `PoolOperations` and
`RunOperations`, which the tests call through an in-memory host.

- Access: 127.0.0.1 only; a one-time key in the first link becomes a session cookie; changes need
  the cookie, a custom header and a matching `Origin`; only local host names are served; a strict
  content policy. The UI is for one person on one computer, not a shared server.
- Setup is one draft object. The wizard and the settings page edit it and the same code turns it
  into the settings file, so there is one place that decides what a valid project is. The checks
  are the `doctor` checks, run against a temporary copy of the draft. A step passes only if its
  checks ran and passed; the server repeats the cheap ones when creating the project.
- The web project cannot reference the command line project (which references it), so the
  command line supplies the `doctor` checks, the folder defaults and the tracker modules behind
  three small interfaces. Tests replace them with fakes.
- Secrets: only environment variable names are stored. Fields for names refuse values that do not
  look like a name, so a pasted token never reaches the settings file.
- Settings gain an optional `identity` (name and address of the agent's commits), and local tasks
  can be edited (`LocalTaskService.Update`); an edit changes the text hash, so stored triage shows
  as stale. A person's answer to a triage question is added to the task as a comment and as a
  section of its text.
- Rejected: a Node front end (a second toolchain for a local tool), serving the UI on a network
  address with accounts (a different product with a different threat model), and polling the
  filesystem instead of calling the services.
