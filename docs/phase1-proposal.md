# Phase 1 proposal: Core types and report schema

Status: **awaiting OK.** No Phase 1 code until this is approved. The report JSON (below) is the contract for the MCP tool in Phase 2.

## 1. Core public types

```csharp
namespace BehaveDiff.Core;

// ---- Recording ---------------------------------------------------------------

/// One thing the app did. Kind is open-ended ("http-response", "db-write", later
/// "blob-written", "message-sent"); nothing in Core switches on it except optional
/// per-kind hints (see IdentityHints).
public sealed record Observation(
    long Seq,                 // emission order within the run
    string Test,              // "SessionEndpointRegressionTests.GetSessions_NoAuthHeader_Returns401", or "(setup)"
    string Kind,
    string Trigger,           // "POST /workflows", "(test code)"
    JsonNode Data,
    double? DurationMs,
    DateTimeOffset Timestamp);

public sealed record Recording(
    string Tree,              // "base" | "current"
    string Ref,               // "master" / "working tree"
    string Commit,            // HEAD of that tree (+ "-dirty" for current with changes)
    IReadOnlyList<TestResult> Tests,          // from TRX: name, outcome, start, end
    IReadOnlyList<Observation> Observations);

/// An input that drives the app and yields observations. v1: MSTest via MTP.
/// The Azure Functions pipeline harness becomes another implementation later.
public interface IInputRunner
{
    Task<Recording> RunAsync(PreparedTree tree, CancellationToken ct);
}

// ---- Diff --------------------------------------------------------------------

public enum DifferenceKind
{
    ValueChanged, TypeChanged, FieldAdded, FieldRemoved,   // inside a matched pair
    ObservationAdded, ObservationRemoved,                  // exists on one side only
}

public enum Classification { Unexpected, Intended, Accepted }

public sealed record Difference(
    string Id,                // stable: hash(match key + path); same change -> same id across runs
    DifferenceKind Kind,
    Classification Classification,
    ClassificationReason Reason,
    string Test,
    string ObservationKind,   // "http-response" | "db-write" | ...
    string Trigger,
    string? Entity,           // db-write only (data.entity); null otherwise
    string? Path,             // JSON path inside data, e.g. "$.body.workflow.enabledModules"; null for Observation*
    JsonNode? Before,
    JsonNode? After,
    MatchKey Match);          // what was paired; lets explain_difference fetch both observations

/// Why something landed where it did. A future LLM classifier adds Source = "llm"
/// with a rationale; no schema change.
public sealed record ClassificationReason(string Source, string? Rule, string? Note);
// Source: "default" | "expect" (Rule = "POST /workflows" / "entity:Workflow") | "accepted" (Note = reason) | later "llm"

public sealed record MatchKey(string Test, string Kind, string Trigger, string? Entity, int Occurrence);

public sealed record LatencyFlag(MatchKey Match, double BaseMs, double CurrentMs, double ChangePct);

public sealed record NoiseRule(string Kind, string Trigger, string Path, string Origin);
// Origin: "config" | "self-noise" (differed between two base runs) | "default"

// ---- Report ------------------------------------------------------------------

public sealed record Report(
    int SchemaVersion,        // 1
    RunInfo Run,
    IntentInfo Intent,
    ReportSummary Summary,
    IReadOnlyList<Difference> Differences,  // sorted: Unexpected, Intended, Accepted; then trigger, test, seq
    IReadOnlyList<LatencyFlag> Latency,     // informational, never affects exit code by default
    IReadOnlyList<NoiseRule> Noise,
    IReadOnlyList<MatchKey> Moved,          // identical observations re-paired across tests (lazy fixtures)
    IReadOnlyList<RunError> Errors);        // build/test failures; non-empty => exit 2
```

Core entry point: `BehaveDiffRunner.RunAsync(RunOptions, IProgress<RunEvent>?, CancellationToken) -> Report`. The CLI and the MCP server both call it, so neither does any orchestration of its own.

## 2. Matching

Within each run, observations are grouped by `(test, kind, trigger, entity)`. They're paired by **occurrence index** within that group, so the 2nd `POST /workflows` in a test pairs with the 2nd on the other side. `entity` is part of the key for db-writes, so one save that writes a `Project` and a `Taxonomy` doesn't cross-pair. Leftovers become `ObservationAdded` / `ObservationRemoved`.

Setup observations use test `"(setup)"` and match like any other test.

**Cross-test re-pairing.** Fixtures that initialize lazily do their one-time work inside whichever test runs first. WhatInBox's `WibApiFactory` seeds its `User` that way. If a branch changes which test runs first, that work moves between tests. So after normal matching, leftover `ObservationRemoved` / `ObservationAdded` pairs with the same `(kind, trigger, entity)` and identical normalized `data` are re-paired regardless of test, and count as unchanged. Re-paired items are listed in `report.moved[]` so the move is visible but doesn't fail the run.

## 3. Normalization (both runs, before diffing)

Applied to `data` generically, with no per-kind code except the identity hints:

| Normalizer | Matches | Becomes |
|---|---|---|
| GUID | D and N formats, standalone or embedded (`wf-x-<32hex>`) | `<guid:k>`, numbered by first appearance in the run, so a GUID that recurs across observations stays linked |
| Timestamp | ISO-8601 with 0–7 fractional digits, optional `Z`/offset; includes `0001-01-01T00:00:00` | `<timestamp>` |
| W3C trace id | `00-<32hex>-<16hex>-<2hex>` | `<traceid>` |
| Headers | config `ignore.headers`; default adds `Content-Length` when a body was captured | removed |
| JSON paths | config `ignore.jsonPaths` + self-noise rules | removed |
| DB columns | config `ignore.dbColumns` (`Entity.Column` or `Column`) | removed |

