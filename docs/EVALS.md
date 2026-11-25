# Evals

An eval checks how well the autopilot does on known work items. Each case is run
several times, because an agent does not give the same result twice.

## Case format

Cases live in the project, in `.autopilot/evals/<case-id>/`:

```
.autopilot/evals/
  e01-book-not-found/
    case.json
    hidden/                  files copied over the agent's branch before grading,
      tests/...              at the same relative paths (usually extra tests)
```

`case.json`:

```json
{
  "id": "e01-book-not-found",
  "workItem": "001",
  "description": "GET /books/{id} returns 404 for an unknown id",
  "expect": {
    "triage": "agent-ready",
    "outcome": ["published"]
  }
}
```

- `id`: the case id. It must equal the folder name; when it is left out, the folder name is used.
- `workItem`: id of a task in `.autopilot/tasks/`.
- `description`: optional, for people.
- `expect.triage`: `agent-ready` or `needs-human`.
- `expect.outcome`: one or more accepted outcomes:
  - `published`: the gate passed and a draft was published,
  - `rejected`: the gate rejected the change,
  - `needs-human`: triage sent the item to a person, nothing was implemented.

  A run that breaks (triage gives no answer, an agent run or publishing fails) has the outcome
  `failed`. It is never an expected outcome, so such a run always fails.

`autopilot eval` checks every case before it runs any, and refuses to start while a case is broken:
an unknown work item, an `expect.triage` or `expect.outcome` value not listed above, an outcome that
cannot follow from the expected triage (`needs-human` triage allows only the `needs-human` outcome;
`agent-ready` triage never ends as `needs-human`), or `published` expected without a non-empty
`hidden/` folder. `--case <id>` runs one good case even when another one is broken.

The number of runs per case is `--runs N`, else `runs` in the optional
`.autopilot/evals/evals.json` (`{ "runs": 5 }`), else 3.

## How a case is graded

1. The runner copies the project into a fresh temporary git repository, **without
   `.autopilot/evals/`**, so the agent can never read hidden tests. This copy is the
   pinned snapshot: every run of every case starts from the same files. The files are the project's
   git-tracked files (or, when the project is not in git or has nothing tracked yet, every file
   except `bin/`, `obj/` and `.git/`), never `.autopilot/evals/` or `.autopilot/out/`. They are
   copied once at the start, hashed (SHA-256 over the sorted relative paths and the contents) and
   committed with `git init -b <baseBranch>` under a fixed author and date, so every run starts
   from the same commit.
2. It runs triage and, when triage says `agent-ready`, implement for the case's work item.
3. A run passes when:
   - the triage label equals `expect.triage`, and
   - the outcome is one of `expect.outcome`, and
   - when the outcome is `published`: the `hidden/` files are copied onto a clean
     checkout of the agent's branch (a fresh worktree, not the agent's) and the project's test
     command passes there, run in the same clean environment as the gate's build and test runs.
4. Each case runs `N` times (default 3). Each run gets its own temporary repository, deleted after
   the run unless `--keep` is given.
5. When a run fails because of the machine rather than the agent (Claude Code not signed in, the
   usage limit reached, a program that cannot start, the policy hook not running, an error while setting up the run), the
   eval stops at once instead of using up the remaining runs. That run is not graded, the report
   says where it stopped, and `autopilot eval` exits with code 1. Otherwise it exits with 0, also
   when cases failed.

## Report

```
autopilot eval [--case <id>] [--runs N] [--keep] [--project <folder>]
```

`autopilot eval` writes `.autopilot/out/evals/<yyyyMMdd-HHmmss>/report.md` and `report.json`, prints
a short summary, and keeps each run's agent logs, gate files and hidden test log in
`<case-id>/run-<n>/` next to the report:

- per case: passes out of N, pass rate, pass^N (all N runs passed), mean turns,
  mean tokens (input, output, cache read, cache write), mean wall time, failure reasons;
- totals: pass@1 (mean pass rate over cases), pass^N over cases (how many cases passed all N runs),
  total turns, total tokens;
- the model per role, the tool version, the snapshot (hash of the copied files) and the date;
- a "Failures" section with every failed run and its reasons.

Turns and tokens come from each run's `runs.jsonl`, per role. Runs that stopped on an
infrastructure error are not graded: pass rate and means use the graded runs only, and pass^N is
"unknown" for a case with fewer than N graded runs.

Runs use the Claude subscription's usage limits. `--runs` and the roles' tool-call limits
are the only cost controls.
