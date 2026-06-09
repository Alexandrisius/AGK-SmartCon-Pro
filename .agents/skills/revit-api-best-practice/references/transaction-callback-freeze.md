# WPF Dockable Panel Freeze from Transaction Callback Logging

## The Bug

UI freezes after button click in WPF DockablePane or modeless dialog that uses `ITransactionService` callbacks. The freeze resolves only on next user interaction (right-click, window resize, or Revit window focus change).

## Root Cause

Blocking operations (logging, file I/O) executed inside `RunInTransaction` or `RunAndRollback` callbacks block the WPF message pump. The UI thread waits for the callback to complete, but the callback itself is blocked by synchronous I/O (e.g., writing to a log file).

Additionally, wrapping `_transactionService.RunInTransaction` in `try-catch` alters exception flow and can interfere with Revit's internal transaction state, causing the UI thread to remain blocked until the next Windows message is processed.

```csharp
// DANGEROUS - blocks UI thread inside transaction callback
_externalEvent.Raise(() =>
{
    _transactionService.RunInTransaction(doc, "LoadFamily", () =>
    {
        SmartConLogger.Info("Starting LoadFamily");  // ← BLOCKS if logger does file I/O
        doc.LoadFamily(path, out var family);
        SmartConLogger.Info($"Loaded: {family.Name}");  // ← BLOCKS
    });
});
```

## The Fix

### Rule 1: Log Outside Transaction Callbacks

```csharp
// SAFE - log before/after, not inside
SmartConLogger.Info("Starting LoadFamily");

_externalEvent.Raise(() =>
{
    _transactionService.RunInTransaction(doc, "LoadFamily", () =>
    {
        // Only Revit API calls here - no logging, no I/O
        doc.LoadFamily(path, out var family);
    });
});

SmartConLogger.Info($"Loaded: {familyName}");
```

### Rule 2: Never Wrap RunInTransaction in try-catch

```csharp
// DANGEROUS - swallows exceptions, corrupts transaction state
try
{
    _transactionService.RunInTransaction(doc, "Action", () => { ... });
}
catch (Exception ex)
{
    SmartConLogger.Error(ex);  // ← NEVER
}

// SAFE - let exceptions propagate, handle outside
_externalEvent.Raise(() =>
{
    _transactionService.RunInTransaction(doc, "Action", () => { ... });
});
```

### Rule 3: Keep Callbacks Minimal

```csharp
// SAFE - only Revit API in callback
_transactionService.RunInTransaction(doc, "PlaceFamily", () =>
{
    if (!symbol.IsActive) symbol.Activate();
    doc.Create.NewFamilyInstance(location, symbol, level, StructuralType.NonStructural);
});
```

### Rule 4: No New `BeginScope` Around `RunInTransaction` (or Nested Inside Caller's Scope)

The structured logger (`SmartConLogger`) opens a new ambient scope via `BeginScope(...)` and writes a closing `=== END ===` marker in `PopOnDispose.Dispose`. The closing marker is **synchronous file I/O on the calling thread** — `lock + StreamWriter.WriteLine`. If the scope is created in a method that calls `RunInTransaction`, the closing marker fires between two Revit operations on the main UI thread, which can cause sluggish UI and contributes to freezes in Revit 2023 (WPF render thread is sensitive to main-thread blocking).

```csharp
// DANGEROUS — extra file I/O on main thread between two Revit operations
private FamilyLoadResult? TryLoadInTransaction(...)
{
    using var _scope = SmartConLogger.BeginScope("FamilyLoad", ...); // ← DON'T
    _transactionService.RunInTransaction("Load Family", _ => { ... doc.LoadFamily ... });
}

// CORRECT — rely on the caller's scope, or use a single outer scope on the entry method
private FamilyLoadResult? TryLoadInTransaction(...)
{
    _transactionService.RunInTransaction("Load Family", _ => { ... doc.LoadFamily ... });
}
```

**The same rule applies to nested scopes inside an already-scoped method.** `LoadFamilyAsync` (outer) calls `TryLoadInTransaction` (inner). Adding a `BeginScope` in the inner method gives **double `=== END ===` writes** on the main thread per drop.

**Decision rule:** place `BeginScope` only on the top-level public method (the one called from `IDropHandler.Execute` / `IExternalEventHandler.Execute` / `IExternalCommand.Execute`). Internal helpers should rely on the ambient chain via `Info`/`Debug` and put distinguishing properties into the message (e.g. `[Attempt1] Loaded successfully`) instead of opening their own scope.

## Affected Code Patterns

| Pattern | Result |
|---|---|
| `SmartConLogger.Info()` inside `RunInTransaction` | **FREEZE** if logger uses file I/O |
| `try-catch` around `_transactionService.RunInTransaction` | **FREEZE** on exception + corrupts state |
| Heavy computation inside callback | **FREEZE** or sluggish UI |
| Minimal Revit API only inside callback | Safe |
| `using var _scope = BeginScope(...)` **around** `RunInTransaction` | **SLOW / hot path I/O** — `PopOnDispose.Dispose` writes `=== END ===` to disk via `lock + StreamWriter.WriteLine`. Sits between two Revit operations on the main UI thread. |
| Nested `BeginScope` inside a method that already has outer scope (e.g. `TryLoadInTransaction` inside `LoadFamilyAsync`) | **DOUBLE I/O on main thread** — outer + inner each write their own `=== END ===`. |

## When It Happens

- **WPF DockablePane** with `ITransactionService` callbacks
- **Modeless dialogs** using transaction service
- **Any blocking I/O** inside transaction callback (file, network, database)
- **Exception swallowing** around transaction calls

## References

- [Autodesk Community: WPF DockablePane UI Freezes](https://forums.autodesk.com/t5/revit-api-forum/wpf-dockablepane-ui-freezes-when-loading-a-family-via-externalevent/m-p/12637117)
- [Autodesk Developer Blog: Revit API Context](https://aps.autodesk.com/blog/revit-api-context)
- [The Building Coder: DoEvents and UI Freezes](https://thebuildingcoder.typepad.com/blog/2019/07/doevents.html)
