# Known Bug Patterns (net48-only)

Detailed record of bugs encountered during SmartCon development. Each entry: symptom, root cause, fix, affected files.

---

## BUG-001: Dialog windows crash on open (net48)

**Symptom:** Clicking "Field Library" or "Parse Rule" button in ShareSettings causes NullReferenceException: "Object reference not set to an instance of an object." Works perfectly in Revit 2025 (net8).

**Root cause:** `GetOwnerWindow()` in ShareSettingsViewModel accessed `Application.Current.Windows` without null check. In Revit's net48 context, `Application.Current` is null because the plugin uses `IExternalApplication`, not `System.Windows.Application`. Net8 runtime auto-creates Application instance, masking the bug.

**Fix:**
1. Added `_ownerWindow` field to ViewModel, set via `SetOwnerWindow(this)` in View constructor
2. `GetOwnerWindow()` returns `_ownerWindow` first, then falls back to `Application.Current?.Windows` (null-safe)

```csharp
// Before (crashes)
return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

// After (works)
if (_ownerWindow is not null) return _ownerWindow;
return Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
```

**Files:** `ShareSettingsViewModel.cs`, `ShareSettingsView.xaml.cs`, `ShareProjectCommand.cs`

---

## BUG-002: Progress bar freezes and never closes (net48)

**Symptom:** ShareProject progress bar appears but never updates progress, never closes, stays on screen even after operation completes. In Revit 2025 everything works.

**Root cause:** `ReportProgress()` and `CloseProgress()` used `Application.Current?.Dispatcher.Invoke(...)`. Since `Application.Current` is null in net48, the `?.` operator short-circuits — lambda never executes. Window was shown but updates/closure silently skipped.

**Fix:** Replace `Application.Current?.Dispatcher` with `_progressView.Dispatcher` — every WPF window has its own Dispatcher independent of Application.Current.

```csharp
// Before (silently skipped)
Application.Current?.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { ... }));

// After (works)
_progressView.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { ... }));
```

**Files:** `ShareProjectCommand.cs:452-473`

---

## BUG-003: Localization strings not displaying (net48)

**Symptom:** DynamicResource keys like `{DynamicResource PM_Title_FieldLibrary}` show as empty text in Revit 2023, but display correctly in Revit 2025.

**Root cause:** Resource dictionary loading order issue. In net48, `LanguageManager.EnsureWindowResources(this)` needs to merge the string dictionary into window resources BEFORE XAML evaluates DynamicResource bindings.

**Fix:** Ensure `LanguageManager.EnsureWindowResources(this)` is called in every dialog constructor, and the localization dictionary is merged at window level (not relying on Application.Current.Resources).

**Pattern:** Every View constructor must call:
```csharp
public MyView(MyViewModel vm)
{
    InitializeComponent();
    LanguageManager.EnsureWindowResources(this);  // Required for net48
    DataContext = vm;
}
```

---

## BUG-004: Revit hard crash on dialog open (net48)

**Symptom:** Revit crashes completely (not just error message) when opening child dialogs. No try-catch in the calling code to show error gracefully.

**Root cause:** Unhandled NullReferenceException from BUG-001 propagated to Revit's native layer.

**Fix:**
1. Root cause fix from BUG-001 (null-safe `GetOwnerWindow`)
2. Added try-catch with logging around dialog opening code
3. `OnUserInitiatedClose` simplified to just `CustomDialogResult = false` (removed `vm.CancelCommand.Execute(null)` which could also NRE)

```csharp
// Safe dialog opening pattern
try
{
    var vm = new MyViewModel();
    var view = new MyView(vm) { Owner = GetOwnerWindow() };
    view.ShowDialog();
}
catch (Exception ex)
{
    SmartConLogger.Error($"[PM] Dialog failed: {ex}");
    MessageBox.Show($"Error:\n{ex.Message}\n\n{ex.StackTrace}", "Error", ...);
}
```

