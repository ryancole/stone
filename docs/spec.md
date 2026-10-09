# BehaveDiff: behavioral diffing for .NET, callable by coding agents

> Working name is **BehaveDiff**.

## What we're building

A .NET tool that answers one question for a developer or a coding agent, mid-task:

**"What did my uncommitted changes do to the app's runtime behavior compared to the base branch?"**

It runs the same test suite against two builds, the base ref and the current working tree. While the tests run, it records what the app *did*: HTTP responses and database writes. Then it diffs the two recordings. You don't write assertions. The base is assumed correct, and every difference is reported, sorted into "intended" vs "unexpected" against a stated intent.

The main consumer is a coding agent (Claude Code), which calls it before declaring a task done and fixes unexpected differences itself. A human uses the same CLI.

## Supported apps

BehaveDiff is a general tool for any .NET repo of this shape. It must not depend on any particular app. App-specific facts (which test project, which headers or fields are noisy, the base branch) live in that app's `.behavediff.yml` or `--config`, never in BehaveDiff's code. It has been validated against a private production API at the user's company. That app's specifics are not part of this repo; the stand-in in `src/Samples` reproduces its shape.

**Required (v1):**
- An **ASP.NET Core** app exercised by a test project, typically in-process via `WebApplicationFactory<T>`. Any hosting model works (`Startup` class or minimal hosting, controllers or minimal APIs, real Kestrel too): capture uses hosting's diagnostic events.
- Tests in **MSTest 4 on Microsoft.Testing.Platform**: `dotnet test --project <csproj>`, with `--report-trx` available. Other frameworks (xUnit v3, NUnit, MSTest on VSTest) are a later input kind; only test attribution is framework-specific.
- **Tests run sequentially.** Attribution relies on it. BehaveDiff detects overlapping TRX windows and fails with a clear message instead of silently mis-attributing.
- A **fresh database per test run**, so both runs start from the same state, e.g. Testcontainers or SQLite created by the fixture. BehaveDiff doesn't snapshot databases in v1.
- **.NET 8+** runtime for the app under test (the startup hook targets net8.0).

**Handled, don't assume otherwise:**
- **EF Core** with any provider, registered any way (`AddDbContext`, `AddDbContextFactory<TContext, CustomFactory>`, pooled). DB writes come from EF diagnostic events, not DI. Apps without EF Core get HTTP observations only.
- **Newtonsoft.Json or System.Text.Json** output, any formatting.
- Unique test data such as `<prefix>-<label>-<guid:N>`. Normalization handles D- and N-format GUIDs embedded inside strings.
- Shared fixtures, including lazy singletons that do one-time setup inside the first test.
- Gitignored local config (`appsettings.*.json`) and user secrets.
- Base branch named anything: default is the remote's default branch (`origin/HEAD`), overridable with `baseRef`.

**Rules for any target:**
- **Never modify the target repo's working tree or committed files.** Everything happens in temp directories, worktrees and environment variables.
- Every `dotnet build` / `dotnet test` passes `-nodeReuse:false`. Lingering MSBuild nodes can lock output DLLs and break later builds, especially with two trees building one after another.
- Docker is needed only if the target's tests need it.

Future input sources: OpenAPI-generated requests, recorded interactive traffic, Azure Functions triggers.

## Core design: observations

The engine does **not** diff HTTP responses specifically. It diffs **observations**. A capture run produces an ordered list:

```json
{ "seq": 12, "test": "WidgetEndpointTests.GetWidget_TagsSerializeAsJsonArray",
  "trigger": "GET /widgets/{id}", "kind": "http-response",
  "data": { "status": 200, "headers": {...}, "body": {...} }, "durationMs": 14 }

{ "seq": 13, "test": "...", "trigger": "POST /widgets", "kind": "db-write",
  "data": { "context": "CatalogContext", "entity": "Widget", "state": "Added",
            "values": {...} } }
```

Later versions add new `kind`s (`blob-written`, `message-sent`) from a pipeline harness for Azure Functions Service Bus and blob triggers. The diff engine, normalization, report, CLI, and MCP tool must not need changes when that happens. Keep `kind` open-ended and the diff logic generic over JSON-shaped `data`.

## Phase 0: capture spike (done; see phase0-report.md)

Both the base worktree **and** the working tree need capture, and the base tree has no reference to our code. So capture has to be injected from outside.

**Approach: zero-touch injection via `DOTNET_STARTUP_HOOKS`.** A small `BehaveDiff.Capture` assembly with a startup hook that subscribes to `DiagnosticListener.AllListeners`:

- **HTTP:** on the ASP.NET Core hosting `HttpRequestIn.Start` event, take the `HttpContext` and wrap `Response.Body` in a tee stream. On `...Stop`, emit an `http-response` observation (method, route template if available, otherwise path, status, selected headers, body).
- **DB writes:** on EF Core's `SaveChanges*` diagnostic events, read `DbContext.ChangeTracker.Entries()` for Added/Modified/Deleted entities and emit `db-write` observations with entity type, state, and property values (for Modified, original and current values for changed properties only). Using diagnostics instead of a DI-registered interceptor avoids depending on how contexts are registered.
- **Test attribution:** tag each observation with the running MSTest test, with no change to the test project.
- Observations are written as JSONL to the directory named by `BEHAVEDIFF_CAPTURE_DIR`. With that var unset, the hook does nothing.

**Fallback if startup hooks don't work:** copy both trees into temp dirs and inject via an MSBuild overlay (`Directory.Build.targets` in the temp copy adding a reference plus `IHostingStartup` / `IStartupFilter`). Never touch the user's real working tree.

