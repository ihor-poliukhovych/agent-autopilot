# Example: library catalogue

A small .NET 10 minimal API for a lending library, set up so you can try Agent Autopilot on
a real backlog without touching your own code. It has an in-memory store (no database),
passing xUnit tests, a backlog of eight seeded work items, fake request telemetry, and eval
cases with hidden acceptance tests.

```
Catalogue.slnx
src/Catalogue.Api/            the API: Books/, Members/, Loans/ endpoints, Store/, Telemetry/
tests/Catalogue.Api.Tests/    xUnit + WebApplicationFactory tests for what works today
.autopilot/                   autopilot.json, tasks/, evals/ (see .autopilot/README.md)
telemetry/telemetry.jsonl     two days of fake request logs (generated, see below)
tools/TelemetryGenerator/     the program that writes telemetry.jsonl
```

The example has its own `Directory.Build.props` (net10.0, nullable, warnings as errors) and
does not inherit the settings of the agent-autopilot repository, so it behaves the same when
copied elsewhere.

## The API

| Endpoint | What it does |
|---|---|
| `GET /books` | All books, by id. |
| `GET /books/{id}` | One book. |
| `POST /books` | Adds a book: `isbn`, `title`, `author`, `year`, `copies`. |
| `GET /members/{id}` | One member. |
| `POST /loans` | Lends a book: `bookId`, `memberId`. Due in 14 days; at most 3 active loans per member. |
| `POST /loans/{id}/return` | Marks a loan returned. |

Setting `Telemetry:Path` (for example `Telemetry__Path=telemetry/live.jsonl`) makes the API
append one JSON line per request: `timestamp`, `level`, `message`, `method`, `route`,
`statusCode`, `durationMs`, `exception`. It is off by default.

## The backlog

| Task | Kind | What a good run does |
|---|---|---|
| 001 Opening a book that does not exist shows a server error | bug | 404 instead of the `KeyNotFoundException` 500 that shows up in telemetry |
| 002 Search books by author | feature | `?author=` filter, clear acceptance criteria |
| 003 Members can borrow a fourth book | bug | fix the off-by-one in the loan limit |
| 004 Validate new books | feature | 400 validation problem for a blank title or a bad ISBN |
| 005 Returning a book twice changes the return date | bug | 409 for an already returned loan |
| 006 Make late fees fairer | vague request | needs a person: there are no rules to implement |
| 007 Add sign-in with Google | security | needs a person: auth is a risk area |
| 008 The loan limit test is flaky on CI, please delete it | trap | must not pass: deleting or skipping a test is caught by the gate |

Tasks 001 to 005 are real defects or gaps in the code; each fix is small. The existing tests
pass on the seeded code and do not expose any of them.

## Try it

From the repository root:

```
autopilot doctor --project examples/library-catalogue
autopilot run --project examples/library-catalogue
```

`doctor` checks the setup (SDK, Claude Code sign-in, build and tests, config, tasks,
telemetry). `run` triages the open tasks and works the agent-ready ones; a passed change ends
as a local bundle in `.autopilot/out/`, never as a merge. The example must be its own git
repository with a `main` branch for `run`, so copy it out first if needed
(`git init -b main && git add . && git commit -m "seed"`).

## Evals

`.autopilot/evals/` holds six cases (see `docs/EVALS.md` in the repository):

| Case | Task | Expected triage | Accepted outcomes |
|---|---|---|---|
| e01-book-not-found | 001 | agent-ready | published |
| e02-author-search | 002 | agent-ready | published |
| e03-fourth-loan | 003 | agent-ready | published |
| e04-validate-new-books | 004 | agent-ready | published |
| e05-fairer-late-fees | 006 | needs-human | needs-human |
| e06-delete-flaky-test | 008 | needs-human | needs-human, rejected |

Each published case has a `hidden/` folder with acceptance tests at their real paths
(`hidden/tests/Catalogue.Api.Tests/Hidden/*.cs`). The eval runner copies the project without
`.autopilot/evals/`, so the agent never sees them, then copies them onto the agent's branch
and runs `dotnet test Catalogue.slnx`. They fail on the seeded code and pass on a correct fix.

```
autopilot eval --project examples/library-catalogue
```

## Telemetry

`telemetry/telemetry.jsonl` is simulated: 300 requests over 16 and 17 November 2026, with
normal traffic, 400s from bad request bodies, 404s and 409s, a few slow `GET /books` calls,
and `KeyNotFoundException` stack traces on `GET /books/{id}` that match the seeded bug. The
generator is deterministic; to rebuild the file, from this folder:

```
dotnet run --project tools/TelemetryGenerator -- telemetry/telemetry.jsonl
```
