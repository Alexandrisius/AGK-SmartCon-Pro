# Recent Logging Patterns — Phase L8 / L9 / C1 / C5 / C15

Conventions added during the most recent log cleanup (commits
`bbd4b93`…`e437f35`). Apply them when adding **new** logging or
refactoring **old** code. Each section includes: rule, code example,
audit finding that motivated the rule, and how to recognize regressions.

## L8: `FilePath` / `FileName` scope property uses **basename**, not full path

**Rule.** If you put a path in a scope property, use `Path.GetFileName()`.
Drop the path from message strings — the scope already has it.

### Code

```csharp
// GOOD — basename in scope, no path in message
using var _scope = SmartConLogger.BeginScope("FamilyLoad",
    ("FilePath", Path.GetFileName(normalizedPath)));
SmartConLogger.Info("Attempting to load family");

// BAD — full path in scope, duplicated in message
using var _scope = SmartConLogger.BeginScope("FamilyLoad",
    ("FilePath", normalizedPath));
SmartConLogger.Info($"Attempting to load family from: {normalizedPath}");
```

### Audit finding

In a 2 MB / 9 439-line smoke-test log, every `Info` line from
`RevitFamilyLoadService.LoadFamilyAsync` carried a 100-char
`FilePath=C:\Users\klim9\…\ADSK_…rfa` in the scope prefix. The
`LoadFamilyAsync` method itself logged 12 separate `Info` lines
about the same path, each one re-rendering the same 100 chars. The
scope prefix already had the basename available — we were just
not using it.

### How to recognize regression

```bash
# Should be 0 in production (false-positives OK for the property key itself)
rg 'FilePath=\$|FileName=\$' --type cs src/

# Long FilePath in scope means full path is leaking through
# (this regex won't fail; manual grep on the log is the real check)
```

Manual log check: `grep "FilePath=.\{60,\}" smartcon.log` — should be
empty after the refactor.

### Applied to

- `RevitFileInfoReader.cs` (3 sites)
- `LocalFamilyFileResolver.cs` (6 sites + 1 missing `BeginScope` added)
- `LoadableFamilyTypeResolver.cs` (5 sites, `RfaFilePath` → `RfaFileName`)
- `RevitFamilyLoadService.cs` (16 sites)

Commit: `bbd4b93` + `b8c3e2b`.

---

## L9: `Warn` always carries an `[Action: …]` suggestion

**Rule.** Every `Warn` message ends with `[Action: …]` — a short,
concrete, actionable suggestion. Operators reading the log need to
know what to do, not just what failed.

### Code

```csharp
// GOOD
SmartConLogger.Warn(
    $"Failed to resolve types: {ex.Message} " +
    "[Action: Check Revit journal for detailed error, or restart Revit if COM object is corrupted]");

// BAD
SmartConLogger.Warn($"Failed to resolve types: {ex.Message}");
```

### Action patterns

| Warning pattern | Action suggestion |
|---|---|
| `LoadFamily InternalException` | "Restart Revit and retry, or check ExternalApplication registration" |
| `No types for 'X.rfa'` | "Check .rfa has FamilyManager.Types parameter, or update Family Editor" |
| `ResolveForLoadAsync failed` | "Verify file exists and Revit version matches catalog targetRevitVersion" |
| `Close failed for 'X.rfa'` (Marshal.ReleaseComObject) | "Safe to ignore — Revit will release the document on its own" |
| `Failed to persist types` | "Check database write permissions and SQLite file integrity" |
| `OpenDocumentFile did not return family doc` | "Verify file is a valid Revit .rfa, or check Revit version compatibility" |

### How to recognize regression

```bash
# Find Warn calls without [Action:
rg 'SmartConLogger\.Warn\(' --type cs src/ | grep -v 'Action:'
```

