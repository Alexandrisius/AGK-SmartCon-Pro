---
name: revit-api-best-practice
description: "Revit plugin development best practices and patterns. Covers threading/Async deadlocks, Transaction patterns, ExternalEvent architecture, MVVM rules, DI setup, and common pitfalls. Essential for C# Revit add-in developers working with WPF and .NET. Keywords: Revit API, ExternalEvent, Transaction, IExternalCommand, IExternalApplication, threading, deadlock, Task.Run, FireAndForget, MVVM, WPF, DI."
license: MIT
metadata:
  author: AGK Engineering
  version: "1.0.0"
---

# Revit API Best Practices

Practical guide for building robust Revit plugins with .NET/C# and WPF.

## Quick Decision Tree

| Situation | Solution | Reference |
|---|---|---|
| Button click needs Revit API | `IExternalCommand` + `Transaction` | [core-patterns](references/core-patterns.md) |
| Ribbon UI at startup | `IExternalApplication.OnStartup` | [core-patterns](references/core-patterns.md) |
| Long-running background task | `ExternalEvent` + `IExternalEventHandler` | [async-threading](references/async-threading-patterns.md) |
| SQLite/async in ExternalEvent | `Task.Run(() => ...).GetResult()` | [async-threading](references/async-threading-patterns.md) |
| WPF dialog shows Revit data | Dialog `DataContext` + `ExternalEvent` | [async-threading](references/async-threading-patterns.md) |
| Family load + type placement | `LoadFamily` + `PostRequestForElementTypePlacement` | [core-patterns](references/core-patterns.md) |
| WPF freeze after family upgrade | Move PropertyChanged inside ExternalEvent | [wpf-mfc-render-freeze](references/wpf-mfc-render-freeze.md) |

## Critical Rules

### 1. Never Call Revit API from Background Thread
```csharp
// WRONG - Revit API is not thread-safe
Task.Run(() => doc.Create.NewFamilyInstance(...));

// CORRECT - always on UI thread via ExternalEvent
_externalEvent.Raise(() => doc.Create.NewFamilyInstance(...));
```

### 2. Never Use .Result/.GetResult() on Async Without Task.Run()
```csharp
// WRONG - deadlock on UI thread
var data = _repo.QueryAsync().GetAwaiter().GetResult();

// CORRECT - offload to ThreadPool
var data = Task.Run(() => _repo.QueryAsync()).GetAwaiter().GetResult();
```

### 3. Transaction Rules
- Always inside `ExternalEvent` handler (never from WPF directly)
- One transaction per logical operation
- Use `TransactionGroup` for multi-step undo
- Never store `Element`/`ElementId` between transactions (use `ElementId` only)

### 4. WPF + Revit Architecture
```
WPF Window (View)
    ↓ DataContext
ViewModel (ObservableObject)
    ↓ Command
ExternalEvent.Raise()
    ↓ Handler
Revit API (Transaction)
```

**Key principle**: WPF never touches Revit API directly. Always through ExternalEvent.

## Architecture Patterns

### Pattern A: Simple Command
```csharp
[RelayCommand]
private void DoSomething()
{
    _externalEvent.Raise(() =>
    {
        using var tx = new Transaction(doc, "Action");
        tx.Start();
        // ... Revit API calls ...
        tx.Commit();
    });
}
```

### Pattern B: Command with Async Data Loading
```csharp
[RelayCommand]
private void LoadAndPlace()
{
    // WARNING: If LoadFamily triggers MFC upgrade dialog, do NOT set
    // StatusMessage/IsLoading (with XAML binding) BEFORE _externalEvent.Raise().
    // Move ALL PropertyChanged inside the ExternalEvent handler.
    // See: [WPF MFC Render Freeze](references/wpf-mfc-render-freeze.md)

    _externalEvent.Raise(() =>
    {
        // ThreadPool: file resolution, SQLite
        var resolved = Task.Run(() => _resolver.ResolveAsync(id))
                          .GetAwaiter().GetResult();
        
        // UI thread: Revit API (may show MFC family upgrade dialog)
        var result = _loadService.LoadFamily(resolved, options);
        
        // FireAndForget: non-critical post-processing (ONLY after Revit API)
        FireAndForget(() => SaveMetadataAsync(result));
    });
}
```

