---
name: smartcon-logging
description: SmartCon structured logging conventions. Use when reading or writing smartcon.log / formula-diagnostic.log, when adding log calls, when analyzing cross-method correlation via OpId, when debugging scope chain issues, when migrating code from [Cat] prefix to BeginScope, or when the user references Phase 1/2 logging migration. Covers log file locations, scope nesting, OpId correlation, IsEnabled checks, and known false-positives in grep audits.
---

# SmartCon Logging Conventions

SmartCon uses an in-house `SmartConLogger` (NOT `Microsoft.Extensions.Logging`) with structured scope-based logging. Every log line written inside a `BeginScope` block carries an `[OpId=…]` correlation prefix that flows through `await` and `Task.Run` via `AsyncLocal`.

## When to use this skill

Activate this skill when:
- Reading/analyzing `smartcon.log` or `formula-diagnostic.log` in `C:\Users\<user>\AppData\Roaming\AGK\SmartCon\`
- Adding new `SmartConLogger.Info/Debug/Warn/Error` calls
- Adding `SmartConLogger.BeginScope` to track an operation's lifecycle
- Migrating legacy `$"[Cat] message"` prefixes to scope-based logging
- Investigating why an `[OpId=…]` prefix is missing on a log line
- Auditing grep for `[Cat]` prefixes after a batch refactor

## Critical constraints (NEVER do these)

1. **NEVER** use `Microsoft.Extensions.Logging.ILogger` in production code. It conflicts with the version Revit ships (Autodesk forum 9270358). `ISmartConLogger` interface exists in `SmartCon.Core` for future DI, but the public API is the static `SmartConLogger` class.
2. **NEVER** use `[LoggerMessage]` source-gen attributes. They require `this ILogger` extension methods, incompatible with the static facade.
3. **NEVER** add `new [Category] ` prefix to message strings. Use `BeginScope("Category", ("Method", "MethodName"))` instead.
4. **NEVER** store the `LogScope` itself between transactions — only `ElementId` (invariant I-05). The scope lives in `LogScopeProvider` via `AsyncLocal`, and is orthogonal to element lifetime.
5. **NEVER** call `BeginScope` and `Measure` in the same method — both create their own `OpId` and you get `[OpId=A Op=…] [OpId=B Op=…]` double scopes. Pick one.
6. **NEVER** `dotnet restore` without `-p:Configuration=Debug.Rxx` — see `smartcon-build-guide` skill.
7. **NEVER** put raw `SmartConLogger.Debug($"…")` inside a `for`/`foreach` body that runs more than ~100 iterations. Use `HotLoopCounter` (see `references/counter-pattern.md`).

## Log files

| File | Path | Purpose | Rotation |
|---|---|---|---|
| Main event log | `%APPDATA%\AGK\SmartCon\smartcon.log` | `Info`/`Warn`/`Error` (+ `Debug` in Debug build) | 5 MB / 3 .bak generations |
| Formula diagnostic | `%APPDATA%\AGK\SmartCon\formula-diagnostic.log` | Every Revit formula the engine sees | Append-only (no rotation) |
| Formula-diagnostic is written via `SmartConLogger.Formula/FormulaOk/FormulaFail` | — separate from main writer | — |

To find the user's log: `$env:APPDATA\AGK\SmartCon\smartcon.log` (typically `C:\Users\<user>\AppData\Roaming\AGK\SmartCon\smartcon.log`).

## Public API (smartcon.Core.Logging)

```csharp
namespace SmartCon.Core.Logging;

public static class SmartConLogger
{
    public static void Info(string message);
    public static void Debug(string message);
    public static void Warn(string message);
    public static void Error(string message);
    public static void DebugSection(string title);
    public static void DebugLines(string header, string[] lines, int maxLines = 20);

    // Scope management — flow through AsyncLocal across awaits
    public static IDisposable BeginScope(string operation, params (string Key, object? Value)[] properties);
    public static IDisposable Measure(string operation);  // writes === END elapsed=…ms ===

    // Special
    public static void LogSessionStart(string commandName);
    public static void Formula(string message);
    public static void FormulaOk(string operation, string formula, string detail);
    public static void FormulaFail(string operation, string formula, string reason);
}

