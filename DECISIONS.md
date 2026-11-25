# Decisions

Short records of choices made while building Agent Autopilot. Newest at the bottom.
Status is one of: proposed, accepted, rejected, superseded.

## D-001 A tool, not a demo (accepted)

Agent Autopilot is a reusable tool that anyone can install and connect to their own
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
