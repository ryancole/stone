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
behavediff explain <id>              # full before/after observations for one difference
behavediff accept <id> --reason ".." # accept a difference; later runs report it as Accepted
behavediff mcp                       # MCP server (stdio) for coding agents
```

Exit codes: `0` no differences, `1` differences found, `2` tool or build error.

Install from source: `dotnet pack src/BehaveDiff.Cli -c Release -o ./nupkg`, then `dotnet tool install -g BehaveDiff --add-source ./nupkg`.

## Coding agents

`claude mcp add behavediff -- behavediff mcp` gives Claude Code three tools: `check_behavior_changes`, `explain_difference` and `accept_difference`. [docs/agent-instructions.md](docs/agent-instructions.md) has the setup and a `CLAUDE.md` snippet that tells the agent when to check and how to handle each difference.

## Status

Phase 2 (MCP server + agent instructions) implemented. Design and roadmap: [docs/spec.md](docs/spec.md).

## Requirements

- .NET 10 SDK
- Docker, only if your tests need it (e.g. Testcontainers)