## Phase 1: engine + CLI

Solution layout (all code under `src/`, scripts under `etc/`):

- `src/BehaveDiff.Core`: orchestration, normalization, diff, report model. No console I/O.
- `src/BehaveDiff.Capture`: the startup hook from Phase 0.
- `src/BehaveDiff.Cli`: thin wrapper (System.CommandLine), packaged as a .NET global tool.
- `src/BehaveDiff.Core.Tests`: MSTest, to match the target ecosystem.

Flow of `behavediff run`:

1. **Base tree:** `git worktree add` the base ref into a temp directory. Copy untracked config the build needs (e.g. gitignored `appsettings.*.json`) from the working tree when present. User secrets are keyed by `UserSecretsId` and work without copying.
2. **Current tree:** the working tree as-is (or a temp copy if the fallback injection is needed).
3. **Build both** with `-nodeReuse:false`. Fail with a specific message naming which tree and what broke.
4. **Run the configured test project in both,** sequentially, with capture env vars set. Base first.
5. **Self-noise check:** optionally run the base twice. Any observation that differs between two base runs is noise by definition. Turn those paths into automatic ignore rules for this run and list them in the report.
6. **Normalize:** GUIDs (D and N formats, including embedded in strings), ISO timestamps, configured JSON paths and headers, and DB key columns that are identity values.
7. **Match and diff:** pair observations by `(test, kind, trigger, per-test sequence)`. Diff JSON structurally: field added, removed, value changed, type changed. Also report observations that exist on only one side. Flag latency regressions over a threshold, but keep latency out of pass/fail by default since it's noisy in tests.
8. **Report:**
   - Terminal: unexpected differences first (grouped by trigger, with field-level detail and the test that produced them), then intended, then an "N observations unchanged" count.
   - `--json`: same content, structured. This is the MCP tool's contract (see phase1-proposal.md).
   - Exit codes: 0 no differences, 1 differences found, 2 tool or build error.
9. Always clean up: remove the worktree, and leave Testcontainers to clean up its own containers. This applies on success, on error, and on Ctrl+C.

Config, `.behavediff.yml` at the app repo root (shared, committable, no secrets):

```yaml
baseRef: master            # optional; default is the remote's default branch (origin/HEAD)
build:
  extraArgs: ["-nodeReuse:false"]
inputs:
  - kind: mstest
    project: src/Tests/Target.Tests/Target.Tests.csproj
ignore:
  jsonPaths: []
  headers: ["Date", "traceparent", "Request-Context"]
  dbColumns: []
latencyRegressionPct: 100
selfNoiseCheck: true
```

Local per-machine overrides go in `.behavediff.local.yml` (gitignored). Run artifacts go in `.behavediff/` (gitignored). They can contain real data and auth headers, so `behavediff init` must add both to `.gitignore`. The tool must also run against a repo **without** this file committed: support `--config <path>` pointing at a config stored outside the repo.

### Intent sorting

`--intent "<what the change is supposed to do>"` is accepted. In v1, sorting into intended vs unexpected is **deterministic and simple**. Any difference whose trigger matches routes or entities the user lists via `--expect "POST /widgets" --expect "entity:Widget"` is "intended", and everything else is "unexpected". Store the intent text in the report. A future version may use an LLM to classify; the report is designed so a classifier can be added without changing the schema.

## Phase 2: agent integration

- **MCP server** (`BehaveDiff.Mcp`, stdio) wrapping Core:
  - `check_behavior_changes(intent, expect[]?)` → report JSON
  - `explain_difference(id)` → full before/after observations for one difference, plus the test that produced it
  - `accept_difference(id, reason)` → records the acceptance in `.behavediff/accepted.json`; accepted items appear in a separate section of later reports
- A **`CLAUDE.md` snippet** (as a doc file in this repo) telling the agent when to run the check, to fix or `accept_difference` every unexpected item with a reason, and never to add ignore rules without saying so.

## Acceptance tests (run them before saying a phase is done)

Run them against the stand-in in `src/Samples` (here, every time) and against at least one real app of the supported shape (a **disposable clone** in a temp directory, never the real checkout). The real app's specifics stay out of this repo.

1. Clean tree → `behavediff run` exits 0. Any noise found by the self-noise check is listed, not reported as differences.
2. Regress a response serialization the app's tests were written to guard (e.g. a list field emitted as a comma-separated string instead of a JSON array) → the report shows the field-level change on the affected endpoints and exits 1.
3. Make a change that alters a DB write without changing any response (e.g. a field defaulted differently on create) → reported as a `db-write` difference even though every test still passes. **This is the headline demo.**
4. Same as 3, but with `--expect "entity:<that entity>"` → the difference moves to "intended".
5. Build break in the working tree → exit 2 with a message naming the working tree and the compiler error.

## Out of scope (design for it, don't build it)

- Azure Functions pipeline harness (Service Bus and blob triggers → `blob-written` / `message-sent` observations)
- Recording inputs from local interactive use of the app, and OpenAPI-generated inputs
- Running only the tests affected by the change, and caching base results per commit
- GitHub Action / PR comments
- LLM-based intent classification

## Working style

- Phase by phase; stop and report after each.
- Before Phase 1 code, the Core public types and report JSON schema are proposed and approved.
- Keep Core free of console I/O and of anything target-specific. Target facts belong in config and acceptance tests only.
- Prefer small, verifiable steps. Run the acceptance tests and show their actual output when reporting a phase done.
