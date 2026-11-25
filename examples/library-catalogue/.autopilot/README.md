# .autopilot settings for the library catalogue

`autopilot.json` holds only what differs from the defaults; every key left out keeps its
default (objects merge key by key).

| Setting | Value here | What it means |
|---|---|---|
| `baseBranch` | `main` | Agent branches start from this branch, and drafts target it. |
| `commands.build` | `dotnet build Catalogue.slnx` | How the agent and the gate build the project. |
| `commands.test` | `dotnet test Catalogue.slnx` | How the agent and the gate run the tests. The gate adds its own TRX logger to count tests. |
| `writablePaths` | `src/**`, `tests/**` | The only files the implementer may change. Everything else, including this folder, is read-only for the agent. |
| `roles.*.model` | `sonnet` | Claude Code model alias for triage, implementer and reviewer. Tool lists and tool-call limits keep their defaults. |
| `modules.workItems` | `local` | Work items are the markdown files in `tasks/`. |
| `modules.publisher` | `local` | A passed change ends as a local bundle in `out/`, not a pull request. |
| `modules.diagnostics` | `jsonl`, `telemetry/telemetry.jsonl` | Triage and the implementer can read the example's fake request telemetry through the read-only diagnostics server. |

Defaults that still apply: risk paths (`*.csproj`, `*.props`, `appsettings*.json`, auth and
security files), gate limits (6 files, 150 source lines, 300 test lines) and the role tool lists.

Folders:

- `tasks/`: the backlog, one markdown file per work item (`001-...md` has id `001`).
- `evals/`: eval cases with hidden acceptance tests; the eval runner removes this folder from
  the copy the agent works in.
- `out/`: run results, ignored by git.
