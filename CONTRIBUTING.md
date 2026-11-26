# Contributing

Thanks for helping. This page is short on purpose.

## Workflow

1. Open an issue first for anything bigger than a small fix (use the templates).
2. Branch from `main`: `feat/<topic>`, `fix/<topic>`, `docs/<topic>`.
3. Keep a change focused: one idea per pull request.
4. Run `dotnet build` and `dotnet test` before you push.
5. Open a pull request; the template asks for what changed, why, and how you checked it.

Pull requests are squash-merged, so the **pull request title** becomes the commit on `main`
and must follow the commit format below.

## Commit format

[Conventional Commits](https://www.conventionalcommits.org/), in English:

```
<type>(<scope>): <subject>

<body: what and why, wrapped at 72 columns>

<footer: Closes #123, BREAKING CHANGE: ...>
```

- **type**: `feat`, `fix`, `docs`, `refactor`, `test`, `perf`, `build`, `ci`, `chore`.
- **scope** (optional): `core`, `cli`, `ui`, `azure-devops`, `claude-code`, `diagnostics`,
  `workspace`, `docs`.
- **subject**: imperative mood ("add", not "added"), lower case, no final full stop,
  at most 72 characters.
- **body**: explain why the change is needed and what it does, not how the code reads.
- Mark a breaking change with `!` after the scope (`feat(cli)!: ...`) and a
  `BREAKING CHANGE:` footer.

Examples:

```
feat(workspace): add a SQLite project registry
fix(cli): stop pool run when the claude process fails to start
docs: describe the Azure DevOps service principal setup
```

Enable the local check once per clone:

```powershell
git config core.hooksPath .githooks
git config commit.template .gitmessage
```

## Rules for every commit

- No secrets, tokens, internal hostnames, customer or employer names, in code, docs,
  tests or messages.
- Commit with a personal address you are happy to show publicly.
- No generated-by or co-author trailers for tools.

## Issues

Use the templates: a bug needs steps, expected and actual behaviour, and the version.
A feature request needs the problem first, then the idea.
