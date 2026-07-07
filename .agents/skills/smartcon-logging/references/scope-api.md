# Scope API — Detailed Reference

## `BeginScope` and `Dispose` semantics

```csharp
using var _scope = SmartConLogger.BeginScope("Category", ("Property", value));
//    ^^^^^^^^^^ local variable name MUST NOT be `_` if the body uses lambda discards
//              (`() => _ = SomeAsync()`) — C# CS0136 / CS1656 collision
```

The `using` statement is the **only** supported pattern. Do not call `LogScopeProvider.Push` directly — that's an internal API.

`_scope` can be any name. Convention is `_scope` for clarity. Use `_` only when:
- the method body has no `() => _ =` lambda
- you don't need to inspect the scope after assignment

## Property rules

`properties` is `params (string Key, object? Value)[]`. The `Key` and `Value` of each tuple are rendered as `Key=Value` after `Op={operation}`. The formatter skips a property iff its `Key == "Op"` AND its `Value.Equals(Operation)` — prevents `Op=X Op=X` duplicates from callers that add `("Op", operationName)` to a scope whose operation is also `operationName`.

Common property names (see SKILL.md for full table):

| Property | Type | Notes |
|---|---|---|
| `Method` | `string` | Use `nameof(MyMethod)` — keeps in sync on refactor |
| `ElementId` | `long` (or `int` on REVIT2024+) | `elementId.IntegerValue` for R21–R23, `elementId.Value` for R24+ |
| `CatalogItemId` | `string` | GUID for catalog items |
| `Count` | `int` | List size, batch count |
| `FilePath` | `string` | Full path to .rfa/.rvt file |
| `Attempt` | `string` | "Attempt1" / "Attempt2" (replaces `[{attemptName}]` prefix) |
| `Topology` | `string` | `chainPlan.Topology.ToString()` |
| `Level` | `int` | BFS depth in chain graph |
| `ConnectorIndex` | `int` | Connector index in element |
| `FittingId` / `DynId` / `StaticId` | `long` | `ElementId.IntegerValue` |

Do NOT add properties that allocate or query external state inside the tuple — they are evaluated eagerly when the scope opens. If a property is expensive, compute it into a local first:

```csharp
var count = list.Count; // compute once
using var _scope = SmartConLogger.BeginScope("MyCategory",
    ("Method", "Process"),
    ("Count", count));
```

## Nesting rules

Scopes stack — `BeginScope` inside another `BeginScope` produces two prefixes in the rendered line, in order of opening (outermost first):

```csharp
using var outer = SmartConLogger.BeginScope("Outer", ("Method", "M1"));
// outer prefix: [OpId=AAA Op=Outer Method=M1]
using var inner = SmartConLogger.BeginScope("Inner", ("Method", "M2"));
// inner prefix: [OpId=AAA Op=Outer Method=M1] [OpId=BBB Op=Inner Method=M2]
SmartConLogger.Info("inside inner");
// line: ...  [OpId=AAA Op=Outer Method=M1] [OpId=BBB Op=Inner Method=M2] inside inner
// outer.Dispose() → back to [OpId=AAA Op=Outer Method=M1]
// outer.Dispose() → back to no prefix
```

`using` block boundaries are the only thing that controls nesting. The compiler generates `try/finally` to call `Dispose` on every exit path.

## Threading model

`LogScopeProvider` is backed by `AsyncLocal<ScopeNode?>`. The scope value is part of `ExecutionContext` and is copied on:

| Operation | Scope flows? |
|---|---|
| `await` | yes (ExecutionContext flows) |
| `Task.Run(() => ...)` | yes (ExecutionContext captured) |
| `IExternalEventHandler.Execute(UIApplication)` | yes (when called via our `IFamilyManagerAwaitableEvent` which captures context) |
| `ThreadPool.QueueUserWorkItem` | yes |
| `new Thread(() => ...).Start()` | NO — creates fresh ExecutionContext |
| `ConfigureAwait(false)` | yes (ExecutionContext is not a SyncContext) |

If you see a log line missing `[OpId=…]` while a parent scope is logically active, the most likely cause is a manually-created `Thread` that didn't capture the context. The fix is to use `Task.Run` (or `await Task.Yield()` to detach from the UI sync context, then re-enter via `await`).