**Files:** `ShareSettingsViewModel.cs`, all dialog Views (FieldLibraryView, ParseRuleView, AllowedValuesView, ExportNameDialog)

---

## BUG-005: string.Contains(string, StringComparison) not available (net48)

**Symptom:** Compilation error CS1061 on net48: `'string' does not contain a definition for 'Contains' accepting 3 arguments`.

**Root cause:** `string.Contains(string, StringComparison)` was added in .NET 5 / net5.0. Not available in net48.

**Fix:** Use `#if NETFRAMEWORK` with `ToLowerInvariant()` fallback.

```csharp
#if NETFRAMEWORK
    var found = text.ToLowerInvariant().Contains(search.ToLowerInvariant());
#else
    var found = text.Contains(search, StringComparison.OrdinalIgnoreCase);
#endif
```

---

## BUG-006: ComboBox selection resets in DataGrid (net48/WPF)

**Symptom:** When changing a ComboBox value in DataGrid, the selection resets to previous value. Happens with `DataGridComboBoxColumn`.

**Root cause:** `DataGridComboBoxColumn` in WPF has known issues with binding context and item source refresh. The column creates separate binding contexts for editing and display templates.

**Fix:** Replace `DataGridComboBoxColumn` with `DataGridTemplateColumn` containing TextBlock (display) + ComboBox (editing):

```xml
<DataGridTemplateColumn>
    <DataGridTemplateColumn.CellTemplate>
        <DataTemplate>
            <TextBlock Text="{Binding Field}" HorizontalAlignment="Center"/>
        </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
    <DataGridTemplateColumn.CellEditingTemplate>
        <DataTemplate>
            <ComboBox ItemsSource="{Binding DataContext.FieldNames, RelativeSource={RelativeSource AncestorType=DataGrid}}"
                      Text="{Binding Field, UpdateSourceTrigger=PropertyChanged}" IsEditable="True"/>
        </DataTemplate>
    </DataGridTemplateColumn.CellEditingTemplate>
</DataGridTemplateColumn>
```

---

## BUG-007: DragDrop.DoDragDrop crashes Revit on net48

**Symptom:** DataGrid row drag & drop works on net8 but crashes Revit completely on net48 when user starts dragging a row.

**Root cause:** Unhandled exceptions in drag/drop event handlers propagate to Revit's native layer and crash the process. Common triggers:
1. `e.OriginalSource as DependencyObject` can be null (not all visual sources are DependencyObject)
2. `VisualTreeHelper.GetParent()` can return null at visual tree root
3. Any exception during `DragDrop.DoDragDrop` is unhandled and fatal in Revit context

**Fix:** Wrap ALL drag/drop handlers in try-catch with logging. Never trust `e.OriginalSource` without null check:

```csharp
private void Grid_PreviewMouseMove(object sender, MouseEventArgs e)
{
    // ... threshold check ...
    var src = e.OriginalSource as DependencyObject;
    if (src == null) return;
    var row = FindAncestor<DataGridRow>(src);
    if (row == null) return;

    try
    {
        row.Opacity = 0.4;
        var data = new DataObject(DataFormats.Serializable, sourceIndex);
        DragDrop.DoDragDrop(grid, data, DragDropEffects.Move);
    }
    catch (Exception ex)
    {
        SmartConLogger.Warn($"[PM] DragDrop failed: {ex.Message}");
    }
    finally
    {
        try { row.Opacity = 1.0; } catch { }
        _isDragging = false;
    }
}

private void Grid_Drop(object sender, DragEventArgs e)
{
    try { /* drop logic */ }
    catch (Exception ex) { SmartConLogger.Warn($"[PM] Drop failed: {ex.Message}"); }
}
```

**Files:** `ShareSettingsView.xaml.cs`

---

## BUG-008: UseWindowsForms=true causes WPF/WinForms type ambiguity

**Symptom:** Compilation errors CS0104 "ambiguous reference between System.Windows.X and System.Windows.Forms.X" for Point, ComboBox, Button, DragDrop, DataObject, MouseEventArgs, DependencyObject, etc.

