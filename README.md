# BehaveDiff

> Working name.

**"What did my uncommitted changes do to the app's runtime behavior compared to the base branch?"**

BehaveDiff runs the same test suite against two builds (the base ref and your working tree), records what the app did while the tests ran (HTTP responses, database writes, test outcomes), and diffs the two recordings. No assertions to write: the base is assumed correct, and every difference is reported, sorted into *intended* vs *unexpected* against a stated intent.

It's built to be called by coding agents (e.g. Claude Code) before they declare a task done, and works the same from a human's terminal.

## Works with

ASP.NET Core apps (EF Core optional) tested by an MSTest 4 project on Microsoft.Testing.Platform, with tests running sequentially against a fresh database per run. See [docs/spec.md](docs/spec.md#supported-apps).

Capture is injected through `DOTNET_STARTUP_HOOKS`; your app and test projects need no changes.

## Usage

```
behavediff init                      # writes .behavediff.yml, gitignores .behavediff/ and .behavediff.local.yml
behavediff run                       # base branch vs working tree
behavediff run --intent "Default new widgets to Low priority" --expect "entity:Widget"
behavediff run --json                # machine-readable report (schema: docs/phase1-proposal.md)
```

Exit codes: `0` no differences, `1` differences found, `2` tool or build error.

From source: `dotnet run --project src/BehaveDiff.Cli -- run --repo <path>`.

## Status

Phase 1 (engine + CLI) implemented. Next: MCP server for agents. Design and roadmap: [docs/spec.md](docs/spec.md).

## Requirements

- .NET 10 SDK
- Docker, only if your tests need it (e.g. Testcontainers)