**Identity values (the order-dependence problem from Phase 0).** Instead of rewriting ints, the diff treats two integers as equal when they're the same *identity ordinal* in their runs. Each run builds a map `(entity, keyValue) -> ordinal` from its `Added` db-writes, in seq order. Then `Workflow#3` in base and `Workflow#3` in current compare equal even if the raw ids are 7 and 8. This applies to:
- db-write key columns, and FK columns. This needs one small capture addition: `data.foreignKeys: {"TaxonomyId": "Taxonomy"}`, read from EF metadata.
- JSON fields in other kinds whose name matches `^id$|Id$`. Configurable via `identity.fieldPattern`.
- route-parameter segments of `request.path` (`/workflows/5` with route `/workflows/{id}`).

For response fields, which entity an `id` refers to isn't known, so it compares equal if *some* entity has the same ordinal for both values. A unit-tested escape hatch: `identity: { enabled: false }`.

## 4. Self-noise check

When `selfNoiseCheck: true`, the base tree is run twice. Every difference between those two runs, after normalization, becomes a `NoiseRule` (`kind + trigger + path`, origin `self-noise`). It's dropped from the real diff and listed in `report.noise`.

## 5. Intent sorting (v1, deterministic)

`--expect "POST /workflows"` matches a difference whose `trigger` matches. `--expect "entity:Workflow"` matches a db-write difference whose `entity` matches. Either sets `Classification = Intended` with `Reason { Source: "expect", Rule: <the expect> }`. Accepted items (from `.behavediff/accepted.json`, keyed by difference `Id`) become `Accepted`. Everything else is `Unexpected`. `--intent` text is stored in `report.intent.text`.

## 6. Report JSON (schemaVersion 1)

```json
{
  "schemaVersion": 1,
  "run": {
    "base":    { "ref": "master", "commit": "8525094", "tests": 21, "passed": 21 },
    "current": { "ref": "working tree", "commit": "8525094-dirty", "tests": 21, "passed": 21 },
    "inputs": [{ "kind": "mstest", "project": "src/Tests/WhatInBox.Tests.Regression/WhatInBox.Tests.Regression.csproj" }],
    "startedAt": "2026-10-09T08:00:00Z",
    "durationMs": 184000
  },
  "intent": { "text": "Default new workflows to archived", "expect": ["entity:Workflow"] },
  "summary": {
    "unexpected": 1, "intended": 2, "accepted": 0,
    "observationsCompared": 38, "unchanged": 35,
    "noiseRules": 1, "latencyFlags": 0
  },
  "differences": [
    {
      "id": "d-3f9a1c2e",
      "kind": "ValueChanged",
      "classification": "Unexpected",
      "reason": { "source": "default", "rule": null, "note": null },
      "test": "WorkflowEndpointRegressionTests.GetWorkflow_EnabledModulesSerializesAsJsonArrayOfNames",
      "observationKind": "http-response",
      "trigger": "GET /workflows/{id}",
      "entity": null,
      "path": "$.body.workflow.enabledModules",
      "before": ["Index", "Verification"],
      "after": "Index,Verification",
      "match": { "test": "...", "kind": "http-response", "trigger": "GET /workflows/{id}", "entity": null, "occurrence": 0 }
    },
    {
      "id": "d-81be0d47",
      "kind": "ValueChanged",
      "classification": "Intended",
      "reason": { "source": "expect", "rule": "entity:Workflow", "note": null },
      "test": "WorkflowEndpointRegressionTests.CreateWorkflow_RoundTripsThroughDatabase",
      "observationKind": "db-write",
      "trigger": "POST /workflows",
      "entity": "Workflow",
      "path": "$.values.IsArchived",
      "before": false,
      "after": true,
      "match": { "test": "...", "kind": "db-write", "trigger": "POST /workflows", "entity": "Workflow", "occurrence": 0 }
    }
  ],
  "latency": [],
  "moved": [],
  "noise": [
    { "kind": "http-response", "trigger": "GET /sessions", "path": "$.body.sessions[*].dateCreated", "origin": "self-noise" }
  ],
  "errors": []
}
```

Rules:
- `differences` is a flat list, so it's easy for an agent to iterate. The terminal view groups by classification, then trigger.
- Array paths use concrete indices (`[0]`). Noise rules may use `[*]`.
- For `ObservationAdded` / `ObservationRemoved`: `path` is null, and `before` / `after` hold the whole `data` of the side that exists.
- `errors[]` entries: `{ "tree": "current", "stage": "build" | "test" | "capture", "message": "...", "detail": "<compiler output excerpt>" }`.
- Exit code: `errors` non-empty → 2. Else any `Unexpected` or `Intended` → 1. Else 0. Accepted-only → 0.

   **Open question:** should `Intended` alone exit 1? The spec says 1 means "differences found", so I've counted it. An agent would treat 1 + zero unexpected as "done, as intended".

## 7. Out of scope for this proposal

CLI flags beyond the spec, MCP tool shapes (Phase 2), caching, affected-test selection.