**Root cause:** When `<UseWindowsForms>true</UseWindowsForms>` is set in .csproj (needed for FolderBrowserDialog), both WPF and WinFX namespaces are imported via implicit usings, causing conflicts.

**Fix:** Use `using` aliases at the top of the file to disambiguate:

```csharp
using DependencyObject = System.Windows.DependencyObject;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDrop = System.Windows.DragDrop;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
// ... etc for all conflicting types
```

**Files:** Any .xaml.cs file in SmartCon.ProjectManagement that uses WPF drag/drop or input types

---

## Pre-commit checklist

Before committing WPF-related code, verify:

- [ ] No `Application.Current.` without `?.` (and preferably use window's own Dispatcher)
- [ ] No `Application.Current.Windows` without null-conditional AND explicit owner fallback
- [ ] All dialogs call `LanguageManager.EnsureWindowResources(this)` in constructor
- [ ] All sub-dialogs use `SetOwnerWindow(this)` pattern
- [ ] No `string.Contains(string, StringComparison)` without `#if NETFRAMEWORK` guard
- [ ] DataGrid ComboBox columns use `DataGridTemplateColumn`, not `DataGridComboBoxColumn`
- [ ] Build passes on both `Debug.R25` (net8) and `Debug.R24` (net48)
- [ ] `OnUserInitiatedClose` does NOT call ViewModel commands (just sets `CustomDialogResult = false`)
- [ ] All DragDrop event handlers wrapped in try-catch (net48 crash prevention)
- [ ] `e.OriginalSource as DependencyObject` always null-checked before use
- [ ] UseWindowsForms ambiguity resolved via `using` aliases
- [ ] Every `MenuItem` inside a `ContextMenu` that has a bound `CommandParameter` carries `behaviors:MenuItemCommandParameterRequery.RequeryOnChange="True"` (see BUG-009)
- [ ] `SingletonResources` never sits as a sibling of other resources in an implicit `<Window.Resources>` — always explicit `<ResourceDictionary>` + `MergedDictionaries` (see BUG-010)

---

## BUG-009: ContextMenu MenuItem greyed out in net48 only (dotnet/wpf#4078)

**Symptom:** A `MenuItem` inside a `ContextMenu` is **permanently greyed out** in Revit 2019-2024 (net48) but works perfectly in Revit 2025-2026 (net8.0-windows). Other `MenuItem`s in the same `ContextMenu` (those without a bound `CommandParameter`, or with a static literal like `CommandParameter="Image"`) are unaffected. First click after a fresh app start may work once, but subsequent right-clicks keep the menu item disabled.

**Diagnostic shape in `smartcon.log`:**

```text
[DBG]  RightClickSelect: setting IsSelected=true on TreeViewItem (DataContext type=CategoryNodeViewModel, hash=…).
[DBG]  CanCheckCategory: result=False, category=<null>, paramType=<null>, IsStaleCheckInProgress=False    ← BUG
[DBG]  CanCheckCategory: result=True,  category=<guid>, paramType=CategoryNodeViewModel, IsStaleCheckInProgress=False   ← expected in net8, MISSING in net48
```

The first `CanExecute` call passes `null` because WPF has not yet resolved the `CommandParameter` binding. In .NET Core 3.1+ a second `CanExecute` call follows once the binding resolves; in .NET Framework 4.8 that second call never happens.

**Root cause:** This is `dotnet/wpf#4078` ("MenuItem: CommandParameters are ignored"), originally reported as #316 in 2008 and tracked through #3452 and #4472. The fix landed in PR #4217 (merged 2022-07-21) which adds a `PropertyChangedCallback` to `MenuItem.CommandParameterProperty` calling `item.UpdateCanExecute()` when the parameter is set. That fix is in .NET Core 3.1+ / .NET 5+ / .NET 6+ / .NET 7+ / .NET 8+, but **was never backported to .NET Framework 4.x**.

**Why `CommandManager.InvalidateRequerySuggested()` does NOT help:** already-shown `ContextMenu` items cache their `CanExecute` state and stop re-evaluating after the first show. Verified empirically on 2026-06-19 (production log) and confirmed in [StackOverflow 37988297](https://stackoverflow.com/questions/37988297/canexecute-not-raised-when-context-menu-opens).

**Fix:** use the `MenuItemCommandParameterRequery` attached property at `src/SmartCon.UI/Behaviors/MenuItemCommandParameterRequery.cs`. It hooks into `MenuItem.CommandParameter` changes via `DependencyPropertyDescriptor` and briefly null-and-reassigns `MenuItem.Command` — this triggers WPF's internal `OnCommandChanged` → `UpdateCanExecute()` path, which is identical in effect to the upstream `PropertyChangedCallback` from PR #4217.

```xml
<MenuItem Header="{loc:Loc FM_Check}"
          CommandParameter="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource AncestorType=ContextMenu}}"
          Command="{Binding CheckCategoryCommand}"
          behaviors:MenuItemCommandParameterRequery.RequeryOnChange="True" />
```

`MenuItem.UpdateCanExecute` is `internal` in WPF, so we cannot subclass `MenuItem` and call it from another assembly — the null/reassign hack is the next cleanest option. Order properties as `CommandParameter` BEFORE `Command` in XAML for clarity (it is not load-bearing once the attached property is in place, but matches the order PR #4217 effectively produces at runtime).

**Files:** `src/SmartCon.UI/Behaviors/MenuItemCommandParameterRequery.cs` (new); `src/SmartCon.FamilyManager/Views/FamilyManagerPaneControl.xaml` (4 `MenuItem`s updated: `CheckCategoryCommand`, `CheckFamilyCommand`, `UpdateCategoryKeepParamsCommand`, `UpdateCategoryOverwriteParamsCommand`); `src/SmartCon.UI/Behaviors/TreeViewBehaviors.cs` (RightClickSelect now logs the hit `DataContext` type/hash, and the comment block above `OnTreeViewPreviewMouseRightButtonDown` documents the WPF bug + WPF fix + our hardening).

**Sources:**
- `https://github.com/dotnet/wpf/issues/4078` — original report
- `https://github.com/dotnet/wpf/issues/316` — 2008 ancestor
- `https://github.com/dotnet/wpf/pull/4217` — the fix (not in net48)
- `https://stackoverflow.com/questions/335849` — workaround: swap XAML order
- `https://stackoverflow.com/questions/3027224` — two known ContextMenu `CanExecute` bugs

### Why the workaround is NOT enough — do NOT unsubscribe on `MenuItem.Unloaded`

A second iteration of this bug appeared as Issue #78 (2026-06-23): after the **first** `ContextMenu` show, every subsequent right-click in the same Revit session left the Stale-Check "Проверить" menu item greyed out in Revit 2023 (net48). The previous workaround appeared to work in isolation but silently disabled itself in long-running sessions.

**Root cause of the regression:** commit `8ffe965` ("feat(FamilyManager): bake-in Type Catalog into managed .rfa at import", 2026-06-22) added an `OnMenuItemUnloaded` handler in `MenuItemCommandParameterRequery` that called `DependencyPropertyDescriptor.RemoveValueChanged(...)` on `MenuItem.CommandParameter` when the `MenuItem` left the visual tree. The intent was to "prevent accumulated handlers across many right-click cycles in long Revit sessions" — but the assumption was wrong on two counts:

1. **WPF reuses `MenuItem` instances across `ContextMenu` shows.** The same `MenuItem` instance is detached from the visual tree when the ContextMenu closes and re-attached when it opens again. The `OnMenuItemUnloaded` cleanup therefore runs on every close, leaving no subscription for the next show.
2. **The `RequeryOnChange` `PropertyChangedCallback` only fires when the value actually changes.** Since the BAML declaration sets `RequeryOnChange="True"` statically, the `OnRequeryOnChangeChanged` handler is invoked **exactly once** per `MenuItem` instance lifetime — never again on subsequent ContextMenu shows. After the first `Unloaded`, the workaround is gone for good.

The `OnMenuItemUnloaded` was over-engineering for a non-existent problem: `DependencyPropertyDescriptor.AddValueChanged` does store handlers in a static `EventHandlerList` keyed by component (see [StackOverflow 6780159](https://stackoverflow.com/questions/6780159)), but in this codebase `OnRequeryOnChangeChanged` runs at most once per `MenuItem` instance, so accumulation cannot happen in practice.

**Why the bug only manifested in net48 (Revit 2023), not net8 (Revit 2025):** in net8 WPF the upstream `PropertyChangedCallback` from dotnet/wpf#4217 calls `MenuItem.UpdateCanExecute()` on every `CommandParameter` change regardless of whether our workaround subscription is alive. So when our handler is unsubscribed on `Unloaded`, net8 still re-evaluates `CanExecute` correctly. In net48, our workaround is the only thing keeping `CanExecute` fresh — once it is gone, `CanExecute` stays stuck with whatever stale value it last saw.

**Fix:** do not subscribe to `MenuItem.Unloaded` at all in `MenuItemCommandParameterRequery`. The current implementation in `src/SmartCon.UI/Behaviors/MenuItemCommandParameterRequery.cs` simply calls `AddValueChanged` on the first `OnRequeryOnChangeChanged(true)` invocation and never tears it down. The XML-doc on the type now records this requirement explicitly.

**Diagnostic recipe for this regression:**
1. Add a `[DBG]` line inside `OnCommandParameterChanged` that logs `RuntimeHelpers.GetHashCode(menuItem)` and the current `CommandParameter` value.
2. Open and close the same ContextMenu twice in a row in Revit 2023.
3. If you see `OnCommandParameterChanged` fire on the first show with the correct `CommandParameter` type, but **never** on the second show despite the same MenuItem identity hash, the workaround has been unsubscribed and you have hit this regression.
4. Re-introducing `MenuItem.Unloaded += ...` (as commit `8ffe965` did) will reproduce it; removing that subscription restores the workaround.

**Lessons for future WPF ContextMenu work:**
- Never treat `MenuItem.Unloaded` as a cleanup point for handlers attached via `DependencyPropertyDescriptor`. The `Unloaded` event fires every time the ContextMenu closes, but the next show will re-attach the same instance — and there is no `Loaded` event on `MenuItem` that can re-subscribe the workaround because the `RequeryOnChange` attached property is already `True` and its `PropertyChangedCallback` only fires on value changes.
- If handler accumulation is genuinely a concern, prefer `WeakEventManager` patterns (which `DependencyPropertyDescriptor` is not) instead of `AddValueChanged`/`RemoveValueChanged` pairs.
- For bugs that only reproduce in net48, **always** compare with a net8 log of the same scenario. The two logs side-by-side reveal whether net48 is missing a callback that net8 has — that is the strongest signal that the root cause is a missing WPF fix, not application logic.

**References:**
- Issue #78: https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/78 — "Stale Check «Проверить» серый на 2+ ПКМ в Revit 2023 (net48)"
- Commit `8ffe965` — regression introduction (Type Catalog bake-in feature accidentally broke BUG-009 workaround)
- Commit `c7e8927` — original BUG-009 fix (the working baseline)
- `https://stackoverflow.com/questions/6780159` — `DependencyPropertyDescriptor` static `EventHandlerList` accumulation behaviour

---

## BUG-010: Dialog silently never opens in net8, crashes Revit in net48 — XamlDuplicateMemberException "Resources already set"

**Symptom:** A WPF dialog simply does not appear when invoked (net8, Revit 2025-2026 — the click "does nothing"), and the same code path **crashes Revit** in net48 (Revit 2019-2024). The command method starts (visible in `smartcon.log` scope `=== START ===`), then dies. On net8 the exception surfaces in `Dispatcher.UnhandledException` (logged, process survives); on net48 the unhandled exception takes down the host process.

**Diagnostic shape in `smartcon.log`:**

```text
[ERR] [Dispatcher.UnhandledException] XamlParseException: Свойство "Resources" уже задано для "ConfirmationDialogView".
 ---> XamlDuplicateMemberException: Свойство "Resources" уже задано для "ConfirmationDialogView".
   в SmartCon.FamilyManager.Views.ConfirmationDialogView.InitializeComponent()
   в FamilyManagerDialogService.ShowConfirmation(...)
```

Real case (Issue #154, 2026-07-22): the «Удалить из каталога» context-menu command never showed its confirmation dialog in Revit 2025 and hard-crashed Revit 2023.

**Root cause:** the dialog's XAML put `<ui:SingletonResources/>` as a **sibling of other resources inside an implicit `<Window.Resources>`**:

```xml
<!-- BROKEN — two children, no explicit ResourceDictionary -->
<Window.Resources>
    <ui:SingletonResources/>
    <converters:BoolToVisibilityConverter x:Key="BoolToVis"/>
</Window.Resources>
```

`SingletonResources` is itself a `ResourceDictionary` subclass. In the implicit-collection form (multiple children, no explicit `<ResourceDictionary>` wrapper) the XAML compiler emits BAML that assigns the `Resources` property twice → `XamlDuplicateMemberException` at `LoadBaml` time. Microsoft Learn: implicit `<Window.Resources>` is equivalent to the explicit form **only when there are no merged dictionaries**; a merged dictionary is legal solely inside `ResourceDictionary.MergedDictionaries` of an explicit `<ResourceDictionary>`.

**Why it slipped through:** the XAML compiles cleanly — the failure is runtime-only at `LoadBaml`. And because the failing call (`_dialogService.ShowConfirmation`) stood **before** the `try` block in the command, the exception escaped into `Dispatcher.UnhandledException` instead of being logged with context.

**Fix — canonical pattern (already used by `DatabaseUpdateProgressView`, `ProfileView`, `FamilyBatchImportView`):**

```xml
<controls:DialogWindowBase.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ui:SingletonResources/>
        </ResourceDictionary.MergedDictionaries>
        <converters:BoolToVisibilityConverter x:Key="BoolToVis"/>
    </ResourceDictionary>
</controls:DialogWindowBase.Resources>
```

**Allowed alternatives:**

```xml
<!-- OK: SingletonResources is the ONLY child (implicit dictionary has one element) -->
<Window.Resources>
    <ui:SingletonResources/>
</Window.Resources>

<!-- OK: resources nested INSIDE SingletonResources (they become its own items) -->
<Window.Resources>
    <ui:SingletonResources>
        <converters:BoolToVisibilityConverter x:Key="BoolToVis"/>
    </ui:SingletonResources>
</Window.Resources>
```

**Audit recipe (PowerShell, run from repo root):** finds every XAML where a self-closed `<ui:SingletonResources/>` is followed by another element inside the same implicit resources block.

```powershell
Get-ChildItem -Recurse -Filter "*.xaml" src/ | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } | ForEach-Object {
    $c = Get-Content $_.FullName -Raw
    if ($c -match '(?s)<Window\.Resources>\s*<ui:SingletonResources\s*/>\s*<(?!/)') { Write-Output "BROKEN: $($_.FullName)" }
}
```

Repeat the check with `<UserControl\.Resources>` and `<controls:DialogWindowBase\.Resources>` in the regex if new root elements appear.

**References:**
- Issue #154: https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/154 — delete-family confirmation dialog crash
- Microsoft Learn — ResourceDictionary: implicit collection usage is invalid once a merged dictionary is involved
- StackOverflow 72673068 — "'Resources' property has already been set" — same class of error