public interface ISmartConLogger { /* Info/Debug/Warn/Error only — scope stays static */ }
public sealed class SmartConLoggerAdapter : ISmartConLogger { /* static facade delegate */ }
```

`Info` returns `void` — there's no `IsEnabled` fast path on the public API. The internal writer checks `MinLevel` and drops the message before acquiring the lock. Use the existing `Debug`/`Info` split instead of manual `IsEnabled` guards.

## Log line format

```
{yyyy-MM-dd HH:mm:ss.fff}  [{LEVEL}]  {prefix}{message}
```

`{prefix}` is the scope chain — every active scope joined by a single space, rendered by `LogScope.FormatPrefix()`:

```
[OpId=84bc3087 Op=TxGroup Method=RunInTransaction TxName=PipeConnect]
```

`FormatPrefix` renders, in this order:
1. `OpId=<8 hex>` (always)
2. `Op=<operation>` (always)
3. Each property: `Key=Value` (omitted if `Key=="Op"` AND `Value==Operation` — D1 fix prevents the `Op=X Op=X` duplicate)

Scope nesting shows as a space-separated chain:

```
[OpId=02b47de7 Op=FMImport Method=ImportActiveFileAsync] [OpId=deb14b6a Op=FMImport Method=ProcessFamilyImportAsync] [OpId=77bb09b3 Op=LocalImport Method=ImportBatchAsync Count=1]
```

To correlate: pick an `OpId=` from the outermost scope and `grep "OpId=84bc3087"` — every line that belongs to that operation will be in the result.

## Adding a new BeginScope

```csharp
using var _scope = SmartConLogger.BeginScope("CategoryName",
    ("Method", nameof(MyMethod)),
    ("ElementId", elementId.IntegerValue),       // or .Value on REVIT2024_OR_GREATER
    ("Count", list.Count));
```

**Category names are short and reused across the project.** Pick from the existing vocabulary:

| Category | Use for |
|---|---|
| `PipeConnect` (or `Chain+`, `Chain-`) | PipeConnect BFS chain operations |
| `ChainOperation` | ChainOperationHandler methods (IncrementLevel, DecrementLevel) |
| `TxGroup` | TransactionGroup lifecycle (Start/Commit/Rollback) |
| `S4` | RevitLookupTableService / RevitParameterResolver parameter resolution |
| `LookupSvc` | Lookup-table lookups |
| `DynSizeResolver` | Dynamic size enumeration |
| `Resolver` | Parameter dependency resolution |
| `FittingRepo` | FittingFamilyRepository |
| `FitAlign` | Fitting alignment |
| `SizeFitting` / `SizeHandler` | Fitting sizing |
| `Validate` | Connect validation branches |
| `Connect` / `Editor` | Connect call (validation+connect) |
| `Rotate` | Rotation |
| `Init` | Init handler (Disconnect/Align) |
| `CTC` | ConnectorTypeCode writers |
| `FileInfoReader` | Revit version reading |
| `FileResolver` | File path resolution |
| `Sidecar` | .txt sidecar (Type Catalog) |
| `TypeCatalog` | Type Catalog import |
| `LocalImport` | Local family import service |
| `SystemImport` | System family import orchestrator |
| `FMEdit` / `FMImport` / `FMVM` | FamilyManager VM (Edit/Import/Dispose) |
| `FMTree` | Tree view operations |
| `FMLoadable` | Loadable family attribute extraction |
| `FMRename` | Family rename |
| `FMProperties` | Family properties dialog |
| `AwaitableEvent` | IFamilyManagerAwaitableEvent |
| `ActiveClassifier` | Active document classification |
| `ActivePrep` / `ActiveCleanup` | Active family file preparer / cleanup |
| `Placement` | Family placement |
| `Awaitable` | Awaitable event handler |
| `Diag` | PipeConnectDiagnostics |
| `EditorCycle` / `EditorInsert` / `EditorConnect` / `EditorChain` | PipeConnectEditorViewModel sub-files |
| `DropHandler` | FamilyPlacementDropHandler |
| `ShareProject` / `ShareSettings` | ShareProjectCommand / ShareSettingsCommand |
| `ActiveDatabase` | LocalCatalogDatabase operations |
| `Count` / `FilePath` / `CatalogItemId` / `FittingId` / `InstanceId` / `DynId` / `ConnectorIndex` / `Topology` / `Level` | Common property names |

**Properties should be queryable in log aggregators.** Use `int`/`long`/`string`/enums — avoid `object`/`dynamic`.

## `Measure` vs `BeginScope` — when to use which

- `BeginScope(category, props)` — emit START line + every child log line gets the prefix. Use when the operation's *identity* matters.
- `Measure(operation)` — emit only END line with `elapsed=…ms`. Use when you only need timing.

If you need both, prefer `BeginScope` (it carries identity for grep correlation; timing is a side benefit).

## What `LogSessionStart` is for

`LogSessionStart("FamilyImport")` writes a visually distinct `=== / SESSION START: FamilyImport ===` block — a human-readable marker in the log. Use it at the start of a top-level user action (a command, a drag-drop, a button click). Don't use it for sub-operations.

## Reading logs efficiently

```bash
# Tail the last 1000 lines (common starting point)
$content = Get-Content "$env:APPDATA\AGK\SmartCon\smartcon.log" -Tail 1000

