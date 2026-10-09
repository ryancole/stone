# BehaveDiff

Behavioral diffing for .NET: run the same test suite against the base ref and the working tree, record what the app *did* (HTTP responses, DB writes, …) as **observations**, and diff the two recordings. Full spec: [docs/build-prompt.md](docs/build-prompt.md). "BehaveDiff" is a working name.

## Current phase

**Phase 0 — capture spike.** Prove `DOTNET_STARTUP_HOOKS` injection works under MSTest/Microsoft.Testing.Platform against WhatInBox, then stop and report (mechanism, test attribution, JSONL sample, surprises). Do not build Phase 1 until reported.

Before Phase 1 code: propose the Core public types (`Observation`, `Difference`, `Report`) and the `--json` report schema, and wait for an OK.

## Hard rules

- **Never modify `C:\Users\Ryan\source\repos\WhatInBox`** — not its working tree, not its commits. Work in temp dirs / `git worktree` / env vars only. Acceptance tests use a disposable clone in a temp dir.
- Every `dotnet build` / `dotnet test` (here and against the target) passes `-nodeReuse:false`.
- Tests on MTP: `dotnet test --project <csproj> -nodeReuse:false`, not the VSTest form.
- `BehaveDiff.Core` has no console I/O and nothing WhatInBox-specific. Target facts live in config and acceptance tests.
- Observation `kind` stays open-ended; diff/normalize/report logic is generic over JSON-shaped `data`.
- Don't claim a phase is done without running the acceptance tests and showing actual output.

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
