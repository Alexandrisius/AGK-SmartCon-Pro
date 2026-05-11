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
    _externalEvent.Raise(() =>
    {
        // ThreadPool: file resolution, SQLite
        var resolved = Task.Run(() => _resolver.ResolveAsync(id))
                          .GetAwaiter().GetResult();
        
        // UI thread: Revit API
        var result = _loadService.LoadFamily(resolved, options);
        
        // FireAndForget: non-critical post-processing
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
- [SmartCon Invariants](docs/invariants.md) - Project-specific rules (I-01..I-13)

## Known Bugs & Workarounds

### Revit Family Upgrade Freeze (REVIT-237190)

**Affected:** Revit 2023 < 2023.1.8, Revit 2025 < 2025.4.3  
**Symptoms:** UI freeze after loading old-version families, process hangs on exit  
**Fix:** `Marshal.ReleaseComObject(doc)` after `OpenDocumentFile` + `Close(false)`  
**Details:** [Async & Threading → Family Upgrade Freeze Bug](references/async-threading-patterns.md#family-upgrade-freeze-bug)

### C# Record `with` Expression Freeze (STA Thread)

**Affected:** Any Revit plugin using `record with` inside loops on UI thread  
**Symptoms:** UI freezes after `for`/`while` loop with `record with`, no exception  
**Fix:** Use LINQ `Select` or constructor instead of `with` in loops  
**Details:** [Record `with` Freeze Bug](references/record-with-freeze.md)