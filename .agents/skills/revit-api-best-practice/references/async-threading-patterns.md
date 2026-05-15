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

## The #2 Fatal Bug: async Task FireAndForget in ExternalEvent

### Symptom
Revit freezes after Load/Place family. Status message "click to place" stays forever. Process hangs on exit.

### Root Cause
Replacing `async void FireAndForget` with `async Task FireAndForgetAsync` and calling `_ = FireAndForgetAsync(...)` inside ExternalEvent.

```csharp
// DANGEROUS - causes freeze/hang
_externalEvent.Raise(() =>
{
    // ... Revit API calls ...
    
    // Post-operation: async Task with discarded result
    _ = FireAndForgetAsync(() => SaveTypesAsync(id)); // FREEZES
});

private static async Task FireAndForgetAsync(Func<Task> taskFactory) { ... }
```

### The Science: Why it deadlocks (CLR-level explanation)

**SynchronizationContext capture** — the core mechanism:

When you `await` an incomplete Task in C#, the compiler generates code that captures `SynchronizationContext.Current` (or `TaskScheduler.Current` if no SynchronizationContext). This captured context is used to post the continuation back to the original thread [^1][^2].

[^1]: Stephen Toub, "ConfigureAwait FAQ", .NET Blog, 2019 — https://devblogs.microsoft.com/dotnet/configureawait-faq/
[^2]: Stephen Cleary, "Async/Await - Best Practices in Asynchronous Programming", MSDN Magazine, March 2013

Revit's ExternalEvent handler runs on the Revit UI thread, which has a `DispatcherSynchronizationContext` (WPF) or equivalent. This context's `Post()` method queues work to the UI thread's message loop.

**What happens with `async Task FireAndForgetAsync`:**

1. `FireAndForgetAsync` is called on Revit UI thread → captures `SynchronizationContext.Current`
2. It returns a `Task` object
3. Inside `SaveTypesAsync()`, when it hits `await` (e.g., `await _typeRepository.SaveTypesAsync()`), the continuation is queued to the captured SynchronizationContext
4. The method returns the Task → `_ =` discards it
5. **Later**, when the awaited operation completes, the CLR tries to execute the continuation via `SynchronizationContext.Post()`
6. But the UI thread is busy processing the next message (or blocked by another sync operation)
7. The continuation waits in the Dispatcher queue → never executes → Task never completes → **deadlock**
8. Worse: if `SaveTypesAsync()` calls `LoadTreeAsync()` which updates WPF UI, it needs UI thread → double deadlock

**Why `async void FireAndForget` works:**

```csharp
private static async void FireAndForget(Func<Task> taskFactory)
{
    try { await taskFactory(); }
    catch (Exception ex) { SmartConLogger.Error($"FireAndForget: {ex}"); }
}
```

1. `async void` does **NOT** return a Task
2. The C# compiler generates the state machine differently for `async void` — it creates a "top-level" async operation that runs independently
3. While individual `await`s inside still capture SynchronizationContext for their continuations, there's **no Task object waiting to complete**
4. The method fires, runs asynchronously, and when it finishes it simply notifies the SynchronizationContext (which is a no-op for Dispatcher context)
5. **No blocking wait** → no deadlock → UI thread stays free

**Key insight from Stephen Cleary:**
> "Async void methods have different composing semantics. Async methods returning Task or Task<T> can be easily composed using await, Task.WhenAny, Task.WhenAll and so on. Async methods returning void don't provide an easy way to notify the calling code that they've completed."
>
> — Stephen Cleary, MSDN Magazine, March 2013

This "no composition" property is exactly what we need in ExternalEvent: we don't want to compose or wait, we want true fire-and-forget.

### Why `_ =` doesn't solve the problem

```csharp
// Still BROKEN — Task object exists and captures context
_ = FireAndForgetAsync(() => SaveTypesAsync(id));
```

The `_ =` discard operator tells the compiler "ignore this return value", but the Task object **still exists** in memory. The CLR still tracks its completion. And the async method's continuations are still posted to the captured SynchronizationContext. The discard just means *your code* doesn't wait for it — but the Task itself is waiting for the UI thread.

### The Fix: Use async void FireAndForget

