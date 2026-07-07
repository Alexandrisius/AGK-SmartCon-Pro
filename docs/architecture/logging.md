# Logging

`SmartCon` ships its own static logger (`SmartCon.Core.Logging.SmartConLogger`)
to keep the project free of a third-party logging dependency. This document
captures the rules every contributor must follow and the knobs available to
operators in the field.

## Log files

The logger writes to **two** files under `%AppData%\AGK\SmartCon\`:

| File | Contents | Rotation |
|------|----------|----------|
| `smartcon.log` | main event log: `Info`, `Debug` (when enabled), `Warn`, `Error` | **5 MB, 3 `.bak` generations** |
| `formula-diagnostic.log` | append-only trace of every Revit formula the engine sees (resolved, unresolved, failed) | **never** — this file is intentionally cumulative so we can collect statistics on which formulas the PipeConnect module encounters and how each one was resolved |

Both files are decorated with the active `LogScope` chain (see [Scope and correlation](#scope-and-correlation)).

`lookup-diagnostic.log` and `freeze-diagnostic.log` were removed in
Phase 0 (2026-06). Their content now goes to `smartcon.log` with
`[Lookup]` / `[Freeze]` tags so the same information is preserved in
a single, grep-able place.

## Severity levels

```csharp
public enum LogLevel
{
    Debug,   // verbose, disabled in Release builds
    Info,    // general informational events
    Warn,    // non-critical issues
    Error,   // critical errors (always written)
}
```

The numeric ordering is deliberate: `Debug < Info < Warn < Error`. A
caller requests the minimum level it cares about and the logger drops
everything below it before the file lock is even acquired, so raising
the threshold is essentially free at runtime.

## What gets written in each build configuration

| Build configuration | `MinLevel` default | What the user sees |
|---------------------|--------------------|---------------------|
| `Debug.*` (R19-R26) | `Debug` | everything: trace, info, warn, error |
| `Release.*` (R19-R26) | `Info` | info, warn, error (no per-row trace) |

The decision happens at type initialisation via the C# `DEBUG` preprocessor
symbol, which the compiler defines for every `Debug.*` configuration in
`src/Directory.Build.props` and leaves undefined for every `Release.*`
configuration. No host-side wiring is required: the level is correct the
moment the assembly loads inside Revit, regardless of how Revit was
started.

## Runtime override: `SMARTCON_LOG_LEVEL`

Field debugging often needs verbose tracing in a Release build, or wants
to silence a chatty Info channel in a Debug build. Set the
`SMARTCON_LOG_LEVEL` environment variable before launching Revit:

```powershell
# Turn on Debug in a Release build (full trace)
[Environment]::SetEnvironmentVariable("SMARTCON_LOG_LEVEL", "Debug", "User")

# Mute Info and below in a Debug build (errors only)
[Environment]::SetEnvironmentVariable("SMARTCON_LOG_LEVEL", "Warn", "User")
```

Accepted values: `Debug`, `Info`, `Warn`, `Error` (case-insensitive).
Anything that fails to parse falls back to the build default, so a typo
in the variable name never wedges the plugin into a silent state.

### Precedence

1. `SMARTCON_LOG_LEVEL` environment variable, if set and parseable
2. Build default (`Debug` for Debug.*, `Info` for Release.*)
3. Programmatic override via `SmartConLogger.MinLevel = LogLevel.X;` —
   call this before any logger method, e.g. from `IExternalApplication.OnStartup`,
   to force a level for the entire session. The backing field is
   `volatile` so changes are visible across threads without explicit
   memory barriers.

The third option is intentionally last so that one-off diagnostic lines
(`SmartConLogger.MinLevel = LogLevel.Debug;`) never leak into a Release
build by accident.

## Scope and correlation

Every call site may wrap a block of work in a `LogScope`:

```csharp
using var _ = SmartConLogger.BeginScope("ImportActiveFile",
    ("Source", filePath));

