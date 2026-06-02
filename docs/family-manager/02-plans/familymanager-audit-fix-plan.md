# FamilyManager Audit & Fix Plan

> **Date:** 2026-06-03
> **Status:** Ready for implementation
> **Scope:** Type-centric refactoring bugs + audit findings

---

## BUG-01: Virtual types not showing in tree for families without types

**Severity:** HIGH — user-visible bug, breaks core workflow (DnD from type)

### Root Cause

`AttachTypesToNodes` in `FamilyManagerMainViewModel.cs:437-458`:

```csharp
if (node is FamilyLeafNodeViewModel leaf && batch.TryGetValue(leaf.CatalogItemId, out var types))
{
    foreach (var t in types)
    {
        leaf.Children.Add(new FamilyTypeNodeViewModel(t.CatalogItemId, t.Name));
    }

    if (leaf.Children.Count == 0)
    {
        leaf.Children.Add(new FamilyTypeNodeViewModel(leaf.CatalogItemId, leaf.DisplayName, isVirtual: true));
    }
}
```

The problem: `batch.TryGetValue(leaf.CatalogItemId, out var types)` returns `false` for families WITHOUT types, because `GetAllTypesBatchAsync` only returns dictionary entries for families that HAVE rows in `family_types`. When a family has no types:

1. `batch` dictionary doesn't contain the family's ID at all
2. The entire `if` block is skipped — including the virtual type creation (lines 448-451)
3. The family leaf node gets zero children → no expand arrow in tree → no DnD possible

### Fix

Split the condition so that virtual types are created for ALL families without types, regardless of whether they appear in the batch:

```csharp
if (node is FamilyLeafNodeViewModel leaf)
{
    if (batch.TryGetValue(leaf.CatalogItemId, out var types))
    {
        foreach (var t in types)
        {
            leaf.Children.Add(new FamilyTypeNodeViewModel(t.CatalogItemId, t.Name));
        }
    }

    if (leaf.Children.Count == 0)
    {
        leaf.Children.Add(new FamilyTypeNodeViewModel(leaf.CatalogItemId, leaf.DisplayName, isVirtual: true));
    }

    if (expandedFamilyIds.Contains(leaf.CatalogItemId))
        leaf.IsExpanded = true;
}
```

### Files to change

| File | Change |
|---|---|
| `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.cs` | Move virtual type creation outside `batch.TryGetValue` condition |

---

## BUG-02: Missing Marshal.ReleaseComObject in batch Import path

**Severity:** HIGH — causes freeze/crash when importing 5+ families that need version upgrade

### Root Cause

`RevitFamilyDataExtractionService.cs` opens family documents via `OpenDocumentFile` + `Close(false)` but never calls `Marshal.ReleaseComObject`. In batch Import (foreach loop over imported items), COM RCW objects accumulate on the finalizer thread. When families need version upgrade, MFC dialog corrupts COM state → finalizer thread blocks → Revit freezes (Autodesk REVIT-237190).

**Log evidence:** Repeated `[WRN] [LoadActiveFamily] Failed to close family document: The referenced object is not valid` — confirms the document COM object is already in invalid state.

### Fix

Add `Marshal.ReleaseComObject(familyDoc)` in the `finally` block of `Extract()`:

```csharp
finally
{
    if (familyDoc != null)
    {
        try { familyDoc.Close(false); } catch { }
        System.Runtime.InteropServices.Marshal.ReleaseComObject(familyDoc);
    }
}
```

### Files to change

| File | Change |
|---|---|
| `src/SmartCon.Revit/FamilyManager/RevitFamilyDataExtractionService.cs` | Add `Marshal.ReleaseComObject` after `Close(false)` |

---

## BUG-03: OverwriteCurrentAsync does not update Type Catalog

**Severity:** MEDIUM — silent data desync when user chooses "Overwrite Current" for family with Type Catalog

### Root Cause

`LocalFamilyImportService.Database.cs:284-398`: `OverwriteCurrentAsync` replaces .rfa file and updates SHA256, but does NOT call `ImportTypeCatalogIfPresentAsync`. Both `ImportFileAsync` and `UpdateFamilyAsync` correctly call this method, but OverwriteCurrent was missed.

