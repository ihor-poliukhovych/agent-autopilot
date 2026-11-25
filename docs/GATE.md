# The merge gate

The gate decides whether an agent's change may reach a person as a draft. It is C#
code (`src/Autopilot.Core/Gate/MergeGate.cs`), not a prompt, and it works only on
facts it collects itself. What the agent says it did is not evidence.

Its result is `Passed` (a draft is published) or `Rejected` (the branch is kept and
`REJECTED.md` lists every failed rule). There is no "merge" result: a person always
reviews and merges.

## Facts the gate collects

- The diff between the base commit and the agent branch (`git diff --numstat -M`).
- Build and test results from **clean checkouts** of the base commit and of the agent
  branch, made by the gate, not the agent's own worktree.
- The base commit's result is cached in `.autopilot/out/cache/gate-base/<key>.json`. The
  key is a hash of the base commit, the build and test command lines and the tool
  version, so changing any of them makes a new key. The gate log says when it reused a
  cached result. Only a complete result (build passed, tests counted) is cached; delete
  the folder to force a fresh run.
- Test counts from TRX files that `dotnet test` writes into a folder the gate owns.
  Summary text in the output is ignored, so a test that prints a fake summary changes
  nothing.
- The evidence chain: the work item, the stored triage result (and a hash of the work
  item text it was made for), the implementer's self-review, the reviewer's verdict.

## Rules

| Rule | Fails when | High risk |
|---|---|---|
| `scope.files` | more files changed than `gate.maxFilesChanged` (default 6) | |
| `scope.source-lines` | added + removed lines outside tests above `gate.maxSourceLinesChanged` (150) | |
| `scope.test-lines` | added + removed lines in tests above `gate.maxTestLinesChanged` (300) | |
| `scope.binary` | a binary file changed | yes |
| `paths.allowed` | a changed path (or a rename's old path) is outside `writablePaths` | |
| `paths.protected` | a change touches `.autopilot/` or a `context` path | |
| `paths.risk` | a change touches `riskPaths` | yes |
| `tests.build` | the branch does not build | |
| `tests.pass` | the branch's tests fail | |
| `tests.count` | fewer tests than on the base, or tests could not be counted | |
| `tests.skips` | tests newly skipped | |
| `tests.deleted` | a test file deleted or renamed out of the test paths | |
| `evidence.work-item` | no work item id | |
| `evidence.triage` | no triage, or triage did not say `agent-ready` | |
| `evidence.self-review` | the implementer gave no self-review | |
| `evidence.review` | no review, or the reviewer did not approve | |
| `evidence.test-change` | non-test code changed but no test file gained a line (`gate.requireTestChange`) | |

All rules run every time, so a rejection lists everything that is wrong at once.

## Rules enforced before the gate

The gate is the last line. Earlier, the PreToolUse hook stops the agent from:

- writing outside `writablePaths`, or to `.autopilot/`, `riskPaths` or `context` paths at all;
- reading or writing outside its worktree, inside `.git`, or through links;
- running shell commands other than the role's list, chaining or redirecting them,
  using variables, or passing options that write or read elsewhere (`--output`, `-o`,
  `--no-index`, absolute paths, `..`, and others). The implementer may always run the
  configured `commands.build` and `commands.test` lines exactly as written, plus extra
  trailing arguments that pass these rules;
- using tools without a policy (web access, subagents, other shells);
- going past its tool-call limit.

If the hook fails or times out, the call is denied.

## What the gate does not prove

It proves the change is small, in the right place, builds, keeps and passes the
tests, and went through triage and review. It does not prove the change is what the
person wanted. That is why a person approves every merge.
