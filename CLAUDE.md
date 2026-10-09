# BehaveDiff

Behavioral diffing for .NET: run the same test suite against the base ref and the working tree, record what the app *did* (HTTP responses, DB writes, …) as **observations**, and diff the two recordings. Full spec: [docs/build-prompt.md](docs/build-prompt.md). "BehaveDiff" is a working name.

## Current phase

**Phase 0 done** ([docs/phase0-report.md](docs/phase0-report.md)); capture fixes since then need one re-run of `etc/spike.ps1` on the WhatInBox machine.
**Phase 1: proposal written, awaiting the user's OK** ([docs/phase1-proposal.md](docs/phase1-proposal.md)). No Phase 1 code until approved.

## Hard rules

- **Never modify `C:\Users\Ryan\source\repos\WhatInBox`** — not its working tree, not its commits. Work in temp dirs / `git worktree` / env vars only. Acceptance tests use a disposable clone in a temp dir.
- Every `dotnet build` / `dotnet test` (here and against the target) passes `-nodeReuse:false`.
- Tests on MTP: `dotnet test --project <csproj> -nodeReuse:false`, not the VSTest form.
- `BehaveDiff.Core` has no console I/O and nothing WhatInBox-specific. Target facts live in config and acceptance tests.
- Observation `kind` stays open-ended; diff/normalize/report logic is generic over JSON-shaped `data`.
- Don't claim a phase is done without running the acceptance tests and showing actual output.

## Two machines

WhatInBox only runs on the user's other dev machine. Verify here against the stand-in (`src/Samples/SampleApi` + `SampleApi.Tests`: Startup class, Newtonsoft, `AddDbContextFactory` with a custom factory, MSTest 4 on MTP, SQLite since this machine has no Docker). Anything that must run against WhatInBox goes through a script in `etc/` that the user runs there and that produces a bundle in `.spike-out/` (gitignored) to bring back.

- `etc/spike.ps1 -TestProject src/Samples/SampleApi.Tests/SampleApi.Tests.csproj` — local stand-in
- `etc/spike.ps1 -RepoPath <WhatInBox> -TestProject src/Tests/WhatInBox.Tests.Regression/WhatInBox.Tests.Regression.csproj` — other machine; clones to temp, never touches the original

## Repo layout

- `src/` — all code (projects and tests)
- `etc/` — scripts (spike runners, acceptance-test drivers, etc.)
- `docs/` — spec and docs

Planned projects (Phase 1). This replaces the `tests/` folder in the spec; tests live under `src/`:

- `src/BehaveDiff.Core` — orchestration, normalization, diff, report model
- `src/BehaveDiff.Capture` — startup hook (from Phase 0)
- `src/BehaveDiff.Cli` — System.CommandLine, packaged as a global tool
- `src/BehaveDiff.Mcp` — stdio MCP server (Phase 2)
- `src/BehaveDiff.Core.Tests` — MSTest