```csharp
// SAFE - true fire-and-forget, no Task capture
_externalEvent.Raise(() =>
{
    // ... Revit API calls ...
    
    // Post-operation: async void, no Task returned
    FireAndForget(() => SaveTypesAsync(id));
});

// Must be async void (NOT async Task)
private static async void FireAndForget(Func<Task> taskFactory)
{
    try { await taskFactory(); }
    catch (Exception ex) { SmartConLogger.Error($"FireAndForget: {ex}"); }
}
```

**Why it works:**
- `async void` does NOT return a Task
- No Task = no object waiting for completion via SynchronizationContext
- The async operation runs independently
- Revit UI thread is never blocked waiting for completion
- Individual `await`s inside still use SynchronizationContext for UI updates, but there's no top-level Task blocking

### When to use what

| Context | Pattern | Why |
|---|---|---|
| Inside ExternalEvent handler | `async void FireAndForget` | No Task capture, UI thread not blocked |
| Outside ExternalEvent (WPF VM) | `_ = MethodAsync()` | Standard async, caller handles errors |
| Calling async from sync in ExternalEvent | `Task.Run(() => async()).GetResult()` | Removes SynchronizationContext entirely |

### References

1. **Stephen Toub** — "ConfigureAwait FAQ", .NET Blog, December 2019
   https://devblogs.microsoft.com/dotnet/configureawait-faq/
   > "When you await a Task in C#, the compiler transforms the code to ask the awaitable for an awaiter... That awaiter is responsible for hooking up the callback... using whatever context/scheduler it captured at the time the callback was registered."

2. **Stephen Cleary** — "Don't Block on Async Code", Blog, July 2012
   https://blog.stephencleary.com/2012/07/dont-block-on-async-code.html
   > "The top-level method is blocking the context thread, waiting for GetJsonAsync to complete, and GetJsonAsync is waiting for the context to be free so it can complete."

3. **Stephen Cleary** — "Async/Await - Best Practices in Asynchronous Programming", MSDN Magazine, March 2013
   https://learn.microsoft.com/en-us/archive/msdn-magazine/2013/march/async-await-best-practices-in-asynchronous-programming
   > "Async void methods are difficult to test... but they're quite useful in one particular case: asynchronous event handlers."

## Common Mistakes

### Mistake 1: Wrapping Revit API in Task.Run
```csharp
// WRONG - Revit API must run on UI thread
var result = Task.Run(() => _loadService.LoadFamily(...)).GetAwaiter().GetResult();
// Error: "Failed to register a managed object"
```

### Mistake 2: Async lambda in ExternalEvent.Raise
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

### Mistake 4: async Task FireAndForget with _ = discard
```csharp
// WRONG - Task captures SynchronizationContext, causes freeze
private static async Task FireAndForgetAsync(Func<Task> f) { ... }
_externalEvent.Raise(() => { _ = FireAndForgetAsync(() => ...); });
```

**Why this is wrong:** The `_ =` discard operator only tells the compiler to ignore the return value. The Task object still exists, and the async method's continuations are still posted to the captured `SynchronizationContext`. The Task is "orphaned" — it's waiting for the UI thread, but nobody is waiting for the Task. Result: the async operation never completes, causing UI freeze and process hang on exit.

**Correct pattern:** Use `async void` for true fire-and-forget inside ExternalEvent:
```csharp
private static async void FireAndForget(Func<Task> f) { ... }
_externalEvent.Raise(() => { FireAndForget(() => ...); });
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

## The Science: Why Task.Run() Works

When you call `Task.Run()` from the UI thread, the delegate executes on a ThreadPool thread where `SynchronizationContext.Current` is `null` [^1]:

```csharp
// UI thread: SynchronizationContext.Current = DispatcherSynchronizationContext
// Task.Run() thread: SynchronizationContext.Current = null

var result = Task.Run(() => 
    _fileResolver.ResolveAsync(id, version, ct)).GetAwaiter().GetResult();