# Correlate one operation across threads/awaits
$content | Select-String "OpId=84bc3087"

# Find all errors
$content | Select-String "\[ERR\]"

# Audit for missed scope coverage (orphan lines = no [OpId=])
$content | Where-Object { $_ -notmatch '\[OpId=' }
```

## Known grep false-positives

When auditing for `[Cat]`-prefixed log calls, these are **valid** (not categories):

- `SmartConLogger.Info($"[{attemptName}] {msg}")` — `attemptName` is a method parameter (string "Attempt1" / "Attempt2"). The same call site should add `("Attempt", attemptName)` to the `BeginScope` properties instead.
- `SmartConLogger.DebugSection($"[DIAG {label}]")` — `label` is a parameter (e.g. "ДО ConnectTo" / "ПОСЛЕ ConnectTo"). It's a section header, not a category.
- `SmartConLogger.Debug($"[Restore SystemClassification] …")` — two-word phrase, not a category.
- `SmartConLogger.Formula($"[ParseSizeLookup] '{formula}' …")` — this writes to `formula-diagnostic.log`, not `smartcon.log`. Different system, ignore.

## AsyncLocal scope flow

`LogScopeProvider` uses `AsyncLocal<ScopeNode?>`. The scope flows:
- Through `await` (the compiler copies `ExecutionContext` automatically)
- Through `Task.Run` (the `ExecutionContext` is captured before the lambda runs)
- Through `IExternalEventHandler.Execute` callback (the lambda runs on Revit UI thread, scope flows via `AsyncLocal`)

**Known caveat in Revit:** if you capture `Action` from inside a scope and pass it to `_awaitableEvent.RaiseAsync`, the `AsyncLocal` flow is correct, but if you store a logger call **outside** the callback and the callback runs on a different `ExecutionContext` (e.g. via a manually-created `Task` with `TaskCreationOptions.LongRunning`), the scope can drop. Always log from inside the callback or `Task.Run` lambda.

## Lint rules (what to check on every PR that touches `SmartConLogger.*`)

```powershell
# Should be 0 (false-positives only)
rg 'SmartConLogger\.\w+\(\s*\$"\[' --type cs src/

# Should be 0 (D1 fix)
rg 'BeginScope\("([^"]+)",\s*\("Op",\s*"([^"]+)"\)' --type cs src/ -r '$1' | %{ if ($_ -match …) { 'DUP' } }

# Should be 0 in production (tests are OK)
rg 'GetAwaiter\(\)\.GetResult\(\)' --type cs src/ | %{ $_ -notmatch 'Tests\\' }

# Should be near-zero (justified exceptions)
rg 'async void' --type cs src/ | %{ $_ -notmatch 'Tests\\' }
```

## How to add logging to a new method

The first 80% of logging tasks reduce to one of these three patterns. See `references/logging-cookbook.md` for the full recipes and `references/recent-patterns.md` for L8 / L9 / C1 / C5 / C15 conventions (basename in scope, `[Action: …]` in WARN, anti-pattern of long-lived scope, etc.).

**TL;DR:**
- **Public service method** → one `BeginScope` at the top, `("Method", nameof(…))` + key inputs as properties, short `Info` messages.
- **Top-level user action** (`IExternalCommand`, button, drag-drop) → `LogSessionStart` + `LogSessionEnd` in `try/finally`, with a `BeginScope` between.
- **Hot loop (≥ 100 iter)** → `HotLoopCounter` (see `references/counter-pattern.md`).

**Three rules to never break:**

1. **Path in scope → `Path.GetFileName()`, not full path.** Full paths in scope + repeated in messages was the #1 source of log spam (L8).
2. **`Warn` message → end with `[Action: …]` suggestion.** Operators reading the log need to know what to do, not just what failed (L9).
3. **No `BeginScope` around long-running methods.** A scope that lives 30+ seconds and emits 5 000 inner events generates 2 MB of log. Let inner work open its own scope (C15).

## More info

- `references/scope-api.md` — detailed scope API with edge cases
- `references/known-issues.md` — issues found during Phase 1 audit (D1, D2, ActiveClassifier, etc.)
- `references/counter-pattern.md` — `HotLoopCounter` for hot-loop logging (allocation pressure)
- `references/logging-cookbook.md` — **NEW** — step-by-step recipes for 5 common scenarios (new method, command, hot loop, ExternalEvent, refactor of legacy)
- `references/recent-patterns.md` — **NEW** — L8 / L9 / C1 / C5 / C15 conventions with rationale and audit findings
- `docs/adr/026-logging-migration.md` — architectural decision record
- `docs/logging/final-validation-report.md` — Phase 1+2 final metrics
- `docs/logging/migration-inventory.md` — 82 files inventory before migration
