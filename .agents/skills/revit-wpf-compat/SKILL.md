---
name: revit-wpf-compat
description: "Net48/net8 WPF compatibility rules for Revit plugins. Use when writing WPF code, creating dialogs, showing windows, using Dispatcher, accessing Application.Current, debugging net48-only crashes in Revit add-ins, when the WPF DockablePane freezes after a FireAndForget import (LMB dead, RMB unfreezes), when a ContextMenu MenuItem is greyed out in net48 but works in net8 — see §'dotnet/wpf#4078 — MenuItem CommandParameter ignored in net48', OR when a dialog silently never opens in net8 / crashes Revit in net48 with XamlParseException 'Resources already set' — see §'SingletonResources in Window.Resources (BUG-010)' for the mandatory MergedDictionaries pattern."
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

## SingletonResources in Window.Resources (BUG-010)

When a dialog needs `SingletonResources` **plus** any other resource (converters etc.), the implicit `<Window.Resources>` form crashes at runtime:

```xml
<!-- BROKEN — XamlDuplicateMemberException "Resources already set" at LoadBaml.
     net8: dialog silently never opens; net48: Revit crashes. Compiles fine. -->
<Window.Resources>
    <ui:SingletonResources/>
    <converters:BoolToVisibilityConverter x:Key="BoolToVis"/>
</Window.Resources>
```

`SingletonResources` is itself a `ResourceDictionary` — as a sibling of other resources in the implicit form, the compiler emits BAML that assigns `Resources` twice.

**Mandatory pattern** (canonical in this repo: `DatabaseUpdateProgressView`, `ProfileView`, `FamilyBatchImportView`):

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

Allowed: `SingletonResources` as the ONLY child of `<Window.Resources>`, or nesting the extra resources INSIDE `<ui:SingletonResources>…</ui:SingletonResources>`.

Details + PowerShell audit recipe: [`references/known-bugs.md`](references/known-bugs.md) BUG-010. Real case: Issue #154 (delete-family dialog, 2026-07-22).

## Build: always build both targets

```bash
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25   # net8.0
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24   # net48
```

If it compiles on R25 but crashes on R24 — it's likely one of the patterns above.

## `Application.Current?.Dispatcher` is ALSO null in net48 — capture in ctor

The forbidden patterns above show the **direct** `Application.Current.Dispatcher` form. The **null-conditional** form `Application.Current?.Dispatcher` looks safer but is the same bug:

- In net48, `Application.Current` is null → `Application.Current?.Dispatcher` is null → any `if (dispatcher is { HasShutdownStarted: false })` check is **false** → the `if` body silently never runs.
- In net8, `Application.Current` is non-null → the same code works → the bug never reproduces there.
- This is a **net48-only** silent failure. The user sees a freeze (right-click unfreezes), not an exception.

The fix is to **capture the dispatcher in the VM ctor** (which runs on the UI thread) and use the captured instance everywhere:

```csharp
public FamilyManagerMainViewModel(...)
{
    // Application.Current?.Dispatcher is null in net48; Dispatcher.CurrentDispatcher
    // from the UI thread ctor is always reliable.
    _uiDispatcher = System.Windows.Application.Current?.Dispatcher
        ?? Dispatcher.CurrentDispatcher;
}
```

This is the **only** safe pattern in net48. `Dispatcher.CurrentDispatcher` from a thread-pool `FireAndForget` lambda is wrong — it creates a brand-new dispatcher for that thread, not the UI one. Capture in the ctor.

