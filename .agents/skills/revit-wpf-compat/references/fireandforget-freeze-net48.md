---
name: revit-wpf-compat-dispatcher-freeze
description: WPF DockablePane freeze in net48 (Revit 2023) caused by FireAndForget through null Application.Current?.Dispatcher. UI is "alive" (clicks register) but doesn't redraw until right-click. Verified in production 2026-06 on ADSK_Бытовой вентилятор import flow. The fix is to capture the dispatcher in the VM ctor (which runs on the UI thread) and use _uiDispatcher.InvokeAsync from background threads.
---

# WPF DockablePane Freeze in net48 — FireAndForget + null `Application.Current?.Dispatcher`

**Affects:** Revit 2019-2024 (net48) when FamilyManager-style FireAndForget pattern updates the WPF TreeView. **Does not** affect Revit 2025-2026 (net8.0-windows) because `Application.Current` is non-null there at runtime.

**Symptoms (user-observable):**

- After triggering an import (or any FireAndForget that updates the TreeView), the **LMB (left mouse button) does not respond** on the tree — clicks register but visual state doesn't update.
- **Selection with the cursor is broken** — drag-select in the tree does not highlight.
- The freeze **resolves on right-click (RMB)** — after that, everything works normally until the next import.
- The freeze does **not** appear in Revit 2025 (net8) with the same binary.

**Logging evidence** (production `smartcon.log`, Revit 2023 net48, 2026-06-19):

```
20:36:34.757 [OpId=1a20ba3c Op=FamilyDataExt Method=Extract] RESULT: 3 named types
20:36:34.807 [DBG]  [AwaitableEvent] Execute[1]: completed (pending=0)
20:36:37.358 [DBG]  [AwaitableEvent] RaiseAsync: enqueued (pending=1)   ← user pressed Refresh
```

In the working net8 case the same flow completes in milliseconds and the tree updates immediately after save.

---

## Root cause (two-part failure)

### Part 1: `Application.Current` is null in Revit net48 add-ins

