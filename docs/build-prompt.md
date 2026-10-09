# Build prompt: BehaveDiff — behavioral diffing for .NET, callable by coding agents

> Working name is **BehaveDiff**. Find/replace it once a final name is picked.

## What we're building

A .NET tool that answers one question for a developer or a coding agent, mid-task:

**"What did my uncommitted changes do to the app's runtime behavior compared to `master`?"**

It runs the same test suite against two builds, the base ref and the current working tree. While the tests run, it records what the app *did*: HTTP responses and database writes. Then it diffs the two recordings. You don't write assertions. The base is assumed correct, and every difference is reported, sorted into "intended" vs "unexpected" against a stated intent.

The main consumer is a coding agent (Claude Code), which calls it before declaring a task done and fixes unexpected differences itself. A human uses the same CLI.

## The target repo (first real user)

The first target is **WhatInBox**, a production repo at `C:\Users\Ryan\source\repos\WhatInBox`. **The tool must never modify that repo's working tree or committed files.** Everything it needs happens in temp directories and through environment variables.

Facts about the target that drive the design:

- .NET 10 SDK (`global.json`, `rollForward: latestFeature`). Tests use **MSTest 4 on Microsoft.Testing.Platform**, so the command is `dotnet test --project <csproj>`, not the legacy VSTest form.
- Default branch is **`master`**.
- **Every** `dotnet build` / `dotnet test` must pass `-nodeReuse:false`. Lingering MSBuild nodes lock output DLLs in this repo and break later builds. This is especially important with two trees building one after another.
- Input source for v1: `src/Tests/WhatInBox.Tests.Regression`. That's 25 MSTest tests driving `WhatInBoxWorkspaceApi` in-process through `WebApplicationFactory<Startup>` (see `Fixtures/WibApiFactory.cs`), with a test auth handler.
- The DB is a **fresh SQL Server per test run via Testcontainers** (`Fixtures/MsSqlContainerFixture.cs`, `EnsureCreatedAsync`). Each of the two runs gets its own clean database, so no snapshotting is needed for v1. Docker must be running.
- Tests are not parallelized (MSTest default, nothing overrides it), so request order within a run is deterministic.
- Tests generate unique names like `wf-<label>-<guid:N>` (32-hex GUIDs with no dashes), so these appear in requests and responses. Normalization must handle N-format GUIDs embedded inside strings, not just whole-value GUIDs.
- The API registers EF contexts with **`AddDbContextFactory<TContext, CustomFactory>`** (`WibWorkspaceContext`, `WibAdmin2Context`) and serializes with **Newtonsoft.Json**. Don't assume `AddDbContext`, and don't assume System.Text.Json output formatting.
- The API also exposes OpenAPI. That's not used in v1, but it's a future input source.

## Core design: observations

The engine does **not** diff HTTP responses specifically. It diffs **observations**. A capture run produces an ordered list:

```json
{ "seq": 12, "test": "WorkflowEndpointRegressionTests.GetWorkflow_EnabledModulesSerializesAsJsonArrayOfNames",
  "trigger": "GET /workflows/{id}", "kind": "http-response",
  "data": { "status": 200, "headers": {...}, "body": {...} }, "durationMs": 14 }

{ "seq": 13, "test": "...", "trigger": "POST /workflows", "kind": "db-write",
  "data": { "context": "WibWorkspaceContext", "entity": "Workflow", "state": "Added",
            "values": {...} } }
```

Later versions add new `kind`s (`blob-written`, `message-sent`) from a pipeline harness for Azure Functions Service Bus and blob triggers. The diff engine, normalization, report, CLI, and MCP tool must not need changes when that happens. Keep `kind` open-ended and the diff logic generic over JSON-shaped `data`.

## Phase 0 — capture spike (do this first, report back before building anything else)

The hard constraint is that both the `master` worktree **and** the working tree need capture, and the master tree has no reference to our code. So capture has to be injected from outside.

**Preferred approach: zero-touch injection via `DOTNET_STARTUP_HOOKS`.**
Build a small `BehaveDiff.Capture` assembly with a startup hook that subscribes to `DiagnosticListener.AllListeners`:

- **HTTP:** on the ASP.NET Core hosting `Microsoft.AspNetCore.Hosting.HttpRequestIn.Start` event, take the `HttpContext` and wrap `Response.Body` in a tee stream. On `...Stop`, emit an `http-response` observation (method, route template if available, otherwise path, status, selected headers, body).
- **DB writes:** on the EF Core diagnostic events (check the exact event name for EF Core 10, e.g. `SaveChangesStarting`), read `DbContext.ChangeTracker.Entries()` for Added/Modified/Deleted entities and emit `db-write` observations with entity type, state, and property values (for Modified, original and current values for changed properties only). Using diagnostics instead of a DI-registered interceptor avoids the custom `AddDbContextFactory` registration.
- **Test attribution:** find a reliable way to tag each observation with the currently running MSTest test name, with no change to the test project. Investigate and pick one. If none works, fall back to per-run sequence numbers, since tests run sequentially. Don't guess: prove it on the real repo.
- Observations are written as JSONL to the directory named by the `BEHAVEDIFF_CAPTURE_DIR` env var. With that var unset, the hook does nothing.

Spike success criteria: with `DOTNET_STARTUP_HOOKS` + `BEHAVEDIFF_CAPTURE_DIR` set, running `dotnet test --project src/Tests/WhatInBox.Tests.Regression/WhatInBox.Tests.Regression.csproj -nodeReuse:false` in an untouched WhatInBox checkout produces a JSONL file with HTTP responses and DB writes for all 25 tests.

**Fallback if startup hooks don't work under MTP's test host:** copy both trees into temp dirs and inject via an MSBuild overlay (`Directory.Build.targets` in the temp copy adding a reference plus `IHostingStartup` / `IStartupFilter`). This must still never touch the user's real working tree.

