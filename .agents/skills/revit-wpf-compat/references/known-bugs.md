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