SmartConLogger.Info("loading family");   // prefixed with [OpId=… Op=… Source=…]
SmartConLogger.Info("placing symbol");   // same prefix
// scope.Dispose() at end of using → emits [OpId=… Op=…] === END elapsed=…ms ===
```

The scope is stored in `LogScopeProvider`, an ambient `AsyncLocal<ImmutableStack<LogScope>>`.
This means:

- **`await` is safe.** The scope flows through `await Task.Yield()`,
  `await Task.Run(...)`, and any other async hop because the value is
  copied along the `ExecutionContext`.
- **Thread-pool is safe.** Continuation on a different thread still sees
  the scope, because `AsyncLocal<T>` is the only mechanism that survives
  the `ExecutionContext` flowing across threads. `[ThreadStatic]` would
  not.
- **Nested scopes compose.** `BeginScope("Outer")` followed by
  `BeginScope("Inner")` renders both prefixes in the line, in root-first
  order.

The correlation `OpId` is 8 hex characters (`Guid.NewGuid().ToString("N")[..8]`).
To find every log line for a single operation:

```bash
grep a1b2c3d4 smartcon.log
```

## Contributor rules

1. **Pick the right level.** A new trace point for an unlikely code path
   is `Debug`. A user-visible state transition ("import started",
   "category changed") is `Info`. A recoverable wrong-but-not-fatal
   condition is `Warn`. Anything that needs a support ticket is `Error`.
2. **Do not log in tight loops without a counter.** A `Debug` line that
   fires 200 times during a batch import is acceptable, but if it would
   produce 10k+ lines it should be aggregated into a single summary
   after the loop. See `LocalFamilyImportService` for the pattern.
3. **Never log secrets or licence data.** A `cat.FamilyUniqueId` is
   fine, a `User.Identity.Name` or a license file path is not.
4. **Do not catch and swallow exceptions for the sake of a log line.**
   Either let them propagate to the `ExternalEvent` boundary or
   convert them into a `Warn`/`Error` line at the boundary itself.
5. **Format strings use `$"..."` interpolation**, not `string.Format`.
   The logger methods take a single `string` argument for a reason —
   the cost of building the string is paid even when the level is
   suppressed, so callers should pre-filter before formatting if the
   work is expensive.
6. **Prefer `BeginScope` over hand-written `[OpName]` prefixes.** Once
   a method's call site is wrapped in a scope, every nested `Info`/`Debug`
   automatically receives the correlation prefix — no need to manually
   write `[OpName] …` in every string.

## Analyzer severity policy

Several Microsoft built-in analyzers report violations in the codebase that
the team has **deliberately not fixed**. This section exists so future
contributors (and AI agents) do not waste time re-discovering why the
build is green despite 30+ `CA1863` hits and 4 `CA1305` hits.

| Rule | Pre-existing sites (commit `9c50226`) | Current severity | Reason for suppression |
|------|---------------------------------------|------------------|------------------------|
| `CA1863` Use `CompositeFormat` | ~30 in `PipeConnect` (no hot path) | `suggestion` in `.editorconfig` | Performance rule, "safe to suppress if performance isn't a concern" (Microsoft docs). Our format volume is <100 ops/click, user-triggered. dotnet/runtime itself keeps this at `suggestion`. |
| `CA1305` Specify `IFormatProvider` | 4 in `ExportNameDialogViewModel.RefreshPreview` (file-name previews) | `suggestion` in `.editorconfig` + `NoWarn` in `Directory.Build.props` | Globalization rule, only relevant for culture-sensitive numbers in UI strings. We log only file names / categories. dotnet/runtime does not promote this to `warning` either. |

### What the build actually does

`Directory.Build.props` sets:

- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
- `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`
- `<AnalysisLevel>latest-recommended</AnalysisLevel>`

These settings **do NOT escalate `suggestion` to `warning`** — only
`warning` is promoted to `error`. So `CA1863.severity = suggestion` and
`CA1305.severity = suggestion` keep the build green, and the diagnostics
still appear as grey "info" hints in the IDE (without breaking the build
on CI).

To prove the rules actually fire on our code, temporarily raise them to
`warning` in `.editorconfig` and rebuild — you will see the exact list
of suppressed sites. Do not commit that change.

### How to opt in to fixing these later

If a profiling session (e.g. BenchmarkDotNet) ever proves that
formatting is on a hot path (>1000 calls/sec in a loop):

1. Refactor the offending sites:
   ```csharp
   // Before
   SmartConLogger.Info($"[Cat] {a} + {b}");

   // After
   private static readonly CompositeFormat _fmt =
       CompositeFormat.Parse("[Cat] {A} + {B}");
   SmartConLogger.Info(_fmt.Format(CultureInfo.InvariantCulture, a, b));
   ```
2. Raise `CA1863.severity` from `suggestion` to `warning` in `.editorconfig`.
3. Document the change in `docs/adr/026-logging-migration.md` (Phase 0+
   rationale section) and add a link from this section.
4. Repeat for `CA1305` if a site is ever moved to log culture-sensitive
   numbers (e.g. file sizes, percentages).

For reference, this is the same approach Microsoft uses in
`dotnet/runtime` — see `eng/CodeAnalysis.src.globalconfig` in that repo.

## Migration cookbook (Phase 1)

Most of the codebase (1011 call-sites across 82 files at the time of
writing) still uses the pre-Phase-0 pattern: a hand-written `[Category]`
prefix in front of the message, formatted via `string` interpolation.
Phase 1 is the sweep that moves these call-sites over to
`BeginScope` so the category becomes a structured scope property
(`[OpId=… Op=…]`) instead of text glued onto the rendered line.

The target pattern, taken from the standard
[.NET structured-logging recipe](https://learn.microsoft.com/dotnet/core/extensions/logging/overview):

```csharp
// Before (legacy)
public void DoWork(string filePath)
{
    SmartConLogger.Info($"[Import] starting: {filePath}");
    SmartConLogger.Debug($"[Import] parsing {lineCount} lines");
    SmartConLogger.Info($"[Import] done: {errors} errors");
}

