# Phase 0 report: capture spike

**Result: capture injection works on WhatInBox with no change to its repo.**

Run: WhatInBox `8525094` on the WhatInBox dev machine, .NET SDK 10.0.401, runtime 10.0.12, `etc/spike.ps1`.
21 tests (the build prompt's "25" came from a stale copy), all passed, 38 observations, 0 hook errors.

| | |
|---|---|
| Mechanism | `DOTNET_STARTUP_HOOKS` + `BEHAVEDIFF_CAPTURE_DIR`. No MSBuild overlay fallback needed. |
| HTTP | ASP.NET Core hosting `HttpRequestIn.Start/Stop` events, body teed from `Response.Body`. Newtonsoft bodies parse as JSON; `text/plain` bodies kept as strings. Trigger uses the route template (`GET /workflows/{id}`). |
| DB writes | EF Core `SaveChangesStarting/Completed/Failed` events, entries from `ChangeTracker`. Works with `AddDbContextFactory<WibWorkspaceContext, CustomFactory>` on Testcontainers SQL Server. Identity keys filled in after save; Modified rows show original/current of changed columns only. |
| Test attribution | **TRX time windows**: `dotnet test --report-trx`, tests are sequential, so each observation's timestamp falls in exactly one test's `[startTime, endTime]`. 38/38 attributed. Stack scan for `[TestMethod]` frames found only 2/38 (agreeing with TRX); it is kept as a cross-check only. |

## Processes

MTP starts three processes that inherit the env vars: the `dotnet test` CLI (ignored by entry assembly name), the test-host controller, and the test host (has `--internal-testhostcontroller-pid`). Only the test host produces observations. Build runs separately without the env vars, so MSBuild never loads the hook.

## Sample

```json
{"seq":22,"trigger":"PUT /workflows/{id}","kind":"db-write","testByTrx":"UpdateWorkflow_ReplacesEnabledModulesCollection",
 "data":{"context":"WibWorkspaceContext","entity":"Workflow","state":"Modified","keys":["Id"],
         "values":{"Id":5,"EnabledModules":{"original":[0,5],"current":[1]}}}}
{"seq":23,"trigger":"PUT /workflows/{id}","kind":"http-response","testByTrx":"UpdateWorkflow_ReplacesEnabledModulesCollection",
 "data":{"request":{"method":"PUT","path":"/workflows/5","route":"/workflows/{id}"},"status":200,
         "headers":{"Content-Type":"application/json; charset=utf-8","Content-Length":"172"},
         "body":{"workflow":{"id":5,"name":"wf-update-modules-144760b994e3468081edee92642dc4fa","description":null,
                             "enabledModules":["ReScan"],"dateCreated":"2026-10-09T07:17:08.7112344"}}}}
```

## Surprises → requirements for Phase 1

1. **Fixture writes land on the first test.** The seeded `User` row (seq 1) comes from fixture setup, but its timestamp falls in the first test's TRX window, so it's attributed to `GetSessions_NoAuthHeader_Returns401`. That's deterministic, so diffs stay correct, but the label is misleading. Fix in capture: extend the stack scan to recognize `[AssemblyInitialize]`, `[ClassInitialize]` and `[TestInitialize]` frames and label those observations as fixture setup.
2. **Async stacks are shallow.** After an `await` resumes, only the innermost state machine is on the stack. This confirms the stack scan can't be the primary attribution method.
3. **One database is shared by the whole run, so identity values depend on test order.** `id`, `collectionId`, `/workflows/5` in paths, and `Id` in writes all come from identity columns. If the branch adds or removes one insert, every later id shifts. Normalization must map identity values to stable placeholders, per entity, in order of first appearance. A plain "ignore key columns" rule isn't enough, because the same ids appear in response bodies and in request paths.
4. **Timestamp formats vary.** The same value appears as `...08.7112344Z` (just created), `...08.7112344` (read back from SQL, no `Z`), `...08.07` (`datetime` column precision), and `0001-01-01T00:00:00`. The timestamp normalizer has to accept a variable number of fractional digits and an optional offset.
5. **W3C trace ids in error bodies.** `traceId: "00-<32hex>-<16hex>-00"` appears in `problem+json` responses. The GUID-N regex would only catch the 32-hex part, so trace ids need a default normalizer of their own.
6. **`Content-Length` is noisy.** It follows the body length, which changes when serialization trims trailing zeros from timestamp fractions. When the body is captured, ignore it by default.
7. **No `Date` / `Request-Context` headers.** TestServer doesn't add them. The spec's default header ignore list is harmless but has nothing to act on here.
8. **Enums are recorded as numbers.** `EnabledModules` appears as `[0,5]` in DB writes but as names in responses. Capture should write enum names. Separately: we record the CLR value, not the value the provider writes to the column. A change that only affects a value converter would be invisible. Phase 1 decision: record CLR values, provider values, or both.
9. **Latency is noisy.** The first request took 424 ms and later ones 2 to 60 ms. This confirms latency should stay out of pass/fail.
