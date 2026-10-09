# BehaveDiff

Behavioral diffing for .NET: run the same test suite against the base ref and the working tree, record what the app *did* (HTTP responses, DB writes, …) as **observations**, and diff the two recordings. Full spec: [docs/spec.md](docs/spec.md). "BehaveDiff" is a working name.

**BehaveDiff is a general tool** for any .NET repo of the supported shape (ASP.NET Core + optional EF Core, MSTest 4 on MTP, sequential tests, fresh DB per run; see spec "Supported apps"). Never build anything around one particular app. Per-app facts go in that app's `.behavediff.yml` / `--config`.

**This repo is public.** The user validates against a private reference app from work. Its name, paths, entity/test names and captured data never go into tracked files or commit messages; say "the reference app". Its specifics live in the gitignored `CLAUDE.local.md`.

## Current phase

**Phase 0 done** ([docs/phase0-report.md](docs/phase0-report.md)).
**Phase 1 implemented** (proposal approved: [docs/phase1-proposal.md](docs/phase1-proposal.md); §9 lists implementation notes). Stand-in acceptance: `etc/acceptance.ps1` (5/5). Reference-app acceptance runs on the other machine via `etc/try-on-repo.ps1` (commands in `CLAUDE.local.md`).
**Phase 2 next:** MCP server + agent CLAUDE.md snippet.

## Hard rules

- **Never modify a target repo** (the reference app or any other): not its working tree, not its commits. Work in temp dirs / `git worktree` / env vars only. Acceptance tests use a disposable clone in a temp dir.
- Every `dotnet build` / `dotnet test` (here and against any target) passes `-nodeReuse:false`.
- Tests on MTP: `dotnet test --project <csproj> -nodeReuse:false`, not the VSTest form.
- `BehaveDiff.Core` and `BehaveDiff.Capture` contain nothing app-specific. App facts live in config; the stand-in in `src/Samples` is the test fixture.
- Observation `kind` stays open-ended; diff/normalize/report logic is generic over JSON-shaped `data`.
- Don't claim a phase is done without running the acceptance tests and showing actual output.

## Two machines

The reference app only runs on the user's other dev machine. Verify here against the stand-in (`src/Samples/SampleApi` + `SampleApi.Tests`: Startup class, Newtonsoft, `AddDbContextFactory` with a custom factory, MSTest 4 on MTP, SQLite since this machine has no Docker). Anything that must run against it goes through a script in `etc/` that the user runs there and that produces a bundle in `.spike-out/` (gitignored) to bring back. Push after committing; the other machine pulls from `origin`.

- `etc/spike.ps1 -TestProject src/Samples/SampleApi.Tests/SampleApi.Tests.csproj`: local stand-in
- `etc/spike.ps1 -RepoPath <repo> -TestProject <test csproj>`: any real app, e.g. the reference app on the other machine; clones to temp, never touches the original
- `etc/acceptance.ps1`: the five Phase 1 acceptance scenarios against a throwaway repo made from the stand-in
- `etc/try-on-repo.ps1 -RepoPath <repo> -TestProject <csproj> [-Edit "path::find::replace"] [-Expect ...]`: `behavediff run` on a disposable clone of any repo, optionally after edits

## Repo layout

- `src/`: all code (projects and tests)
- `etc/`: scripts (spike runners, acceptance-test drivers, etc.)
- `docs/`: spec and docs

Planned projects (Phase 1):

- `src/BehaveDiff.Core`: orchestration, normalization, diff, report model
- `src/BehaveDiff.Capture`: startup hook (from Phase 0)
- `src/BehaveDiff.Cli`: System.CommandLine, packaged as a global tool
- `src/BehaveDiff.Mcp`: stdio MCP server (Phase 2)
- `src/BehaveDiff.Core.Tests`: MSTest
