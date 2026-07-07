# Hot-Loop Logging Counter Pattern

## Why this exists

`SmartConLogger.Debug($"…")` allocates a string for the interpolation on every call.
Inside a `for`/`foreach` that runs thousands of times per second (chain BFS,
connector search, lookup-table fallback) the cost is significant:

| Calls per second | Allocations | Cost (rough) |
|---:|---:|---|
| 100 | 100 strings | negligible |
| 1 000 | 1 000 strings | measurable |
| 10 000 | 10 000 strings | visible in profiler |
| 100 000+ | 100 000 strings | dominant in hot paths |

The pattern below emits **one** log line per N iterations instead of one per
iteration, keeping diagnostic value while dropping allocation cost by N×.

## Helper: `HotLoopCounter`

```csharp
public struct HotLoopCounter
{
    public HotLoopCounter(int sampleEvery) { ... }
    public int Count { get; }
    public int SampleEvery { get; }
    public bool ShouldLog();
}
```

- Value type — no heap allocation.
- `sampleEvery` rounds up to the next power of two so `ShouldLog()` is one
  bitwise `AND` instead of a `%` division.
- Coerces 0 / negative values to 1 (every call).

## Pattern: replace raw `Debug` in loops

### Before (allocates per iteration)

```csharp
foreach (var c in allConns)
{
    SmartConLogger.Debug($"connector: {c.ConnectorIndex} R={c.Radius}");
    // ... heavy work ...
}
```

### After (one log per 1024 iterations + final summary)

```csharp
var counter = new HotLoopCounter(sampleEvery: 1024);
foreach (var c in allConns)
{
    // ... heavy work ...
    if (counter.ShouldLog())
        SmartConLogger.Debug($"processed {counter.Count}/{allConns.Count}");
}
SmartConLogger.Debug($"done: {allConns.Count}");
```

## Choosing `sampleEvery`

| Loop size | Recommended `sampleEvery` | Rationale |
|---:|---:|---|
| < 100 | 1 (or no counter) | overhead is negligible; just `Debug` per iter |
| 100 – 1 000 | 16 or 32 | one log every ~30 iter |
| 1 000 – 10 000 | 64 or 128 | one log every ~100 iter |
| 10 000 – 100 000 | 256 or 512 | one log every ~250 iter |
| > 100 000 | 1 024 or 2 048 | one log every ~1k iter; consider whether you need to log at all |

When in doubt, **1024** is the safe default.

## Things to avoid

### Don't put the counter check before heavy work

```csharp
// BAD — logs "processed N" BEFORE doing the work
var counter = new HotLoopCounter(1024);
foreach (var c in allConns)
{
    if (counter.ShouldLog())
        SmartConLogger.Debug($"processed {counter.Count}/{allConns.Count}");
    // ... heavy work ...
}
```

```csharp
// GOOD — logs after work is done
var counter = new HotLoopCounter(1024);
foreach (var c in allConns)
{
    // ... heavy work ...
    if (counter.ShouldLog())
        SmartConLogger.Debug($"processed {counter.Count}/{allConns.Count}");
}
```

### Don't reset the counter between loops

`HotLoopCounter` is a value type. If you need a counter that spans
multiple loops, hoist it outside the method or pass it by `ref`.

### Don't log exception text inside the counter

```csharp
// BAD — exception.ToString() allocates even when ShouldLog is false
foreach (var c in allConns)
{
    try { ... }
    catch (Exception ex)
    {
        if (counter.ShouldLog())
            SmartConLogger.Debug($"ex: {ex}");  // ex.ToString() called only here — OK
    }
}
```

`ex.ToString()` is **inside** the `if`, so it only runs when we actually
log. Good.

## `IsEnabled` guard alternative

For very high-frequency loops (millions per second) even the counter check
is overhead. The alternative is to disable `Debug` entirely:

```csharp
if (SmartConLogger.MinLevel <= LogLevel.Debug)
{
    foreach (var c in allConns)
    {
        // ... hot work, can freely call SmartConLogger.Debug ...
    }
}
```

In Release builds `MinLevel == LogLevel.Info`, so the guard short-circuits
the entire loop. This is what we use in `RevitLookupTableService` for the
fuzz-fallback path (called per-lookup, can run thousands of times per
import).

## Where this pattern applies in the codebase

- `RevitLookupTableService` — fuzz fallback per-lookup
- `RevitParameterResolver` — per-constraint evaluation
- `ChainOperationHandler` — connector traversal
- `PipeConnectSessionBuilder` — connector validation
- `RevitDynamicSizeResolver` — symbol search

## Reference

- Helper: `src/SmartCon.Core/Logging/HotLoopCounter.cs`
- Tests: `src/SmartCon.Tests/FamilyManager/Models/HotLoopCounterTests.cs`
- Skill: `.agents/skills/smartcon-logging/SKILL.md` (constraint #1)