// After (Phase 1)
public void DoWork(string filePath)
{
    using var _ = SmartConLogger.BeginScope("Import", ("File", filePath));
    SmartConLogger.Info($"starting: {filePath}");
    SmartConLogger.Debug($"parsing {lineCount} lines");
    SmartConLogger.Info($"done: {errors} errors");
}
```

### Rules

1. **Always `using var _ = …;`.** `BeginScope` returns an
   `IDisposable`; without the `using` the scope is disposed
   immediately and the correlation prefix is lost. This is the
   number-one mistake teams make when adopting scopes
   (see Nicholas Blumhardt's post
   ["The semantics of ILogger.BeginScope()"](https://nblumhardt.com/2016/11/ilogger-beginscope/)).
2. **One scope per logical operation.** If a method touches three
   sub-systems, three nested scopes are fine — but the outer scope
   names the high-level operation ("ImportActiveFile"), and the
   inner scopes name the sub-steps ("Parse", "Validate", "Commit").
3. **Use the category that used to be in `[Category]`.** The
   scope `Op` is what the log aggregator will index on; renaming
   it breaks every existing grep and dashboard.
4. **Strip the `[Category]` prefix from the message.** Once the
   scope carries it, leaving the prefix in the message doubles the
   noise (`[OpId=… Op=Import] [Import] starting: …`). Run
   `rg "\[Category\]" src/ --type cs` after each batch to verify.
5. **`string` interpolation stays.** Our `Info`/`Debug`/`Warn`/`Error`
   take a single `string` (not a structured template), so the
   `$"…"` form is still the correct call shape. The structured
   property only attaches to the `Op=` scope prefix, not the
   message body — that is by design (Phase 2 will introduce
   `Microsoft.Extensions.Logging` with proper template support).
6. **Hot loops stay on `Debug` with a counter.** Phase 0b already
   flipped the chatty hot paths to `Debug`; the scope refactor
   does not change that decision. See the
   `Measure`-without-`Debug`-per-row pattern in
   `LocalFamilyImportService` if the loop body still wants a
   "processed N rows" line.
7. **Exception logging keeps the `ex` variable.** If the call site
   is in a `catch (Exception ex)` block, the new code becomes
   `SmartConLogger.Warn($"failed: {ex.GetType().Name}: {ex.Message}");`
   inside the scope — the exception detail is still in the
   message, the `ex` object is not passed to a separate `Error(ex,…)`
   overload because our `Error(string)` does not accept one.
   Documented as a known limitation; Phase 2 will widen the API.

### Verification

After each batch of refactors, the build **must** stay green and
the test suite **must** stay at 1227/1227:

```bash
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25
```

A failing test is always a refactor bug — the log output format
itself is part of the contract (`SmartConLoggerScopeTests`).

### Rollback

Every batch is one commit. To roll back, `git revert` the commit
header. There is no "Phase 1 staging branch"; the work lands
directly on `feature/logging-improvements` so bisect still works
across the whole 1011-site refactor.

## Migration history

- **Phase 0 (2026-06):**
  - `LogScope`/`Measure` rewritten on top of `AsyncLocal<ImmutableStack<>>`
    so the `OpId` flows through `await` and thread-pool hops.
  - `lookup-diagnostic.log` and `freeze-diagnostic.log` removed; their
    content now lives in `smartcon.log` with `[Lookup]` / `[Freeze]`
    tags.
  - `formula-diagnostic.log` is now strictly append-only — no rotation,
    no size cap. Operators can collect months of data.
  - `MinLevel` backing field is `volatile` so the cross-thread override
    is memory-safe.
  - `FreezeTimer` and `FreezeThreadPool` removed (zero production
    callers; never re-introduce them — use `BeginScope` + `Measure`).

- **Earlier:**
  - The level-based gating was introduced after a debugging session
    where 200+ `[INF]` lines per batch import made the production log
    unusable. The previous behaviour was "always write everything",
    with callers expected to manually wrap each call in a
    `if (DebugEnabled) { ... }` block. The new approach inverts the
    responsibility: callers declare their intent (`Debug` vs `Info`)
    and the logger filters centrally, so the call sites stay readable
    and there is exactly one place to tune the verbosity.
