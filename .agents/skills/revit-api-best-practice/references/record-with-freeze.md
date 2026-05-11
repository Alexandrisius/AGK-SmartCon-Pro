# C# Record `with` Expression Freeze in STA Thread

## The Bug

**C# `record with` expression inside `for` loop freezes Revit UI.**

```csharp
// DANGEROUS - freezes Revit UI when called from ExternalEvent handler
for (var i = 0; i < types.Count; i++)
{
    types[i] = types[i] with { SortOrder = i };  // ← FREEZES!
}
```

## Root Cause

The C# compiler generates a **virtual `<Clone>$()` method** for `record` types. When you use `with`, the compiler emits:

```csharp
var clone = obj.<Clone>$();    // virtual method call
clone.Property = newValue;     // set init-only property
```

Inside a `for` loop with `List<T>` indexer:
1. **JIT compilation** — `<Clone>$()` may not be JIT-compiled yet; compilation happens during the loop
2. **STA thread blocked** — JIT compilation in STA thread blocks message pump
3. **COM deadlock** — Revit's COM interop depends on message pump; when blocked, COM calls hang
4. **UI freeze** — WPF render thread stops updating

**Why LINQ `Select` is safe:**
```csharp
.Select((t, i) => t with { SortOrder = i })  // lambda JIT-compiled BEFORE loop
```

**Why `new` constructor is safe:**
```csharp
types[i] = new MyRecord(old.TypeName, i, old.Values);  // direct call, no virtual
```

## Evidence

| Test | Code Pattern | Result |
|------|-------------|--------|
| Dirty test | `continue` in COM `foreach` + `GC.Collect()` | No freeze |
| Test 2 | `Sort()` + `for` loop with `with` | **FREEZE** |
| Test 3 | `Sort()` only (no loop) | No freeze |
| Test 4 | `Sort()` + `for` loop with `new` | No freeze |

**Conclusion:** `record with` inside `for` loop is the sole trigger.

## Affected Code Patterns

### Pattern 1: List indexer + `with` (BROKEN)
```csharp
for (var i = 0; i < list.Count; i++)
{
    list[i] = list[i] with { Property = value };  // FREEZES in STA thread
}
```

### Pattern 2: Array indexer + `with` (BROKEN)
```csharp
for (var i = 0; i < array.Length; i++)
{
    array[i] = array[i] with { Property = value };  // Same issue
}
```

## Safe Alternatives

### Option A: LINQ `Select` (RECOMMENDED)
```csharp
var sorted = items
    .OrderBy(t => t.Name)
    .Select((t, i) => t with { SortOrder = i })  // safe: lambda JIT'd before execution
    .ToList();
```

### Option B: Constructor instead of `with`
```csharp
for (var i = 0; i < types.Count; i++)
{
    var old = types[i];
    types[i] = new MyRecord(old.TypeName, i, old.Values);  // safe: direct constructor
}
```

### Option C: Mutable struct/class
```csharp
// Use class with settable properties instead of record
public class MutableItem
{
    public string Name { get; set; }
    public int SortOrder { get; set; }
    public IReadOnlyList<Value> Values { get; set; }
}
```

## When It Happens

- **STA thread only** (WPF UI thread, ExternalEvent handler)
- **During JIT compilation** — first time `<Clone>$()` is called
- **With `List<T>` or array indexer** — creates temp variable + virtual call
- **NOT in background threads** — no message pump dependency

## Detection

Symptoms:
- UI freezes after loop with `record with`
- Process not responding, but no exception
- Mouse still moves, clicks processed but UI doesn't redraw
- Freeze resolves after second user interaction (e.g., right-click)

## References

- [GitHub Issue #106468](https://github.com/dotnet/runtime/issues/106468) — `record with` crashes under AOT
- [StackOverflow: Record constructor not called with `with`](https://stackoverflow.com/questions/75519223) — compiler generates `<Clone>$()` and copy constructor
- [Roslyn PR #50650](https://github.com/dotnet/roslyn/pull/50650) — race conditions in record member synthesis
- [JIT Issue #110968](https://github.com/dotnet/runtime/issues/110968) — bad codegen for `record class` + value types

## Rule

**Never use `record with` expression inside `for`/`while` loop when running in STA thread (WPF/Revit UI).**

Use LINQ `Select` or constructor call instead.
