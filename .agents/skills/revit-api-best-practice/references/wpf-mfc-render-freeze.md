# WPF Render Thread Freeze from PropertyChanged Before MFC Dialog

## Symptom

WPF DockablePane or modeless dialog stops redrawing after a family upgrade dialog appears and closes. The UI is **not frozen** in the traditional sense:

- You can drag the window around
- Clicks are processed (links open external apps)
- But controls don't update on hover, click, or property change
- Window resize temporarily "wakes up" the render thread
- Process closes normally (unlike COM/finalizer deadlock)

**Critical distinction:** This is a **WPF render thread zombie state**, NOT a thread deadlock. The render thread is stuck waiting for the UI thread, which was blocked by an MFC modal dialog.

> "This is NOT a frozen UI issue or a locked thread. I can drag the window around, and if I click on any of the links in the UI that are supposed to open web pages or external sources the external sources open, so it is actually processing clicks, etc. However, when I do something that should make changes to the UI (like mouse over a button or try to click a filtering button) nothing changes."
>
> — StackOverflow: [Revit addins Window stops responding after family upgrade](https://stackoverflow.com/questions/68688249), user sfaust, 2021

---

## Root Cause

**WPF render thread deadlock** caused by the interaction between:
1. **PropertyChanged** in WPF thread (before ExternalEvent)
2. **MFC modal dialog** (family upgrade dialog from `LoadFamily`/`OpenDocumentFile`)
3. **STA thread architecture** (single-threaded apartment for both WPF and Revit)

### The Sequence

```
[WPF Thread] StatusMessage = string.Empty
    ↓ PropertyChanged event
[WPF Thread] TextBlock.Text updates → Layout pass queued
    ↓ WPF Render Thread starts processing layout
[Render Thread] Waiting for UI thread to complete layout...
    ↓ ExternalEvent.Raise() queues work to Revit UI thread
[Revit UI Thread] Execute ExternalEvent handler
[Revit UI Thread] doc.LoadFamily() → MFC Upgrade Dialog (#32770)
    ↓ MFC dialog blocks UI thread (STA apartment rule)
[Render Thread] Still waiting for UI thread → NEVER RESUMES
    ↓ MFC dialog closes, UI thread continues
[Render Thread] Zombie state — doesn't know dialog closed
```

**Why it happens:** WPF's render thread synchronizes with the UI thread at specific points during layout. When the UI thread is blocked by an MFC modal dialog, the render thread's synchronization request is never answered. Unlike normal WPF dialogs (which pump messages), MFC dialogs don't cooperate with WPF's message loop.

> "So it seems like this is some type of redraw issue and it seems to be triggered by the family upgrade dialog."
>
> — Autodesk Community: [Addin WPF Window Stops Responding after Family Upgrade](https://forums.autodesk.com/t5/revit-api-forum/addin-wpf-window-stops-responding-after-family-upgrade/td-p/10559303), 2021

---

## Trigger Conditions

| Condition | Required? | Explanation |
|---|---|---|
| `PropertyChanged` in WPF thread | **YES** | Must fire BEFORE `ExternalEvent.Raise()` |
| Property bound to UI element | **YES** | `IsLoading` without XAML binding is safe; `StatusMessage` bound to `TextBlock` is NOT |
| MFC dialog from Revit API | **YES** | Family upgrade dialog (`#32770`) is the known trigger |
| `FireAndForget(async)` before Raise | Common pattern | Creates the async context where PropertyChanged fires before Raise |
| STA thread | Implicit | Both WPF and Revit run on the same STA thread |

**Affected APIs that trigger MFC dialog:**
- `Document.LoadFamily()` — when `.rfa` version < current Revit version
- `Application.OpenDocumentFile()` — when file needs upgrade
- `Family.Load()` — similar family upgrade path

---

## Anti-Pattern (DO NOT USE)

```csharp
[RelayCommand]
private void ImportData()
{
    IsLoading = true;
    StatusMessage = string.Empty;  // ← PropertyChanged in WPF thread!
    
    FireAndForget(async () =>  // ← async before ExternalEvent
    {
        var prepareResult = await _dataImportService.PrepareExtractionAsync(...);
        
        _externalEvent.Raise(() =>
        {
            var result = _extractionService.Extract(rfaPath);  // ← MFC dialog
            // ...
        });
    });
}
```

**Why this fails:**
1. `StatusMessage = string.Empty` fires `PropertyChanged` in WPF thread
2. WPF render thread begins layout update for `TextBlock`
3. `FireAndForget(async)` yields control to WPF message pump
4. Render thread tries to synchronize with UI thread → UI thread is busy
5. `ExternalEvent.Raise()` eventually executes on Revit UI thread
6. `Extract()` calls `OpenDocumentFile()` → MFC upgrade dialog blocks UI thread
7. Render thread's synchronization is stuck forever

---

## Correct Pattern (USE THIS)

```csharp
[RelayCommand]
private void ImportData()
{
    IsLoading = true;  // ← Safe: IsLoading has no XAML binding
    
    // NO PropertyChanged here! NO async before ExternalEvent!
    _externalEvent.Raise(() =>
    {
        try
        {
            StatusMessage = string.Empty;  // ← Safe: inside Revit UI thread
            
            // ThreadPool: SQLite/async operations
            var prepareResult = Task.Run(() => 
                _dataImportService.PrepareExtractionAsync(...)).GetAwaiter().GetResult();
            
            // UI thread: Revit API (may show MFC upgrade dialog)
            var result = _extractionService.Extract(prepareResult.ResolvedFilePath);
            
            // ThreadPool: save results
            var saveResult = Task.Run(() =>
                _dataImportService.SaveExtractionResultAsync(...)).GetAwaiter().GetResult();
            
            StatusMessage = $"Imported: {saveResult.TypesCount} types";
            IsLoading = false;
            
            // Post-processing: FireAndForget ONLY for UI updates AFTER Revit API
            FireAndForget(() => LoadTreeAsync());
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            IsLoading = false;
        }
    });
}
```

**Why this works:**
1. No `PropertyChanged` fires in WPF thread before `ExternalEvent.Raise()`
2. All UI updates (`StatusMessage`, `IsLoading`) happen inside `ExternalEvent` handler
3. WPF render thread is idle during MFC dialog → no synchronization deadlock
4. `FireAndForget` is used ONLY after Revit API completes (for non-critical UI refresh)

---

## Comparison with LoadToProject (Working Pattern)

`LoadToProject` never freezes because it follows the correct pattern:

```csharp
[RelayCommand]
private void LoadToProject()
{
    // NO IsLoading = true (CanLoadToProject handles button state)
    // NO StatusMessage change before Raise!
    
    _externalEvent.Raise(() =>
    {
        var resolved = Task.Run(() => 
            _fileResolver.ResolveForLoadAsync(...)).GetAwaiter().GetResult();
        
        var result = _loadService.LoadFamily(resolved, options);  // ← MFC dialog
        
        StatusMessage = $"Loaded: {result.FamilyName}";  // ← After dialog
        CanPlace = true;
        
        FireAndForget(() => _usageRepo.RecordUsageAsync(...));
    });
}
```

**Key differences from broken ImportData:**
| Aspect | LoadToProject (Working) | ImportData (Broken) |
|---|---|---|
| PropertyChanged before Raise | ❌ None | ✅ `StatusMessage = string.Empty` |
| Async context before Raise | ❌ None | ✅ `FireAndForget(async)` |
| All UI updates | Inside ExternalEvent | Split: WPF thread + ExternalEvent |
| Pattern | Sync ExternalEvent + sync SQLite | Async WPF + async SQLite + TCS |

---

## Key Rules

### Rule 1: PropertyChanged Only Inside ExternalEvent

```csharp
// WRONG — PropertyChanged in WPF thread before ExternalEvent
StatusMessage = "Loading...";  // ← Triggers layout → render thread busy
_externalEvent.Raise(() => { doc.LoadFamily(...); });  // ← MFC dialog blocks UI thread

// CORRECT — PropertyChanged inside ExternalEvent handler
_externalEvent.Raise(() =>
{
    StatusMessage = "Loading...";  // ← Safe: UI thread blocked anyway
    doc.LoadFamily(...);
});
```

### Rule 2: No async/FireAndForget Before ExternalEvent with MFC Dialog

```csharp
// WRONG — async yields control, creates timing window for render deadlock
FireAndForget(async () =>
{
    await PrepareAsync();
    _externalEvent.Raise(() => { LoadFamily(...); });
});

// CORRECT — direct ExternalEvent.Raise(), all async inside via Task.Run
_externalEvent.Raise(() =>
{
    var data = Task.Run(() => PrepareAsync()).GetAwaiter().GetResult();
    LoadFamily(data);
});
```

### Rule 3: IsLoading Without XAML Binding is Safe

```csharp
// Safe: IsLoading is [ObservableProperty] but NOT bound in XAML
IsLoading = true;  // PropertyChanged fires, but no UI element updates

// Dangerous: StatusMessage is bound to TextBlock in XAML
StatusMessage = string.Empty;  // PropertyChanged → TextBlock.Text → layout pass
```

Check your XAML:
```xml
<!-- This makes StatusMessage dangerous before ExternalEvent -->
<TextBlock Text="{Binding StatusMessage}" />

<!-- IsLoading is safe only if not used in XAML -->
<!-- (or used only in code-behind, not bindings) -->
```

---

## Related Bugs (Different Mechanisms)

| Bug | Symptom | Mechanism | Fix |
|---|---|---|---|
| **This bug** | UI alive, no redraw after upgrade dialog | WPF render thread zombie from PropertyChanged | Move PropertyChanged inside ExternalEvent |
| [COM/Finalizer Deadlock](async-threading-patterns.md#family-upgrade-freeze-bug) | Process hangs on exit, lags 2-3s | Finalizer thread blocked by corrupted RCW | `Marshal.ReleaseComObject(doc)` |
| [Transaction Callback Freeze](transaction-callback-freeze.md) | Freeze on button click, unfreezes on next click | I/O blocking inside transaction callback | No I/O inside `RunInTransaction` |
| [Record `with` Freeze](record-with-freeze.md) | Freeze in loop with `record with` | JIT compilation blocks STA thread | Use LINQ `Select` or constructor |

**Important:** The same family upgrade dialog can trigger ANY of these symptoms depending on your code pattern. If you see UI freeze after family operations, check all four causes.

---

## References

### Primary Sources

1. **StackOverflow — Revit addins Window stops responding after family upgrade**
   https://stackoverflow.com/questions/68688249
   > "This is NOT a frozen UI issue or a locked thread. I can drag the window around... but when I do something that should make changes to the UI... nothing changes."
   
   — Confirms WPF render thread zombie (not deadlock). MFC upgrade dialog is the trigger.

2. **Autodesk Community — Addin WPF Window Stops Responding after Family Upgrade**
   https://forums.autodesk.com/t5/revit-api-forum/addin-wpf-window-stops-responding-after-family-upgrade/td-p/10559303
   > "So it seems like this is some type of redraw issue and it seems to be triggered by the family upgrade dialog."
   
   — Autodesk forum confirmation. Multiple developers report same pattern.

3. **Autodesk Community — WPF DockablePane UI freezes when loading a family via ExternalEvent**
   https://forums.autodesk.com/t5/revit-api-forum/wpf-dockablepane-ui-freezes-when-loading-a-family-via/td-p/14021600
   > "The user clicks the Insert button inside a WPF UserControl hosted in a DockablePane... Inside IExternalEventHandler.Execute(), the plugin opens a Transaction, calls Document.LoadFamily()..."
   
   — 2026 report. Same pattern: WPF DockablePane + ExternalEvent + LoadFamily = freeze.

### Secondary Sources

4. **Autodesk Community — Loading a rfa file into a document using LoadFamily() freezes Revit UI**
   https://forums.autodesk.com/t5/revit-api-forum/loading-a-rfa-file-into-a-document-using-loadfamily-freezes/td-p/8955088
   > "This problem can cause the ribbon UI to freeze until manually moving the mouse to any ribbon button or just resizing the revit window."
   
   — Earlier report (2019). Resizing window "wakes up" render thread — confirms render thread zombie.

5. **Microsoft Learn — WPF Render Thread Failures**
   https://learn.microsoft.com/en-us/troubleshoot/developer/dotnet/framework/general/wpf-render-thread-failures
   > "Exceptions and situations in which the software stops responding occur in a UI thread if the WPF render thread experiences a fatal error."
   
   — Microsoft documentation on render thread failures. Generic symptoms match our case.

6. **The Building Coder — DevDay Conference in Munich and WPF DoEvents**
   https://blog.autodesk.io/devday-conference-in-munich-and-wpf-doevents/
   > "The workarounds involved stuff like setting the window focus... and allowing the WPF form to access the Windows message queue."
   
   — Jeremy Tammik blog. Focus/DoEvents workarounds don't fix root cause (our fix is structural).

---

## Diagnostic Checklist

If you see UI freeze after family operations:

- [ ] **Process closes normally?** Yes → likely THIS bug (render thread). No → [COM/finalizer deadlock](async-threading-patterns.md#family-upgrade-freeze-bug).
- [ ] **Mouse moves, clicks processed?** Yes → THIS bug. No → classic deadlock.
- [ ] **Window resize temporarily fixes it?** Yes → THIS bug (wakes render thread).
- [ ] **Any PropertyChanged before ExternalEvent.Raise()?** Yes → THIS bug. Check StatusMessage, IsLoading, etc.
- [ ] **Any `FireAndForget(async)` before ExternalEvent?** Yes → THIS bug. Replace with sync ExternalEvent + `Task.Run().GetResult()`.
- [ ] **Freeze inside transaction callback?** Yes → [Transaction Callback Freeze](transaction-callback-freeze.md).
- [ ] **Freeze in loop with `record with`?** Yes → [Record `with` Freeze](record-with-freeze.md).

---

## Summary

```
WPF Thread (before ExternalEvent)
├── SAFE: IsLoading = true (no XAML binding)
├── SAFE: CanExecute changes (no layout impact)
└── DANGEROUS: StatusMessage = ... (TextBlock binding → layout → render thread)

ExternalEvent Handler (Revit UI thread)
├── SAFE: All PropertyChanged here
├── SAFE: StatusMessage = ... (inside handler)
├── SAFE: doc.LoadFamily() (MFC dialog blocks UI thread, but render thread is idle)
└── SAFE: Task.Run(() => asyncWork).GetResult() (SQLite on ThreadPool)

Post-Processing (after Revit API)
└── SAFE: FireAndForget(() => LoadTreeAsync()) (async void, no Task capture)
```

**The Golden Rule:** Never trigger WPF layout (PropertyChanged on bound properties) in the WPF thread immediately before an `ExternalEvent.Raise()` that may show an MFC modal dialog. Move all UI updates inside the `ExternalEvent` handler.
