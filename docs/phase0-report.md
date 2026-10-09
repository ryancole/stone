# Phase 0 report: capture spike

**Result: capture injection works on a real production app with no change to its repo.**

Verified two ways:
- **Local stand-in** (`src/Samples`): same shape as the reference app. `Startup` class, Newtonsoft MVC, `AddDbContextFactory` with a custom factory, MSTest 4 on MTP, `WebApplicationFactory<Startup>`. Uses SQLite.
- **A reference app**, a private production API at the user's company, on its dev machine: Testcontainers SQL Server, .NET SDK 10.0.401, runtime 10.0.12. 21 tests, all passed, 38 observations, 0 hook errors, every observation attributed. Its captured data stays off this public repo.

| | |
|---|---|
| Mechanism | `DOTNET_STARTUP_HOOKS` + `BEHAVEDIFF_CAPTURE_DIR`. No MSBuild overlay fallback needed. |
| HTTP | ASP.NET Core hosting `HttpRequestIn.Start/Stop` events, body teed from `Response.Body`. Newtonsoft and System.Text.Json bodies parse as JSON; `text/plain` bodies are kept as strings. Trigger uses the route template (`GET /widgets/{id:int}`). |
| DB writes | EF Core `SaveChangesStarting/Completed/Failed` events, entries from `ChangeTracker`. Works with `AddDbContextFactory<TContext, CustomFactory>` on SQLite and SQL Server. Identity keys are filled in after save. Modified rows show original/current of changed columns only. Enums are written as names. `providerValues` holds the stored form of columns whose value converter changes it. |
| Test attribution | **TRX time windows**: `dotnet test --report-trx`. Tests are sequential, so each observation's timestamp falls in exactly one test's `[startTime, endTime]`. Every observation was attributed on both apps. In-process cross-check: MSTest 4's `TestContext.Current` for code running on the test's own async flow (see 1). |

## Processes

MTP starts three processes that inherit the env vars:
- the `dotnet test` CLI, ignored by its entry assembly name
- the test-host controller
- the test host, which has `--internal-testhostcontroller-pid`

Only the test host produces observations. The build runs separately without the env vars, so MSBuild never loads the hook.

## Sample (stand-in)

```json
{"seq":1,"test":"(setup)","trigger":"(test code)","kind":"db-write","attribution":"mstest-context",
 "data":{"context":"CatalogContext","entity":"Widget","state":"Added","keys":["Id"],
         "values":{"Id":1,"Aliases":["seed","vip"],"Name":"fixture-seed","Priority":"High", "...": "..."},
         "providerValues":{"Aliases":"seed;vip"}}}
{"seq":9,"trigger":"PUT /widgets/{id:int}/name","kind":"db-write","testByTrx":"RenameWidget_ChangesName",
 "data":{"entity":"Widget","state":"Modified","keys":["Id"],
         "values":{"Id":3,"Name":{"original":"wd-rename-ded7…","current":"wd-renamed-dd96…"}}}}
{"seq":3,"trigger":"POST /widgets","kind":"http-response","testByTrx":"CreateWidget_ReturnsCreated",
 "data":{"request":{"method":"POST","path":"/widgets","route":"/widgets"},"status":201,
         "headers":{"Location":"http://localhost/widgets/2","Content-Type":"application/json; charset=utf-8","Content-Length":"202"},
         "body":{"id":2,"name":"wd-create-5335…","tags":["red"],"createdUtc":"2026-10-09T07:10:30.2737142Z", "...": "..."}}}
```

## Findings → requirements for Phase 1

1. **Fixture work and test attribution.** In-process, `TestContext.Current` (an `AsyncLocal`) holds the running test for code on MSTest's async flow, including after `await`s, and is null during `AssemblyInitialize` / `ClassInitialize`. A direct write with no current test is labeled `(setup)`. Two other approaches were tried and rejected:
   - **TRX `duration`:** a test's window can include setup before the body *and* cleanup after it (`AssemblyCleanup` runs inside the last test's window).
   - **MSTest activities:** MSTest 4.5 emits no per-test `Activity`.

   The reference app's fixture is a **lazy singleton**: its first use, inside whichever test runs first, starts the container and seeds a user. That work is correctly attributed to that test. If a branch changes which test runs first, it moves, so Phase 1 re-pairs identical leftover observations across tests (phase1-proposal §2). HTTP calls made *from* fixture code always land on the first test, because `TestContext.Current` doesn't reach TestServer's request threads.
2. **Async stacks are shallow.** After an `await` resumes, only the innermost state machine is on the stack, so the stack scan is only a last-resort fallback.
3. **One database is shared by the whole run, so identity values depend on test order.** Ids in response bodies, in request paths (`/widgets/5`), and in write keys and FKs all come from identity columns. If the branch adds or removes one insert, every later id shifts. A plain "ignore key columns" rule isn't enough, because the same ids appear across observation kinds.
4. **Timestamp formats vary.** The same value appears as `…08.7112344Z` (just created), `…08.7112344` (read back from SQL, no `Z`), `…08.07` (`datetime` column precision), and `0001-01-01T00:00:00`. The normalizer has to accept 0–7 fractional digits and an optional offset.
5. **W3C trace ids in error bodies.** `traceId: "00-<32hex>-<16hex>-00"` appears in `problem+json` responses. The GUID-N regex would only catch the 32-hex part.
6. **`Content-Length` is noisy.** It follows the body length, which changes when serialization trims trailing zeros from timestamp fractions. Ignore it by default when the body is captured.
7. **No `Date` / `Request-Context` headers under TestServer.** The default header ignore list is harmless but has nothing to act on here.
8. **Stored form of converted columns.** The reference app stores an enum-list column as a JSON array of numbers while its API exposes names. Without `providerValues`, a change that only affects a value converter would be invisible.
9. **Latency is noisy.** The first request took ~420 ms and later ones 2–60 ms, which confirms latency should stay out of pass/fail.
