# Revit Plugin Async & Threading Patterns

## The #1 Fatal Bug: Sync-over-Async Deadlock

### Symptom
Revit freezes after clicking a button. Process cannot be killed. Only force-close via Task Manager.

### Root Cause
Calling `.GetAwaiter().GetResult()` or `.Result` on an async method **inside ExternalEvent callback** (Revit UI thread).

```csharp
// DANGEROUS - causes deadlock
_externalEvent.Raise(() =>
{
    var result = _repository.GetDataAsync().GetAwaiter().GetResult(); // FREEZES
});
```

**Why it deadlocks:**
1. `ExternalEvent.Raise()` runs on Revit UI thread
2. `.GetResult()` blocks UI thread waiting for task
3. Inside the async method, `await` captures `SynchronizationContext.Current` (UI thread)
4. Task tries to resume on UI thread → UI thread is blocked → **deadlock**

### The Fix: Task.Run()

```csharp
// SAFE - runs async code on ThreadPool
_externalEvent.Raise(() =>
{
    var result = Task.Run(() => _repository.GetDataAsync()).GetAwaiter().GetResult();
});
```

**Why it works:**
- `Task.Run()` executes on ThreadPool (no SynchronizationContext)
- Async method resumes on ThreadPool, not UI thread
- UI thread is blocked only briefly, no deadlock

## The Golden Rule

```
ExternalEvent callback (UI thread)
├── Revit API calls → DIRECT (Transaction, LoadFamily, etc.)
└── Async operations → Task.Run() (SQLite, File I/O, HTTP)
```

## Complete Example: Load Family

```csharp
[RelayCommand]
private void LoadAndPlace()
{
    _externalEvent.RaiseWithApplication(appObj =>
    {
        var uiApp = (UIApplication)appObj;
        var doc = _revitContext.GetDocument();
        
        // ThreadPool: file resolution (SQLite/async)
        var resolved = Task.Run(() => 
            _fileResolver.ResolveAsync(id, version, ct)).GetAwaiter().GetResult();
        
        // UI thread: Revit API
        var result = _loadService.LoadFamily(resolved, options, ct);
        
        // ThreadPool: save metadata
        FireAndForget(() => SaveTypesAsync(id, typeNames));
    });
}

// FireAndForget pattern for post-load operations
private static async void FireAndForget(Func<Task> taskFactory)
{
    try { await taskFactory(); }
    catch (Exception ex) { SmartConLogger.Error($"FireAndForget: {ex}"); }
}
```

## Common Mistakes

### Mistake 1: Wrapping Revit API in Task.Run
```csharp
// WRONG - Revit API must run on UI thread
var result = Task.Run(() => _loadService.LoadFamily(...)).GetAwaiter().GetResult();
// Error: "Failed to register a managed object"
```

### Mistake 2: Async void in ExternalEvent
```csharp
// WRONG - async void swallows exceptions, unpredictable
_externalEvent.Raise(async () =>
{
    await _repository.SaveAsync(); // Never do this
});
```

### Mistake 3: Blocking on UI thread without Task.Run
```csharp
// WRONG - classic deadlock
var data = _dbService.QueryAsync().Result;
```

## FireAndForget Best Practice

Use for **non-critical** post-operations (logging, analytics, caching):

```csharp
FireAndForget(() => _usageRepo.RecordUsageAsync(usage, ct));
FireAndForget(() => SaveTypesAndReloadTreeAsync(id, typeNames));
```

**Never** use FireAndForget for:
- Transaction commits
- User-facing results
- Operations where failure matters

## Why Not ConfigureAwait(false)?

In ExternalEvent callbacks you're on UI thread. Even with `ConfigureAwait(false)`, the calling code uses `.GetResult()` which still blocks. The only reliable solution is `Task.Run()` to remove the SynchronizationContext entirely.

## Summary Checklist

- [ ] Revit API calls → Direct in ExternalEvent
- [ ] SQLite/File/HTTP → Wrap in `Task.Run()`
- [ ] Post-operations → Use `FireAndForget()`
- [ ] Never use `.Result` or `.GetResult()` on async without `Task.Run()`
- [ ] Never wrap Revit API in `Task.Run()`