### Pattern C: Dialog with Revit Data
```csharp
// ViewModel
[RelayCommand]
private void OpenDialog()
{
    var vm = new MyDialogViewModel();
    
    _externalEvent.Raise(() =>
    {
        var data = CollectRevitData(doc);
        vm.SetData(data); // Update VM on UI thread
    });
    
    _dialogService.ShowDialog(vm);
}
```

## Common Pitfalls

| Pitfall | Why It Happens | Fix |
|---|---|---|
| Revit freezes on button click | Sync-over-async deadlock | Use `Task.Run()` for SQLite, direct for Revit API |
| "Failed to register managed object" | Revit API called from ThreadPool | Remove `Task.Run()` around Revit API |
| Dialog data is stale | Revit data collected before dialog shows | Use `ExternalEvent` to populate VM after dialog opens |
| Transaction fails silently | Exception swallowed by async void | Use `try/catch` + logging in ExternalEvent handler |
| Memory leak | Storing `Element` references | Store `ElementId` only, re-query when needed |

## Multi-Version Support

| Revit Version | .NET Target | Notes |
|---|---|---|
| 2019-2024 | net48 | Use `ValueTuple` package |
| 2025-2026 | net8.0-windows | Native `ValueTuple`, C# 12 |

## Resources

- [Core API Patterns](references/core-patterns.md) - 11 essential code patterns
- [Async & Threading](references/async-threading-patterns.md) - Deadlock prevention and COM cleanup
- [WPF MFC Render Freeze](references/wpf-mfc-render-freeze.md) - WPF render thread zombie from PropertyChanged before MFC dialog
- [Known Bugs](references/transaction-callback-freeze.md) - WPF freeze from transaction callback logging

## Known Bugs & Workarounds

### Revit Family Upgrade Freeze (REVIT-237190)

**Affected:** Revit 2023 < 2023.1.8, Revit 2025 < 2025.4.3  
**Symptoms:** UI freeze after loading old-version families, process hangs on exit  
**Fix:** `Marshal.ReleaseComObject(doc)` after `OpenDocumentFile` + `Close(false)`  
**Details:** [Async & Threading → Family Upgrade Freeze Bug](references/async-threading-patterns.md)

### C# Record `with` Expression Freeze (STA Thread)

**Affected:** Any Revit plugin using `record with` inside loops on UI thread  
**Symptoms:** UI freezes after `for`/`while` loop with `record with`, no exception  
**Fix:** Use LINQ `Select` or constructor instead of `with` in loops  
**Details:** [Record `with` Freeze Bug](references/record-with-freeze.md)

### WPF Dockable Panel Freeze from Transaction Callback Logging

**Affected:** WPF DockablePane using `ITransactionService` callbacks  
**Symptoms:** UI freezes after button click, unfreezes on next interaction  
**Fix:** Never log or do I/O inside `RunInTransaction`/`RunAndRollback` callbacks. Log before/after only.  
**Details:** [Transaction Callback Freeze](references/transaction-callback-freeze.md)

### WPF DockablePane Freeze in net48 — `FireAndForget` + null `Application.Current?.Dispatcher`

**Affected:** Revit 2019-2024 (net48) when a `FireAndForget` (`Task.Run + ConfigureAwait(false)`) lambda updates the WPF TreeView after a save. **Does not** affect Revit 2025-2026 (net8.0-windows) because `Application.Current` is non-null there.

**Symptoms:** After triggering an import (or any FireAndForget that updates the tree), the **LMB does not respond** on the tree, drag-select is broken, the freeze **resolves on right-click** until the next operation. In Revit 2025 (net8) the same code works fine.