**Full case study** (FamilyManager freeze after import, 2026-06-19): [`references/fireandforget-freeze-net48.md`](references/fireandforget-freeze-net48.md) — root cause, fix, the 5 supporting rules (capture-in-ctor, explicit marshal, no double `LoadTreeAsync`, minimize `Measure`/`Debug` on UI thread, don't try `Clear() + Add()` instead of `TreeNodes = rootNodes`), and the diagnostic recipe. The decision and full post-mortem are in [`docs/adr/031-fireandforget-ui-marshalling.md`](../../docs/adr/031-fireandforget-ui-marshalling.md).

## dotnet/wpf#4078 — MenuItem CommandParameter ignored in net48

**Symptoms:**
- A `MenuItem` inside a `ContextMenu` is **greyed out** in Revit 2023 (net48) but works in Revit 2025 (net8.0-windows).
- The bound `Command` exists, the `CanExecute` method gets called, but **always with `parameter = null`** on the first invocation.
- After the first click, the button works once and then stays disabled forever (subsequent `CanExecute` calls also get `null`).
- `CommandManager.InvalidateRequerySuggested()` does **NOT** help — verified empirically in this codebase on 2026-06-19 and confirmed by StackOverflow 37988297 ("I tried calling `InvalidateRequerySuggested()` manually... it seems I'm mistaken").
- Other `MenuItem`s in the same `ContextMenu` (without `CommandParameter` binding) work fine in both Revit versions.

**Root cause** (verified via Exa on 2026-06-19):

When WPF creates a `MenuItem` inside a freshly-shown `ContextMenu`, it sets the dependency properties **in the order they appear in XAML**. If `Command` is set BEFORE `CommandParameter`, the property-changed callback on `CommandProperty` calls `UpdateCanExecute()` **before** the `CommandParameter` binding has resolved. So `CanExecute` runs with `null` and the menu item is disabled.

`dotnet/wpf` issue **#316** (2008) and **#3452** (2020) and **#4078** (2021) all track this. PR **#4217** (merged 2022-07-21 into .NET Core 3.1+) added a `PropertyChangedCallback` on `MenuItem.CommandParameterProperty` that calls `item.UpdateCanExecute()` when the parameter is set, fixing the symptom by causing a second `CanExecute` evaluation once the binding is resolved.

**The fix is in .NET Core 3.1+ / .NET 5+ / .NET 6+ / .NET 7+ / .NET 8+ — but was NOT backported to .NET Framework 4.x.** So:

| Revit version | TFM | WPF version | Bug present? |
|---|---|---|---|
| 2019-2024 | `net48` | .NET Framework 4.8 | **YES** — button permanently disabled after first click |
| 2025-2026 | `net8.0-windows` | .NET 8 | No (PR #4217 already applied) |

**Why other menu items "work fine" in 2023:** the bug only triggers when a `MenuItem` has BOTH a `Command` AND a `CommandParameter` binding in the same `XAML` declaration. `MenuItem`s with only `Command` (e.g. `LoadToProjectCommand`, `EditFamilyCommand`, `OpenPropertiesCommand`, `DeleteFamilyCommand`) or with a static `CommandParameter="…"` literal are unaffected.

**Workaround for net48:** use the local `MenuItemCommandParameterRequery` attached property in `src/SmartCon.UI/Behaviors/MenuItemCommandParameterRequery.cs`. It:

1. Subscribes to `MenuItem.CommandParameter` changes via `DependencyPropertyDescriptor.FromProperty(...)`.
2. When the parameter changes (first bind or rebind on a new `PlacementTarget`), it briefly null-and-reassigns `MenuItem.Command` to trigger `OnCommandChanged` → `UpdateCanExecute()` (the **same internal path that PR #4217 added**). This forces WPF to re-evaluate `CanExecute` with the now-resolved parameter.

```xml
<MenuItem Header="{loc:Loc FM_Check}"
          CommandParameter="{Binding PlacementTarget.DataContext, RelativeSource={RelativeSource AncestorType=ContextMenu}}"
          Command="{Binding CheckCategoryCommand}"
          behaviors:MenuItemCommandParameterRequery.RequeryOnChange="True" />
```

`MenuItem.UpdateCanExecute` is `internal` in WPF, so we cannot subclass `MenuItem` and call it from another assembly — the null/reassign hack is the next cleanest option. It is identical in effect to the upstream `PropertyChangedCallback` (verified by reading the dotnet/wpf source for `MenuItem.OnCommandParameterChanged`).

**Three rules:**

1. **Always** add `behaviors:MenuItemCommandParameterRequery.RequeryOnChange="True"` to any `MenuItem` inside a `ContextMenu` that has a bound `CommandParameter`. Do this for the FIRST such menu item and the same fix propagates — WPF propagates the property through all bindings. Actually, no — the attached property is per-element, so add it to every relevant `MenuItem`. (No harm in adding it to others; it's a no-op when the parameter never changes.)
2. **XAML order**: place `CommandParameter` BEFORE `Command` in the declaration. This helps in net8 too (slightly faster first render, since the binding is already resolved when `OnCommandChanged` fires). The `RequeryOnChange` attached property makes this less critical, but keep the order anyway for clarity.
3. **Do NOT** rely on `CommandManager.InvalidateRequerySuggested()` to fix this — it does not work for already-shown `ContextMenu` items. Sources: [StackOverflow 37988297](https://stackoverflow.com/questions/37988297), our own production log 2026-06-19 01:26:16 where the second right-click still got `category=<null>` despite the global InvalidateRequerySuggested call.

**Diagnostic recipe when a MenuItem is greyed out:**

1. Add `SmartConLogger.Debug("CanCheck: result=…, param=…, paramType=…")` to the `CanExecute` callback. See `smartcon-logging` skill for the format.
2. If `param=<null>` on the first call and stays null on subsequent calls, this is dotnet/wpf#4078 — apply the workaround.
3. If `param` is non-null but `result=False` for an unrelated reason, it's a logic bug in the `CanExecute` callback, not a WPF binding issue.
4. **Important:** make sure the build is actually Debug — see `smartcon-logging` §"Debug.* configurations are NOT Debug". Otherwise the `[DBG]` lines won't appear and you'll chase the wrong problem.

## References

- `references/known-bugs.md` — detailed bug patterns with symptoms, root cause, and fixes (BUG-001..BUG-010, incl. MenuItem #4078 and SingletonResources/BUG-010)
- `references/fireandforget-freeze-net48.md` — **`Application.Current?.Dispatcher` is also null in net48**; capture dispatcher in VM ctor; the freeze-after-import symptoms (LMB dead, RMB unfreezes, types don't appear). Decision in [`docs/adr/031-fireandforget-ui-marshalling.md`](../../docs/adr/031-fireandforget-ui-marshalling.md).
- `smartcon-logging` skill, §"Debug.* configurations are NOT Debug" — the diagnostic `[DBG]` lines in this recipe only appear if the deployed DLL was actually built with the `DEBUG` symbol. In custom `Debug.Rxx` configurations that requires the `Directory.Build.props` block documented in that skill.
