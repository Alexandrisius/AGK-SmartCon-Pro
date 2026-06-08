# Logging

`SmartCon` ships its own static logger (`SmartCon.Core.Logging.SmartConLogger`)
to keep the project free of a third-party logging dependency. This document
captures the rules every contributor must follow and the knobs available to
operators in the field.

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

| Build configuration | `MinLevel` default | What the user sees                              |
|---------------------|--------------------|-------------------------------------------------|
| `Debug.*` (R19-R26) | `Debug`            | everything: trace, info, warn, error            |
| `Release.*` (R19-R26) | `Info`           | info, warn, error (no per-row trace)            |

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
   to force a level for the entire session

The third option is intentionally last so that one-off diagnostic lines
(`SmartConLogger.MinLevel = LogLevel.Debug;`) never leak into a Release
build by accident.

## What goes in which file

| File under `%AppData%\AGK\SmartCon\` | Contents                                          | Filtering            |
|--------------------------------------|---------------------------------------------------|----------------------|
| `smartcon.log`                       | `Info`, `Debug`, `Warn`, `Error` from the main logger | `MinLevel` gate      |
| `lookup-diagnostic.log`              | category/parameter lookup traces                  | always on            |
| `formula-diagnostic.log`             | formula evaluation traces                         | always on            |
| `freeze-diagnostic.log`              | freeze / hang diagnostics (timings, thread pool)  | always on            |

The three diagnostic files are not gated by `MinLevel` because they are
opt-in by the very fact that the diagnostic code is on the hot path —
they only ever contain a handful of lines per session and exist
specifically to be turned on when a customer reports a freeze or a wrong
lookup. Operators who want to silence them can simply delete the file;
it will not be recreated until something is written to it.

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

## Migration history

The level-based gating was introduced after a debugging session where
200+ `[INF]` lines per batch import made the production log unusable.
The previous behaviour was "always write everything", with callers
expected to manually wrap each call in a `if (DebugEnabled) { ... }`
block. The new approach inverts the responsibility: callers declare
their intent (`Debug` vs `Info`) and the logger filters centrally, so
the call sites stay readable and there is exactly one place to tune the
verbosity.