Result: .txt file on disk stays old, DB records (family_types, extracted_attribute_values) stay old. User sees stale types in tree.

### Fix

Add `await ImportTypeCatalogIfPresentAsync(...)` after `tx.Commit()` at line 381, matching the pattern in `UpdateFamilyAsync`.

### Files to change

| File | Change |
|---|---|
| `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.Database.cs` | Add `ImportTypeCatalogIfPresentAsync` call in `OverwriteCurrentAsync` |

---

## BUG-04: ImportTypeCatalogIfPresentAsync runs outside transaction

**Severity:** MEDIUM — family imported "successfully" but without types if Type Catalog parsing fails

### Root Cause

`LocalFamilyImportService.cs:132-140`: `tx.Commit()` runs on line 132, then `ImportTypeCatalogIfPresentAsync` runs on line 140. If parsing fails, the exception is only logged as Warning — the family is already committed without types.

### Fix

Either: (a) include Type Catalog import inside the transaction, or (b) on failure, delete the imported family and re-throw. Option (b) is simpler and safer.

### Files to change

| File | Change |
|---|---|
| `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.cs` | Handle Type Catalog failure after commit |

---

## STYLE-01: Inconsistent FireAndForget pattern

**Severity:** LOW — no deadlock risk (not inside ExternalEvent), but violates code consistency

### Details

`CategoryPickerViewModel` and `CategoryTreeEditorViewModel` use `async Task FireAndForgetAsync` + `_ =` discard, while `FamilyManagerMainViewModel` uses canonical `async void FireAndForget`. Should be unified.

### Files to change

| File | Change |
|---|---|
| `src/SmartCon.FamilyManager/ViewModels/CategoryPickerViewModel.cs` | Replace `async Task FireAndForgetAsync` with `async void FireAndForget` |
| `src/SmartCon.FamilyManager/ViewModels/CategoryTreeEditorViewModel.cs` | Same |

---

## TEST GAP: Missing test coverage for type-centric features

### Priority tests to add

| # | Test | What it validates |
|---|---|---|
| T-01 | Virtual type creation when batch returns no entry for family | BUG-01 regression |
| T-02 | Virtual type NOT created when family has real types | No false virtuals |
| T-03 | FamilyTypeNodeViewModel.IsVirtual property | Model correctness |
| T-04 | FamilyPlacementDragData.IsVirtual property | Model correctness |
| T-05 | TypeCatalogParser: Tab delimiter | Edge case |
| T-06 | TypeCatalogParser: UTF-16 BOM | Edge case |
| T-07 | Skvoznoy import: .rfa + .txt → family_types + extracted_attribute_values | End-to-end |
| T-08 | Import family without .txt → virtual type in tree | BUG-01 scenario |
| T-09 | OverwriteCurrent updates Type Catalog | BUG-03 regression |
| T-10 | Type Catalog versioning: copy .txt on new version | Plan stage 9 |

---

## Implementation Order

1. **BUG-01** — Virtual types in tree (1 line condition change)
2. **BUG-02** — Marshal.ReleaseComObject (2 lines)
3. **BUG-03** — OverwriteCurrent Type Catalog (~5 lines)
4. **BUG-04** — Import Type Catalog error handling (~10 lines)
5. **STYLE-01** — FireAndForget consistency (~4 lines)
6. **T-01..T-04** — Unit tests for BUG-01
7. **T-05..T-10** — Additional test coverage

---

## Confirmed: Non-issues (rejected after cross-validation)

| Finding | Verdict | Reason |
|---|---|---|
| Revit API in ViewModel | **Not a bug** | All calls inside ExternalEvent callback. I-01 regulates thread context, not calling class. |
| Sync-over-async in LoadFamilyAsync | **Not a bug** | Methods return Task.FromResult (synchronous). All async calls wrapped in Task.Run. |
| InitializeAsync().GetResult() in constructor | **Not a bug** | No SynchronizationContext at DI startup. |
| PropertyChanged on Revit thread | **Not a bug** | WPF 4.5+ auto-marshals PropertyChanged for bindings. |