**Stop after Phase 0 and report:** which mechanism worked, how test attribution works, a sample of the JSONL, and anything surprising.

## Phase 1 — engine + CLI

Solution layout (new repo, separate from WhatInBox):

- `src/BehaveDiff.Core`: orchestration, normalization, diff, report model. No console I/O.
- `src/BehaveDiff.Capture`: the startup hook from Phase 0.
- `src/BehaveDiff.Cli`: thin wrapper (System.CommandLine), packaged as a .NET global tool.
- `tests/BehaveDiff.Core.Tests`: MSTest, to match the target ecosystem.

Flow of `behavediff run`:

1. **Base tree:** `git worktree add` the base ref (default `master`) into a temp directory. Copy untracked config the build needs (e.g. gitignored `appsettings.*.json`) from the working tree when present. User secrets are keyed by `UserSecretsId` and work without copying.
2. **Current tree:** the working tree as-is (or a temp copy if the fallback injection is needed).
3. **Build both** with `-nodeReuse:false`. Fail with a specific message naming which tree and what broke.
4. **Run the configured test project in both,** sequentially, with capture env vars set. Base first.
5. **Self-noise check (important):** optionally run the base twice. Any observation that differs between two base runs is noise by definition. Turn those paths into automatic ignore rules for this run and list them in the report.
6. **Normalize:** GUIDs (D and N formats, including embedded in strings), ISO timestamps, configured JSON paths and headers, and DB key columns that are identity values.
7. **Match and diff:** pair observations by `(test, kind, trigger, per-test sequence)`. Diff JSON structurally: field added, removed, value changed, type changed. Also report observations that exist on only one side (e.g. the branch writes a row the base never wrote). Flag latency regressions over a threshold, but keep latency out of pass/fail by default since it's noisy in tests.
8. **Report:**
   - Terminal: unexpected differences first (grouped by trigger, with field-level detail and the test that produced them), then intended, then an "N observations unchanged" count.
   - `--json`: same content, structured. **Propose this schema before implementing it.** It's the MCP tool's contract.
   - Exit codes: 0 no differences, 1 differences found, 2 tool or build error.
9. Always clean up: remove the worktree, and leave Testcontainers to clean up its own containers. This applies on success, on error, and on Ctrl+C.

Config, `.behavediff.yml` at the target repo root (shared, committable, no secrets):

```yaml
baseRef: master
build:
  extraArgs: ["-nodeReuse:false"]
inputs:
  - kind: mstest
    project: src/Tests/WhatInBox.Tests.Regression/WhatInBox.Tests.Regression.csproj
ignore:
  jsonPaths: []
  headers: ["Date", "traceparent", "Request-Context"]
  dbColumns: []
latencyRegressionPct: 100
selfNoiseCheck: true
```

Local per-machine overrides go in `.behavediff.local.yml` (gitignored). Run artifacts go in `.behavediff/` (gitignored). They can contain real data and auth headers, so `behavediff init` must add both to `.gitignore`. For v1, the tool should run against WhatInBox **without** this file being committed there: support `--config <path>` pointing at a config stored outside the repo.

### Intent sorting

`--intent "<what the change is supposed to do>"` is accepted. In v1, sorting into intended vs unexpected is **deterministic and simple**. Any difference whose trigger matches routes or entities the user lists via `--expect "POST /workflows" --expect "entity:Workflow"` is "intended", and everything else is "unexpected". Store the intent text in the report. A future version may use an LLM to classify; design the report so a classifier can be added without changing the schema.

## Phase 2 — agent integration

- **MCP server** (`BehaveDiff.Mcp`, stdio) wrapping Core:
  - `check_behavior_changes(intent, expect[]?)` → report JSON
  - `explain_difference(id)` → full before/after observations for one difference, plus the test that produced it
  - `accept_difference(id, reason)` → records the acceptance in `.behavediff/accepted.json`; accepted items appear in a separate section of later reports
- A **`CLAUDE.md` snippet** (write it as a doc file, not into WhatInBox) telling the agent when to run the check, to fix or `accept_difference` every unexpected item with a reason, and never to add ignore rules without saying so.

## Acceptance tests (run them yourself before saying a phase is done)

Use a **disposable clone** of WhatInBox in a temp directory. Never the real checkout.

1. Clean tree → `behavediff run` exits 0. Any noise found by the self-noise check is listed, not reported as differences.
2. Change the `enabledModules` serialization back to a comma-separated string, the exact regression `WorkflowEndpointRegressionTests` was written to catch → the report shows the field-level change on the affected workflow endpoints and exits 1.
3. Make a change that alters a DB write without changing any response (e.g. a field defaulted differently on create) → reported as a `db-write` difference even though every test still passes. **This is the headline demo**, so make sure it works.
4. Same as 3, but with `--expect "entity:Workflow"` → the difference moves to "intended".
5. Build break in the working tree → exit 2 with a message naming the working tree and the compiler error.

## Out of scope (design for it, don't build it)

- Azure Functions pipeline harness (Service Bus and blob triggers → `blob-written` / `message-sent` observations)
- Recording inputs from local interactive use of the app, and OpenAPI-generated inputs
- Running only the tests affected by the change, and caching base results per commit
- GitHub Action / PR comments
- LLM-based intent classification

## Working style

- Phase 0 first, then stop and report.
- Before Phase 1 code, propose the Core public types (`Observation`, `Difference`, `Report`) and the report JSON schema, and wait for an OK.
- Keep Core free of console I/O and of anything WhatInBox-specific. WhatInBox facts belong in config and acceptance tests only.
- Prefer small, verifiable steps. Run the acceptance tests yourself, and show their actual output when reporting a phase done.