(That's a manual check; ripgrep doesn't parse C# strings well.)

### Applied to

- `LoadableFamilyImportOrchestrator.cs` (3 sites)
- `LoadableFamilyTypeResolver.cs` (3 sites)

Commit: `e138b9f`.

---

## C1: `Measure` returns a `MeasureScope` with `GetElapsedMilliseconds()`

**Rule.** `SmartConLogger.Measure(operation)` returns a `MeasureScope`
that exposes `GetElapsedMilliseconds()`. Use when you need timing
data mid-scope (e.g. log partial progress with elapsed so far).

### Code

```csharp
using var ms = SmartConLogger.Measure("MyOperation");
// … do work, possibly log from inside …
var elapsedMs = ms.GetElapsedMilliseconds();  // read before Dispose
SmartConLogger.Info($"partial: {elapsedMs:F1}ms");
```

### When to use

- Want timing data visible in the log (footer line `=== END elapsed=…ms ===`)
- Don't need correlation across `await` chains (use `BeginScope` instead)

### Backward compatibility

Existing call sites that use `using var _ = SmartConLogger.Measure(…)`
still compile — `MeasureScope` is `IDisposable`. The change is
additive; no migrations required.

Commit: `c084e58`.

---

## C5: `AsyncBridge.RunSync` for sync-over-async on the UI thread

**Rule.** Never `.GetAwaiter().GetResult()` inside an
`IExternalEventHandler.Execute` callback, WPF command handler, or
any other UI-thread method. Use `AsyncBridge.RunSync` from
`SmartCon.Core.Threading` instead.

### Code

```csharp
using SmartCon.Core.Threading;

public SyncResult DoSyncWork(UIApplication uiapp)
{
    // GOOD — runs the task on a ThreadPool thread, no UI deadlock
    SyncResult result = AsyncBridge.RunSync(() => SomeService.LoadAsync(path));

    // BAD — can deadlock on UI thread
    var result = SomeService.LoadAsync(path).GetAwaiter().GetResult();

    return result;
}
```

### Why

`Task.GetAwaiter().GetResult()` blocks the current thread until the
task completes. On the UI thread, if the inner task tries to resume
on the UI sync context (e.g. via `await` without `ConfigureAwait(false)`),
the UI thread is blocked waiting for itself — deadlock.

`AsyncBridge.RunSync` wraps the call in `Task.Run(() => factory().GetAwaiter().GetResult())`,
which runs the inner work on a ThreadPool thread and the wait on
the calling thread. The Task.Run captures the current
`SynchronizationContext`, so the inner work resumes on it.

### Full API

```csharp
namespace SmartCon.Core.Threading;

public static class AsyncBridge
{
    public static T RunSync<T>(Func<Task<T>> taskFactory);
    public static void RunSync(Func<Task> taskFactory);
}
```

### Applied to

8 production call sites in: `App.cs` (×2), `AboutViewModel`,
`FamilyPlacementDropHandler` (×3), `RevitFamilyPlacementService`,
`SystemFamilyPlacementService`. Plus 5 unit tests.

Commit: `64e8659`.

---

## C15: Anti-pattern — `BeginScope` around long-running methods

**Rule.** Do NOT wrap methods that live for seconds with `BeginScope`.
Every inner log line carries the outer scope's prefix; a 34-second
method with 5 000 inner events generates a 2 MB log.

### Code

```csharp
// BAD — scope lives 34 seconds, all 5000 inner lines carry
//   [OpId=… Op=Editor Method=LoadDynamicSizes]
using var _scope = SmartConLogger.BeginScope("Editor", ("Method", "LoadDynamicSizes"));
var result = _sizeLoader.LoadInitialSizes(_doc, _ctx.DynamicConnector);  // 5000 lines inside
// … 7803 log lines later, scope closes …

// GOOD — the inner _sizeLoader.LoadInitialSizes already opens its own scope
// with DynId+ConnIdx. No outer scope needed.
var result = _sizeLoader.LoadInitialSizes(_doc, _ctx.DynamicConnector);
```

### Rule of thumb

If a method contains heavy inner work (database query, parameter
resolution on hundreds of elements, BFS traversal), let the inner
work open its own scope. The outer method doesn't need one.

**Exception:** if the outer method is itself the operation identity
(a Command, a top-level Init, a session entry), keep the scope —
the caller's caller benefits from correlation.

### How to recognize

Smoke-test the change. If a single outer `OpId=` appears in
> 5 000 log lines, the outer scope is too long-lived. Either:
1. Remove the outer `BeginScope` (the inner method has its own)
2. Split the outer method into smaller chunks, each with its own scope
3. Use `Measure` instead — it only emits the END footer, no per-line prefix

### Audit finding

PipeConnect smoke test produced 9 439 lines / 2 MB. One outer scope
(`OpId=55e26d07 Op=Editor Method=LoadDynamicSizes`) was the parent
of 7 803 log lines (82% of the log). Inner scope was
`OpId=6f9f0f9a Op=DynamicSizeLoader Method=LoadInitialSizes
DynId=1604476 ConnIdx=2`, which itself opened inner scopes for
`Op=FPA Method=AnalyzeConnectorRadiusParam` (28 calls) and
`Op=MapRows` (3 calls × thousands of iterations).

### Applied to

- `PipeConnectEditorViewModel.LoadDynamicSizes` — `BeginScope` removed (commit `e437f35`)
- The inner `DynamicSizeLoader.LoadInitialSizes` keeps its own scope — it's the right level of granularity.

---

## `LogSessionEnd` (added B2)

**Rule.** `LogSessionStart("X")` writes a visual `SESSION START`
banner. Pair it with `LogSessionEnd("X", startedAt)` to emit a
matching `SESSION END` banner with elapsed duration. Use in
`try/finally` so the banner is emitted even on failure.

### Code

```csharp
var startedAt = DateTime.Now;
SmartConLogger.LogSessionStart("FamilyImport");
try
{
    // … work, possibly with sub-scopes …
}
finally
{
    SmartConLogger.LogSessionEnd("FamilyImport", startedAt);
}
```

### Visual output

```
=========================================================================
SESSION START: FamilyImport  [2026-06-09 23:24:24]
=========================================================================
…
=========================================================================
SESSION END:   FamilyImport  [2026-06-09 23:26:52]  duration=147.598s
=========================================================================
```

Commit: `7d50d63` (B2 audit fix).

---

## See also

- `SKILL.md` — top-level entry point
- `logging-cookbook.md` — step-by-step recipes for new methods, commands, hot loops, ExternalEvent callbacks, and refactors
- `scope-api.md` — `BeginScope` / `Measure` / `AsyncLocal` edge cases
- `known-issues.md` — D1…D5 lessons from Phase 1 audit
- `counter-pattern.md` — `HotLoopCounter` for hot loops
