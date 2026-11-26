# Tasks and tracker adapters

Dev Autopilot keeps its own tasks. A project with no tracker uses them directly; a project with
a tracker (Azure DevOps today) imports the tracker's items into the same model and writes the
agent's results back. One internal model means a new tracker is mostly field mapping.

## Local tasks

Tasks live in the workspace database (`autopilot.db`), per project, in three tables
(migration 003): `tasks`, `task_comments` and `task_history`.

- A task has a number (1, 2, 3 ... per project, never reused), title, markdown body,
  state (`open` or `closed`), labels, author, timestamps, and an optional external id and url
  (the link to a tracker item).
- Comments are written by people. History is written by the tool: created, closed, reopened,
  labels, and the agent's results: the triage reason, the gate verdict and findings, and the
  draft location.
- The work item id of a local task is its number, so `triage 3`, `implement 3`, `pool add 3`
  and `list` work on it like on any other work item.

```powershell
autopilot project add demo --repo D:\code\demo
autopilot task add "Fix the 404 on /orders" --body "Steps: ..." --label bug --project demo
autopilot task list --project demo            # --all, --label, --json
autopilot task show 1 --project demo          # body, comments, history
autopilot task comment 1 "Seen it on staging too" --project demo
autopilot task label 1 --add ui --remove bug --project demo
autopilot task close 1 --project demo         # and: reopen
autopilot task import-files --project demo    # load .autopilot/tasks/*.md, repeatable
```

All `task` commands need a registered project (`--project <key>`). The repository-local
`.autopilot/tasks/*.md` flow still works for a plain folder passed to `--project`.
A registered project that still has task files in its workspace folder and no tasks in the
database keeps reading those files until `task import-files` moves them.

`import-files` matches a file by its id (`001-fix.md` is `file:001`), so running it again
updates the task instead of creating a duplicate.

## The tracker interface

`IWorkItemTracker` (in `DevAutopilot.Core.Tasks`) is the whole contract. It has two methods:

```csharp
public interface IWorkItemTracker
{
    string Name { get; }
    Task<IReadOnlyList<TrackerItem>> ReadAsync(CancellationToken ct);
    Task WriteAsync(string externalId, TrackerWrite write, CancellationToken ct);
}
```

- `ReadAsync` returns items to import: `ExternalId` (stable, unique in the tracker), title,
  body (markdown or plain text), labels, closed flag, url. Return open items; return closed ones
  too only if that is cheap, because that is how a local task learns it was closed.
- `WriteAsync` sends results for one item. `TrackerWrite` has four optional parts, and a null
  part means "do not touch":
  - `Labels`: the labels the item should carry. Add the missing ones. Never remove a label the
    tool does not know about.
  - `Comments`: new comments, each with an author and markdown text. The adapter converts the
    format (for example to HTML). Triage reasons and gate reports arrive as comments from
    `autopilot`.
  - `Closed`: the state to set.
  - `DraftLink`: the location of the draft pull request or bundle, for the adapter to show
    (a link field, or a line in a comment).
- A write must be safe to repeat with the same input.

`TaskSyncService` drives it:

- `ImportAsync` creates or updates local tasks by external id (unchanged items are left alone).
- `ExportAsync(number)` sends the task's labels and state, plus the comments and agent results
  not sent before, and marks them sent only after `WriteAsync` returned. A task without an
  external id is skipped.

Implementations: `FileTaskTracker` reads `.autopilot/tasks/*.md` and writes nothing back (the
files are the person's input). The Azure DevOps adapter implements the same interface; mapping
rules (fields, tags to labels, HTML to markdown) belong to the adapter.