**Two-part root cause:**
1. `Application.Current?.Dispatcher` is **null** in net48 Revit add-ins (WPF `Application` is not created; `IExternalApplication` is used instead). The `if (dispatcher is { HasShutdownStarted: false })` check is false, the `if` body silently never runs, `LoadTreeAsync` is never invoked. See [lepoco/wpfui#662](https://github.com/lepoco/wpfui/issues/662).
2. `Task.Run + ConfigureAwait(false)` inside `FireAndForget` drops the UI `SynchronizationContext`, so even if Part 1 were fixed the `TreeNodes = ...` setter would run on a thread pool thread. Combined with double `LoadTreeAsync` around save (~15ms of main-thread work in net48), the WPF render thread falls behind and the tree appears frozen until the next right-click forces a repaint.

**Fix (two rules):**
1. **Capture the dispatcher in the VM ctor** (which runs on the UI thread):
   ```csharp
   _uiDispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
   ```
   `Dispatcher.CurrentDispatcher` from a thread-pool FireAndForget lambda is wrong — it creates a brand-new dispatcher for that thread. Capture in the ctor is the only correct way.
2. **Marshal explicitly from FireAndForget back to the UI thread:**
   ```csharp
   if (!_uiDispatcher.HasShutdownStarted)
   {
       await _uiDispatcher.InvokeAsync(() => LoadTreeAsync());
   }
   ```

**Plus three supporting rules:** (a) no double `LoadTreeAsync` around save — one is enough; (b) minimize `using var _measure = SmartConLogger.Measure(...)` and `Debug` on the UI thread (Rule 4 of `transaction-callback-freeze.md`); (c) don't try to "optimize" `TreeNodes = rootNodes` to `Clear() + foreach Add()` in net48 — it breaks the display.

**Decision and full post-mortem:** [`docs/adr/031-fireandforget-ui-marshalling.md`](../../docs/adr/031-fireandforget-ui-marshalling.md). Implementation details, diagnostic recipe (compare net8 vs net48 logs side-by-side, look for `LoadTreeAsync: finally`), and 9 Exa sources: [`revit-wpf-compat` → `references/fireandforget-freeze-net48.md`](../../.agents/skills/revit-wpf-compat/references/fireandforget-freeze-net48.md).

### WPF Render Thread Freeze from PropertyChanged Before MFC Dialog

**Affected:** WPF DockablePane with `PropertyChanged` before `ExternalEvent.Raise()` that triggers MFC family upgrade dialog  
**Symptoms:** UI "alive" (clicks work, window moves) but doesn't redraw after family upgrade dialog. Process closes normally.  
**Fix:** Move ALL `PropertyChanged` (StatusMessage, IsLoading with XAML binding) inside `ExternalEvent` handler. Never use `FireAndForget(async)` before `ExternalEvent` with MFC dialog.  
**Details:** [WPF MFC Render Freeze](references/wpf-mfc-render-freeze.md)

### IDropHandler.Execute + LoadFamily Async Pattern (CRITICAL)

**Affected:** `IDropHandler.Execute` calling `IFamilyLoadService.LoadFamilyAsync` / `LoadFamilySymbolAsync`  
**Symptoms:** "Failed to register a managed object" or "The calling thread cannot access this object because a different thread owns it" when wrapped in `AsyncBridge.RunSync(() => LoadFamilyAsync(...))` (= `Task.Run`).  
**Root cause:** `IFamilyLoadService.LoadFamilyAsync` is a sync wrapper that internally calls `doc.LoadFamily` / `doc.LoadFamilySymbol` (Revit API, main UI thread only). `AsyncBridge.RunSync` (= `Task.Run` + `GetResult`) shifts the call to ThreadPool → Revit API on non-main thread → crash.  
**Fix:** Do NOT wrap Revit API in `AsyncBridge.RunSync`. Use `.GetAwaiter().GetResult()` directly — it's deadlock-free because the underlying method is `Task.FromResult(...)` with no real `await`.  
**Real example (feature/logging-improvements B1 fix):**
```csharp
// WRONG — Task.Run + Revit API = crash
result = AsyncBridge.RunSync(() => _loadService.LoadFamilyAsync(resolved, options, _onStatusMessage, ct));

// CORRECT — sync-on-sync on main thread, no deadlock
result = _loadService.LoadFamilyAsync(resolved, options, _onStatusMessage, ct).GetAwaiter().GetResult();
```
**Decision rule:** `AsyncBridge.RunSync` is SAFE only for true async I/O (SQLite, file, HTTP). It is DANGEROUS for any method that internally calls Revit API. See `AsyncBridge.cs` XML doc for the full SAFE/DANGEROUS table.  
**Details:** [Async & Threading](references/async-threading-patterns.md) (Mistake 1)