## Push-once discipline

`LogScopeProvider.Push` checks for accidental double-push — it pops only if the same `ScopeNode` is on top. The defensive `PopOnDispose` in `LogScopeProvider.cs` line 88–92:

```csharp
if (!ReferenceEquals(_top.Value, _node)) return;
_top.Value = _node.Next;
```

This means if you `Dispose` a scope that is no longer on top (e.g. another scope was disposed first), the chain is left alone. This is a defensive guard — code with `using` discipline should never trigger it.

## What `LogScopeProvider.Current` returns

`Current` returns the **innermost** scope (top of the stack), or `null` if no scope is active. `EnumerateFromRoot()` returns the full chain from outermost to innermost — that's what `WriteMain` uses to render the prefix.

## Performance characteristics

- `BeginScope`: allocates one `LogScope` + one `ScopeNode` + one `IDisposable` (boxed in `using var`). ~120 bytes per open.
- `Measure`: also `Stopwatch` + `TimedScope`. ~80 bytes per open.
- `Info`/`Debug`/`Warn`/`Error`: each call takes `lock(_lock)` (writer is shared). The lock is held for the duration of the `StreamWriter.WriteLine` call. In Debug build at `Info` level, this is typically < 5 µs per call.
- `FormatPrefix`: rebuilds the prefix string on every log call. With 4 nested scopes this is ~6 string concatenations of ~80-char strings. Measurable in hot loops (> 10k calls/sec) but negligible in interactive UI code.

The hot-loop optimization documented in `docs/architecture/logging.md` (counter pattern) is still valid for code that calls `Info`/`Debug` more than 10 000 times per second.

## `LogSessionStart` vs `BeginScope`

`LogSessionStart("FamilyImport")` writes a visually distinct block:
```
=========================================================================
SESSION START: FamilyImport  [2026-06-09 19:53:25]
=========================================================================
```

It does **not** create a scope. It is purely a visual marker. Use it for top-level user-initiated actions (a command invoked from Revit's ribbon, a drag-drop, a button click in a WPF dialog). Pair it with the surrounding `BeginScope("FMImport", …)` which actually tracks the operation's lifetime.

## Common mistakes

### 1. `using var _ = BeginScope(...)` with a lambda discard in body
```csharp
// WRONG — CS0136 / CS1656
using var _ = SmartConLogger.BeginScope("X");
someHandler.Saved += () => _ = LoadAsync();  // '_' collides
```
Fix: name the variable.
```csharp
using var _scope = SmartConLogger.BeginScope("X");
someHandler.Saved += () => _ = LoadAsync();
```

### 2. Storing the scope in a field
```csharp
// WRONG — the scope is disposed at the end of the method,
// but the field still references it. Subsequent calls would
// operate on a disposed scope.
private IDisposable? _scope;
void Start() => _scope = SmartConLogger.BeginScope("X");
void Stop() => _scope?.Dispose();
```
Fix: use `using` so the scope is tied to a lexical block.

### 3. Putting `BeginScope` inside a `for`/`foreach` body
```csharp
// WRONG — every iteration opens a new scope with the SAME
// OpId namespace. Lines from iteration 2 cannot be distinguished
// from iteration 1 by OpId.
foreach (var item in items)
{
    using var _ = SmartConLogger.BeginScope("Loop", ("Index", item.Index));
    DoWork(item);
}
```
Fix: open the scope outside the loop, put the per-iteration data into a property only when it's stable for the whole batch:
```csharp
using var _ = SmartConLogger.BeginScope("Loop", ("Count", items.Count));
foreach (var item in items)
{
    SmartConLogger.Info($"Processing {item.Name}");
    DoWork(item);
}
```

### 4. Calling `BeginScope` with an empty properties array
```csharp
// Equivalent to BeginScope("X") with no properties.
using var _ = SmartConLogger.BeginScope("X", Array.Empty<(string, object?)>());
```
Not an error, but if you have no properties to add, prefer the shorter overload:
```csharp
using var _ = SmartConLogger.BeginScope("X");
```

### 5. `BeginScope` from a static constructor
`AsyncLocal` works fine in static contexts, but the scope will never be `Dispose`d because the constructor returns. **Don't** open scopes from static initializers or static field initializers.
