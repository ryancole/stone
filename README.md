# BehaveDiff

> Working name.

**"What did my uncommitted changes do to the app's runtime behavior compared to the base branch?"**

BehaveDiff runs the same test suite against two builds (the base ref and your working tree), records what the app did while the tests ran (HTTP responses, database writes), and diffs the two recordings. No assertions to write: the base is assumed correct, and every difference is reported, sorted into *intended* vs *unexpected* against a stated intent.

It's built to be called by coding agents (e.g. Claude Code) before they declare a task done, and works the same from a human's terminal.

## Status

Phase 0: capture spike. See [docs/spec.md](docs/spec.md) for the full design and roadmap.

## Requirements

- .NET 10 SDK
- Docker, only if your tests need it (e.g. Testcontainers)