Revit plugins use `IExternalApplication`, not `System.Windows.Application`. So `Application.Current` is `null` in net48 context. See [lepoco/wpfui#662](https://github.com/lepoco/wpfui/issues/662) (June 2023) for explicit confirmation in a Revit plugin.

The naive pattern

```csharp
var dispatcher = System.Windows.Application.Current?.Dispatcher;
if (dispatcher is { HasShutdownStarted: false })
{
    _ = dispatcher.InvokeAsync(() => LoadTreeAsync());
}
```

silently **never executes the `if` body** in net48 — because `Application.Current` is null. In net8 the property is non-null, so the same code works there. That's why the bug only appears in Revit 2023 and earlier.

This is a **strict superset** of the existing rule in `SKILL.md` §"Critical: Application.Current is null in Revit" — that section warns against `Application.Current.Dispatcher`, `Application.Current.MainWindow`, etc. **It also applies to `Application.Current?.Dispatcher`** which is what the FamilyManager initial fix used.

### Part 2: `Task.Run + ConfigureAwait(false)` drops the UI SyncContext

When `FireAndForget` was refactored from `async void` to `Task.Run + ConfigureAwait(false)` (Phase 4c, I-13 anti-pattern cleanup), the new pattern is:

```csharp
private static void FireAndForget(Func<Task> taskFactory, string operationName)
{
    _ = Task.Run(async () =>
    {
        try { await taskFactory().ConfigureAwait(false); }
        catch (...) { ... }
    });
}
```

`ConfigureAwait(false)` removes the UI `SynchronizationContext`. Every `await` inside `taskFactory` (and downstream) **stays on the thread pool**. So even if Part 1 were fixed, setters like `TreeNodes = new ObservableCollection<…>(…)` would execute on a non-UI thread, leading to the WPF render thread falling behind and the right-click unfreeze pattern.

---

## Fix

Two complementary rules.

### Rule 1: Capture the dispatcher in the VM ctor (on the UI thread)

```csharp
public FamilyManagerMainViewModel(...)
{
    // ctor runs on the UI thread. Dispatcher.CurrentDispatcher is reliable here.
    // It returns the same instance as Application.Current.Dispatcher when
    // Application.Current exists (net8), and creates/returns the UI thread
    // dispatcher when Application.Current is null (net48).
    _uiDispatcher = System.Windows.Application.Current?.Dispatcher
        ?? Dispatcher.CurrentDispatcher;
}

private readonly Dispatcher _uiDispatcher;   // captured once, used everywhere
```

**Why not call `Dispatcher.CurrentDispatcher` inside FireAndForget?** Because FireAndForget runs on a **thread pool thread** (after `Task.Run`), where `Dispatcher.CurrentDispatcher` would create **a brand-new dispatcher for that thread**, not the UI one. Capturing once in the ctor is the only correct way.

See [StackOverflow: Dispatcher.CurrentDispatcher vs Application.Current.Dispatcher](https://stackoverflow.com/questions/10448987/dispatcher-currentdispatcher-vs-application-current-dispatcher) for the difference.

### Rule 2: Marshal explicitly from FireAndForget back to the UI thread

```csharp
FireAndForget(async () =>
{
    try
    {
        // ... background work (SQLite, file I/O) ...
    }
    catch (Exception ex)
    {
        SmartConLogger.Warn($"save failed: {ex.Message} [Action: нажмите Refresh]");
    }

    if (!_uiDispatcher.HasShutdownStarted)
    {
        try
        {
            await _uiDispatcher.InvokeAsync(() => LoadTreeAsync());
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"Tree reload after extract failed: {ex.Message} [Action: нажмите Refresh]");
        }
    }
}, nameof(ExtractTypesForImportedFamilies));
```

`_uiDispatcher.InvokeAsync(Func<Task>)` is the `Func<Task>` overload. The lambda `() => LoadTreeAsync()` is a method group that returns `Task` — the dispatcher returns a `DispatcherOperation<Task>` and we `await` it. Exceptions from `LoadTreeAsync` are caught via the try/catch and the action returns to the thread pool.

If you only have an `Action` (not a Task-returning method), use `() => Method()` — never `dispatcher.InvokeAsync(methodGroup)` directly, because the C# compiler will pick the `Action` overload and reject a `Task`-returning method with CS1503.

---

## Secondary rules discovered while debugging this

### 3. Never call `Application.Current?.Dispatcher` from background threads

See `SKILL.md` §"Critical: Application.Current is null in Revit" — this is a direct extension of that rule. **All four** FireAndForget call sites in `FamilyManagerMainViewModel` were affected (only the Import path was user-observable, but the bug was latent in OnPlacementCompleted, CategoryPicker.OnSearchTextChanged, CategoryTreeEditor.OnSelectedNodeChanged too).

### 4. Avoid double `LoadTreeAsync` around save

Calling `await LoadTreeAsync()` before save (for instant feedback) **and** again after save (for types) doubles the main-thread work. In net48 the WPF render thread falls behind. Pick **one** call site — the after-save one (via dispatcher) is more honest because the types are only present after the types are committed to the DB.

### 5. Minimize `Measure` / `Debug` on the UI thread

`using var _measure = SmartConLogger.Measure(...)` calls `lock + StreamWriter.WriteLine` synchronously on `Dispose` — see `transaction-callback-freeze.md` Rule 4 in the `revit-api-best-practice` skill. Keep at most **one** `Debug` in a hot path like `LoadTreeAsync` (e.g. only in `finally` for diagnostic purposes). Move the rest of the instrumentation to background threads (thread-pool `FireAndForget` is fine — that's not on the UI thread).

### 6. Don't try to "optimize" `TreeNodes = rootNodes` to `Clear() + Add()`

A first instinct is to keep the same `ObservableCollection` instance and call `Clear()` + `foreach (Add)`. In net48 WPF this **broke the display entirely** — the new family did not appear in the tree until RMB. The `PropertyChanged` from `TreeNodes = rootNodes` (with a new collection reference) is the path WPF TreeView handles reliably. Leave it alone until profiling shows it is a bottleneck.

### 7. If you must use `Application.Current` from a ViewModel, get the dispatcher from the View

In a few cases (e.g. `CategoryPickerViewModel`, `CategoryTreeEditorViewModel`) we cannot use the VM-injected `_uiDispatcher` because the VM is not the `FamilyManagerMainViewModel`. There, the partial-void setters `OnSearchTextChanged` / `OnSelectedNodeChanged` run on the UI thread (they are generated by `[ObservableProperty]`), so `Dispatcher.CurrentDispatcher` in that context is reliable:

```csharp
partial void OnSearchTextChanged(string value)
{
    var dispatcher = System.Windows.Application.Current?.Dispatcher
        ?? Dispatcher.CurrentDispatcher;   // ← fallback is safe in UI-thread setter
    if (!dispatcher.HasShutdownStarted)
    {
        _ = dispatcher.InvokeAsync(() => LoadTreeAsync());
    }
}
```

This works in both net48 and net8 because the partial void is called on the UI thread.

---

## Diagnostic recipe (when this pattern is suspected)

1. Add a `Debug` log in the FireAndForget block right after the `save complete` step that prints:
   - `Environment.CurrentManagedThreadId` (caller thread — should be thread pool, e.g. 22-60)
   - `_uiDispatcher.Thread.ManagedThreadId` (should be 1 for UI thread)
   - `same=<True/False>` comparison
2. Add a `Debug` log in the finally block of `LoadTreeAsync` that prints `thread=` and `treeNodes=`. If this line never appears, `LoadTreeAsync` was never invoked — the dispatcher captured was `null` or `HasShutdownStarted=true`.
3. Compare Revit 2025 (net8) vs Revit 2023 (net48) logs side-by-side. If the net8 log shows `LoadTreeAsync: finally` after save but the net48 log doesn't, the bug is `Application.Current == null` in net48.
4. Verify the build is actually `Debug.*` and not `Release` — see the `smartcon-logging` skill, §"Debug.* configurations are NOT Debug". Without the `Directory.Build.props` block, every `Debug(...)` call is silently dropped.

---

## Verified outcome

After the fix:

| Scenario | Before | After |
|---|---|---|
| Revit 2023 (net48), 3 imports in a row | Freeze on first import, RMB unfreezes, types don't appear | 3/3 successful, types appear, no freeze |
| Revit 2025 (net8), single + batch import + DropFamily + EditFamily + ImportActiveFile | OK | OK (unchanged) |
| R25/R24/R21/R19 build | 0 warnings, 0 errors | 0 warnings, 0 errors |
| 1312 unit tests | pass | pass |

`LoadTreeAsync: finally` line is present in the log after every save on both Revit versions. The dispatcher marshalling path is now:

```
ExtractTypesForImportedFamilies: save complete on thread 22, _uiDispatcher thread=1, HasShutdownStarted=False
ExtractTypesForImportedFamilies: about to dispatcher.InvokeAsync(LoadTreeAsync) — caller thread=22, dispatcher thread=1, same=False
LoadTreeAsync: finally thread=1 treeNodes=2 treeRef=52330689
ExtractTypesForImportedFamilies: dispatcher.InvokeAsync(LoadTreeAsync) returned on thread 40
```

---

## Sources (Exa)

- [lepoco/wpfui#662 — `Application.Current` will be null in net48 Revit plugins](https://github.com/lepoco/wpfui/issues/662) — explicit confirmation in a Revit plugin scenario.
- [lepoco/wpfui#837 — `UiApplication.Current` workaround](https://github.com/lepoco/wpfui/issues/837) — alternative `UiApplication.Current` approach.
- [StackOverflow: `Application.Current` and `App.Current` is null](https://stackoverflow.com/questions/39644256/application-current-and-app-current-is-null) — general pattern + `MainWindow.AppWindow.Dispatcher` workaround.
- [StackOverflow: `Dispatcher.CurrentDispatcher` vs `Application.Current.Dispatcher`](https://stackoverflow.com/questions/10448987/dispatcher-currentdispatcher-vs-application-current-dispatcher) — the difference between the two ways to get a dispatcher.
- [Autodesk Community: WPF DockablePane UI freezes when loading a family via ExternalEvent](https://forums.autodesk.com/t5/revit-api-forum/wpf-dockablepane-ui-freezes-when-loading-a-family-via/td-p/14021600) — similar symptoms, different trigger.
- `revit-api-best-practice` skill, `references/transaction-callback-freeze.md` — right-click unfreeze pattern + `Measure.Dispose` I/O on UI thread.
- `revit-api-best-practice` skill, `references/wpf-mfc-render-freeze.md` — WPF render thread freeze after `PropertyChanged` before MFC dialog.
- `revit-api-best-practice` skill, `references/async-threading-patterns.md` — `ConfigureAwait` semantics and deadlock prevention.
- [StackOverflow: UI slow at updating `ObservableCollection<T>` in TreeView control](https://stackoverflow.com/questions/6252839/ui-slow-at-updating-observablecollectiont-in-treeview-control) — why `Clear() + Add()` is not a free optimization.
- [Microsoft Learn: Improve the performance of a TreeView](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/how-to-improve-the-performance-of-a-treeview) — `VirtualizingStackPanel.IsVirtualizing` for future optimization if needed.

---

## Internal references

- `docs/adr/031-fireandforget-ui-marshalling.md` — full ADR with all 5 rules, the reverted `Clear() + Add()` experiment, and the before/after code listing.
- `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.cs:60,158-167` — `_uiDispatcher` field + ctor capture.
- `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Import.cs:226-274` — FireAndForget block with explicit `dispatcher.InvokeAsync`.
- `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.cs:605-625` — `OnPlacementCompleted` using `_uiDispatcher`.
- `src/SmartCon.FamilyManager/ViewModels/CategoryPickerViewModel.cs:115-121` and `CategoryTreeEditorViewModel.cs:65-82` — partial-void setters with UI-thread `Dispatcher.CurrentDispatcher` fallback.
- Commit `5fb1689` on `develop` — the fix.
- Commit `ab443c8` on `develop` — the initial FireAndForget UI-marshalling fix that exposed the net48-only bug.
