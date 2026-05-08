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