```

**Why this prevents deadlock:**

1. `Task.Run()` creates a new Task on `TaskScheduler.Default` (ThreadPool)
2. Inside the Task, `SynchronizationContext.Current` is null
3. When `ResolveAsync()` hits `await`, there's no SynchronizationContext to capture
4. The continuation runs on the ThreadPool, not the UI thread
5. The UI thread is blocked only briefly (waiting for Task.Run result), but the async work runs independently
6. No deadlock because the async continuation doesn't need the UI thread

**Key quote from Stephen Toub:**
> "`Task.Run` implicitly uses `TaskScheduler.Default`, which means querying `TaskScheduler.Current` inside of the delegate will also return `Default`. That means the `await` will exhibit the same behavior regardless of whether `ConfigureAwait(false)` was used."
>
> — Stephen Toub, ConfigureAwait FAQ, .NET Blog, 2019

## Why Not ConfigureAwait(false)?

`ConfigureAwait(false)` prevents a single `await` from capturing context, but it doesn't solve the fundamental problem [^1]:

1. `.GetAwaiter().GetResult()` **still blocks the UI thread**
2. If the async method calls other async methods, EVERY `await` in the chain needs `ConfigureAwait(false)`
3. Third-party libraries might not use `ConfigureAwait(false)`
4. The calling code still creates a Task that tries to return to the UI thread

**Stephen Cleary's warning:**
> "Using `ConfigureAwait(false)` to avoid deadlocks is at best just a hack. As the title of this post points out, the better solution is 'Don't block on async code'."
>
> — Stephen Cleary, "Don't Block on Async Code", 2012

`Task.Run()` is the only reliable solution because it **removes the SynchronizationContext entirely** at the entry point, rather than fighting it at every `await`.

## Summary Checklist

- [ ] Revit API calls → Direct in ExternalEvent
- [ ] SQLite/File/HTTP → Wrap in `Task.Run()`
- [ ] Post-operations → Use `FireAndForget()`
- [ ] Never use `.Result` or `.GetResult()` on async without `Task.Run()`
- [ ] Never wrap Revit API in `Task.Run()`

---

## The #3 Fatal Bug: Family Upgrade Freeze (COM/Finalizer Deadlock)

### Overview

**Bug ID:** Autodesk REVIT-237190, REVIT-236376  
**Affected Versions:** Revit 2023 < 2023.1.8, Revit 2025 < 2025.4.3, Revit 2026 < 2026.3  
**Fixed In:** Revit 2023.1.8+, Revit 2025.4.3+, Revit 2026.3+  
**Platform:** Windows 11 (primary), Windows 10 (less frequent)

### Root Cause

When `Application.OpenDocumentFile()` or `Document.LoadFamily()` triggers a **family upgrade dialog** (old .rfa version → current Revit version), Revit's COM message pump enters a corrupted state. The finalizer thread becomes permanently blocked trying to cleanup the RCW (Runtime Callable Wrapper) for the `Document` COM object.

**Why it accumulates:**
- Single upgrade dialog → COM cleanup queued to finalizer → usually succeeds
- 5+ consecutive upgrades → finalizer queue overwhelmed → possible freeze
- 10+ consecutive upgrades → **guaranteed freeze**

**Symptoms:**
1. Revit UI stutters (2-3 sec delays on every click/selection)
2. WPF render thread dies — UI controls don't redraw until mouse hover/resize (see StackOverflow #68688249)
3. Process remains in Task Manager after window close
4. Frequency: ~0.1% for single `LoadFamily`, 100% for batch `OpenDocumentFile` × 10+

**Note:** The freeze is NOT classic async deadlock (UI thread is not blocked). Revit's COM message pump is corrupted, affecting both the finalizer thread and WPF render thread. Moving the mouse or resizing the window temporarily "wakes up" the render thread, but the underlying COM corruption remains.

**Relationship to #2 Fatal Bug:** Both bugs cause UI freeze, but through different mechanisms. #2 is a classic sync-over-async deadlock (UI thread blocked waiting for Task). #3 is COM corruption in RevitMFC.dll (UI thread is free but finalizer/render threads are dead). The same family upgrade dialog can trigger EITHER symptom depending on your code pattern.

### Fix 1: Marshal.ReleaseComObject (OpenDocumentFile)

**Applies to:** `RevitFamilyDataExtractionService.Extract()` — temporary documents

```csharp
Document? familyDoc = null;
try
{
    familyDoc = app.OpenDocumentFile(rfaFilePath);
    // ... extract data ...
}
finally
{
    if (familyDoc != null)
    {
        familyDoc.Close(false);
        // FORCE immediate COM cleanup, bypass finalizer
        Marshal.ReleaseComObject(familyDoc);
    }
}
```

**Why this works:**
- `ReleaseComObject` decrements COM reference count synchronously in current thread
- COM object destroyed immediately, before finalizer ever sees it
- Prevents finalizer thread from touching corrupted RCW

**Important:** Even without explicit `GC.WaitForPendingFinalizers`, many .NET operations internally wait for the finalizer thread (e.g., `Thread.Join()`, certain lock operations, memory allocations under pressure). Once the finalizer is blocked, any such operation on the main thread will also hang Revit. `ReleaseComObject` bypasses the finalizer entirely, eliminating the root cause.

**Why NOT for LoadFamily:**
- `LoadFamily` loads `Family` into current project — persistent element
- `ReleaseComObject(family)` would crash Revit (AV) — object lifetime managed by Revit
- Workarounds for `LoadFamily`:
  - **User-paced operations** (natural pauses between clicks)
  - **Pre-converting families** before catalog import (see below)
  - **Load via temporary Document** (see workaround below)
  - Upgrading Revit to fixed version

### Workaround: Load Family via Temporary Document

If you need batch `LoadFamily` without upgrade dialog, open the .rfa in a temporary `Document` first, then load it into the target document. The temporary Document uses `ReleaseComObject`, so the upgrade dialog's COM corruption is cleaned up immediately:

```csharp
Document? famDoc = null;
try
{
    famDoc = app.OpenDocumentFile(rfaFilePath);
    
    // Load from temp document into current project
    // (upgrade dialog happens in temp doc context)
    famDoc.LoadFamily(doc, new FamilyLoadOptions());
}
finally
{
    if (famDoc != null)
    {
        famDoc.Close(false);
        Marshal.ReleaseComObject(famDoc); // ← cleanup upgrade dialog COM state
    }
}
```

**Trade-offs:**
- Slower than direct `LoadFamily` (opens document twice)
- More memory during operation
- Eliminates upgrade dialog in target document
- Must still add natural pauses or pre-convert for very large batches

### Pre-Convert Families (Batch Upgrade)

**When to use:** Catalog import of many old-version families

```csharp
Document? familyDoc = null;
try
{
    using var basicInfo = BasicFileInfo.Extract(rfaPath);
    if (!basicInfo.IsSavedInCurrentVersion)
    {
        familyDoc = app.OpenDocumentFile(rfaPath);
        var upgradedPath = Path.Combine(storagePath, $"{name}_{targetVersion}.rfa");
        familyDoc.SaveAs(upgradedPath);
        // Store upgradedPath in database
    }
}
finally
{
    if (familyDoc != null)
    {
        familyDoc.Close(false);
        Marshal.ReleaseComObject(familyDoc); // ← REQUIRED during batch conversion
    }
}
```

**Alternative:** Use Autodesk Design Automation API to batch-upgrade families server-side, avoiding the bug entirely.

**Benefits:**
- Eliminates upgrade dialog entirely during normal operations
- Faster `LoadFamily` (no conversion overhead)
- Works around bug for ALL Revit versions
- **Critical:** Must use `ReleaseComObject` during batch conversion, or the converter itself will freeze

### User-Paced Operations

**Applies to:** `LoadAndPlace` — single family selection

**Why LoadAndPlace rarely freezes:**
1. User selects one family → upgrade dialog → COM cleanup
2. User thinks, scrolls, selects next family → **natural pause**
3. During pause, finalizer catches up with queued cleanups
4. Next upgrade dialog starts with clean COM state

**The problem with ImportData:**
```csharp
// WRONG — blocks UI thread immediately after Extract
_externalEvent.Raise(() =>
{
    var result = _extractionService.Extract(rfaPath, paramNames);
    // Upgrade dialog just closed, COM cleanup pending...
    _dataImportService.SaveExtractionResultAsync(result)
        .GetAwaiter().GetResult(); // ← BLOCKS UI thread!
    // Next Extract starts before previous cleanup finishes
});
```

**Solution:** Separate Revit API from blocking work. The key is: **do NOT block UI thread after Extract**. The `Task.Delay` is optional — it gives Revit breathing room, but the real fix is `ReleaseComObject` inside `Extract`:

```csharp
// CORRECT — Extract only, queue result
_externalEvent.Raise(() =>
{
    var result = _extractionService.Extract(rfaPath, paramNames);
    _pendingResults.Enqueue((id, result, versionId));
});

