---
name: revit-wpf-compat
description: "Net48/net8 WPF compatibility rules for Revit plugins. Use when writing WPF code, creating dialogs, showing windows, using Dispatcher, accessing Application.Current, or debugging net48-only crashes in Revit add-ins."
---

# Revit WPF net48/net8 Compatibility

Rules for writing WPF code that works in both net48 (Revit 2019-2024) and net8.0-windows (Revit 2025-2026).

## Critical: Application.Current is null in Revit

Revit plugins use `IExternalApplication`, NOT `System.Windows.Application`. Therefore:

- `Application.Current` is **null** in net48 context
- In net8 it MAY be non-null (runtime auto-creates it), but NEVER rely on this

### Forbidden patterns

```
Application.Current.Windows              // NullReferenceException in net48
Application.Current.Dispatcher           // NullReferenceException in net48
Application.Current.MainWindow           // NullReferenceException in net48
Application.Current?.Dispatcher.Invoke() // Silently skipped in net48 — progress never updates, windows never close
```

### Correct patterns

```
window.Dispatcher.Invoke(...)                        // Use window's own Dispatcher
window.Owner = explicitWindowReference               // Pass via SetOwnerWindow()
new WindowInteropHelper(view).Owner = revitHandle    // Parent to Revit via Win32 handle
```

## Owner Window: pass explicitly, never search

Do NOT search `Application.Current.Windows` to find parent. Instead:

1. Top-level dialog (from Command): use `WindowInteropHelper(view).Owner = uiapp.MainWindowHandle`
2. Sub-dialog (from ViewModel): store `_ownerWindow` field, set via `SetOwnerWindow(this)` in View constructor

```csharp
// ViewModel
private Window? _ownerWindow;
public void SetOwnerWindow(Window? w) => _ownerWindow = w;
private Window? GetOwnerWindow() => _ownerWindow ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

// View constructor
viewModel.SetOwnerWindow(this);
```

## Dispatcher: use window's own, never Application.Current

```csharp
// WRONG — silently skipped when Application.Current is null
Application.Current?.Dispatcher.Invoke(...)

// RIGHT — window has its own Dispatcher
_progressView.Dispatcher.Invoke(...)
```

## Resource loading: SingletonResources works, but test both targets

`SingletonResources` loads Generic.xaml via embedded resource — does NOT depend on `Application.Current`. WPF styles with `DynamicResource` resolve from window-level resources. Always test on both net48 and net8.

## Build: always build both targets

```bash
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25   # net8.0
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24   # net48
```

If it compiles on R25 but crashes on R24 — it's likely one of the patterns above.

## References

- `references/known-bugs.md` — detailed bug patterns with symptoms, root cause, and fixes