// Process queue asynchronously on current thread (WPF Dispatcher)
// FireAndForget MUST be async void (see #2 Fatal Bug above)
FireAndForget(async () =>
{
    while (_pendingResults.TryDequeue(out var item))
    {
        await _dataImportService.SaveExtractionResultAsync(...);
    }
});
```

**Why this works:**
1. `Extract` uses `OpenDocumentFile` + `Close(false)` + `ReleaseComObject` → COM cleanup is synchronous
2. ExternalEvent handler returns immediately after Enqueue → UI thread is free
3. SQLite save runs in `FireAndForget` (async void) on WPF VM thread → no UI blocking
4. Even without `Task.Delay`, consecutive `Extract` calls are safe because `ReleaseComObject` forces cleanup

### Diagnostic Log Pattern

```
[FRZ] Extract: Starting OpenDocumentFile for 'ADSK_xxx.rfa'
[FRZ] Extract: OpenDocumentFile completed in 1200.5ms
[FRZ] Extract: Starting Close
[FRZ] Extract: Close completed in 50.2ms
[FRZ] Extract: Starting ReleaseComObject
[FRZ] Extract: ReleaseComObject completed, remaining refs=0, time=2.1ms
```

If freeze occurs, log stops after `Close` or `ReleaseComObject` never completes.

### COM Interop Cleanup Rules

| Scenario | Pattern | Notes |
|---|---|---|
| Temporary `Document` (OpenDocumentFile) | `Close(false)` + `Marshal.ReleaseComObject(doc)` | Force immediate cleanup |
| `Family` from `LoadFamily` | **NEVER** ReleaseComObject | Managed by Revit, persistent in project |
| `Element` references | Store `ElementId` only | Re-query via `doc.GetElement(id)` |
| `UIApplication`/`UIDocument` | Never ReleaseComObject | Owned by Revit framework |

### References

- [Autodesk: Revit 2026 hangs after upgrading a family](https://forums.autodesk.com/t5/revit-api-forum/revit-2026-hangs-after-upgrading-a-family/td-p/13613818) — **Root cause confirmed**: finalizer thread blocked forever cleaning up RCW after upgrade dialog. "The problematic function is located inside RevitMFC.dll."
- [Autodesk: Revit freezing after upgrading and importing](https://forums.autodesk.com/t5/revit-api-forum/revit-freezing-after-upgrading-and-importing-a-family-symbol/td-p/13816835) — **Windows 11 specific**: affects Revit 2023-2025, cumulative effect
- [Autodesk: Revit Freezes While Upgrading Programmatically](https://forums.autodesk.com/t5/revit-api-forum/revit-freezes-while-upgrading-programmatically-to-a-newest/td-p/13781378) — Fixed in Revit 2023.1.8+
- [Autodesk: Loading a rfa file into a document using LoadFamily() freezes Revit UI](https://forums.autodesk.com/t5/revit-api-forum/loading-a-rfa-file-into-a-document-using-loadfamily-freezes/td-p/8955088) — Ribbon freeze, WPF render thread death. "For us the only solution was converting our families in batch"
- [StackOverflow: Revit addins Window stops responding after family upgrade](https://stackoverflow.com/questions/68688249/revit-addins-window-stops-responding-after-family-upgrade) — WPF render thread freeze, not UI thread
- [The Building Coder: Upgrading Family Files Silently](https://jeremytammik.github.io/tbc/a/1183_silent_upgrade.htm) — Jeremy Tammik: `BasicFileInfo.Extract()`, ADN file updater, pre-conversion approach
- [The Building Coder: Modifying, saving and reloading families](https://jeremytammik.github.io/tbc/a/1214_mod_reload_family.htm) — `EditFamily`, `LoadFamily` with `IFamilyLoadOptions`