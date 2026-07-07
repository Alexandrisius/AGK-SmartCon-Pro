# ADR-024: Active Family Import Preparer — Sidecar (.txt) Preservation

**Status:** accepted
**Date:** 2026-06-05

## Context

The "Import Active File" command in FamilyManager was dropping the Type
Catalog (.txt) sidecar that accompanies a `.rfa` family file. The
pipeline was:

```
[VM] ImportActiveFileAsync
  └─ [VM] ScanActiveFileAsync (ExternalEvent)
       └─ activeDoc.SaveAs(tempRfaPath)        ← .rfa only, no .txt
  └─ LocalFamilyImportService.ImportFileAsync(tempRfaPath)
       └─ ImportTypeCatalogIfPresentAsync(tempRfaPath, ...)
            ├─ .txt next to tempRfaPath?         ← never — temp folder is empty
            └─ previous version .txt in storage? ← only works for UPDATE,
                                                  not for first-time import
```

Two failure modes followed from this design:

1. **First-time import of a new family with Type Catalog** — the catalog
   was silently dropped. The user saw a successful import but their
   types were not registered.
2. **Single-version import of any family with Type Catalog** — the
   "previous version" fallback (`LIMIT 1 OFFSET 1`) returned `null`,
   falling through to a silent no-op.

The single in-line `ScanActiveFileAsync` method also mixed five
responsibilities: classification, SaveAs, sidecar lookup, project
analysis and cleanup — making the code untestable and resistant to
refactoring.

## Decisions

### FM-024-001: Split responsibility into 4 services

| Interface | Responsibility | Revit API? |
|---|---|---|
| `IFamilySidecarLocator` | Pure I/O: find & copy .txt sidecar | No (unit-testable) |
| `IActiveFamilyFilePreparer` | SaveAs active family + sidecar copy | Yes (ExternalEvent) |
| `IActiveDocumentClassifier` | Detect Family / Project / None | Yes (ExternalEvent) |
| `IActiveImportCleanupService` | Remove temp staging folders | No |

`IFamilySidecarLocator` is intentionally pure I/O so that the lookup
and copy logic can be unit-tested with regular file system fixtures
(13 tests in `LocalFamilySidecarLocatorTests`). The Revit-bound
preparer is reduced to orchestration and is covered by smoke tests
plus the sidecar locator tests.

### FM-024-002: `ActiveFamilyPreparationResult` carries both paths

The result of preparation exposes the temp paths *and* the original
paths so the import layer can pass the original .rfa location down to
the Type Catalog resolver:

```csharp
public sealed record ActiveFamilyPreparationResult(
    string TempRfaPath,
    string? TempTxtPath,
    string? OriginalRfaPath,
    string? OriginalTxtPath);
```

### FM-024-003: `OriginalSourcePath` on `FamilyImportRequest` /
`FamilyBatchImportItem` / `FamilyUpdateRequest`

The temp .rfa is not where Revit looks for the sidecar — the original
document is. The request models now carry an explicit
`OriginalSourcePath` so `ImportTypeCatalogIfPresentAsync` can search
both locations.

The new sidecar resolution chain is:

1. `.txt` next to `FilePath` (the temp .rfa) — covers ordinary file
   imports where the user just picks a file from disk.
2. `.txt` next to `OriginalSourcePath` — covers the "Import Active
   File" case, the "Update with a temp copy" case, and any workflow
   where the file is staged through a temp folder.
3. `.txt` from the previous version in managed storage — legacy
   fallback, retained for compatibility with batch imports of files
   that were originally placed by older versions of the plugin.

When none of the three yield a sidecar, the family is imported
without types — silently, as before.

### FM-024-004: `IActiveImportCleanupService` replaces the static
`CleanupImportActiveTemp`

The cleanup logic is moved out of `FamilyManagerMainViewModel.FamilyEdit.cs`
into a dedicated service so the VM stays focused on UI state and the
cleanup can be invoked independently (e.g. from integration tests or
a future "Clean Temp" diagnostics command).

### FM-024-005: `IActiveDocumentClassifier` is the first step of
`ImportActiveFileAsync`

The ViewModel now classifies the active document *before* deciding
which import flow to take. The `ImportActiveKind` enum and the
`ImportActiveScanResult` record are removed; the new flow is a clean
`switch` on `ActiveDocumentKind`.

## Layers

| Layer | New types |
|---|---|
| `SmartCon.Core/Services/Interfaces/` | `IFamilySidecarLocator`, `IActiveFamilyFilePreparer`, `IActiveDocumentClassifier`, `IActiveImportCleanupService`, `ActiveDocumentKind` |
| `SmartCon.Core/Models/FamilyManager/` | `ActiveFamilyPreparationResult`; `OriginalSourcePath` on `FamilyImportRequest`, `FamilyBatchImportItem`, `FamilyUpdateRequest` |
| `SmartCon.FamilyManager/Services/LocalCatalog/` | `LocalFamilySidecarLocator` |
| `SmartCon.FamilyManager/Services/` | `ActiveFamilyFilePreparer`, `ActiveDocumentClassifier`, `ActiveImportCleanupService` |
| `SmartCon.FamilyManager/ViewModels/` | `FamilyManagerMainViewModel.FamilyEdit.cs::ImportActiveFileAsync` (rewritten) |
| `SmartCon.App/DI/ServiceRegistrar.cs` | 4 new registrations |
| `SmartCon.Tests/FamilyManager/Services/` | `LocalFamilySidecarLocatorTests` (13), `ActiveFamilyFilePreparerTests` (1) |
| `SmartCon.Tests/FamilyManager/Repository/` | `LocalFamilyImportServiceTypeCatalogTests` (5) |

## Consequences

**Плюсы:**
- Type Catalog больше не теряется при импорте активного файла.
- Чистая sidecar-логика покрыта 13 unit-тестами без моков Revit.
- VM-слой стал проще и читаемее; ad-hoc static helper `CleanupImportActiveTemp` удалён.
- Цепочка resolution явная и логируемая на каждом шаге (`[TypeCatalog] …`).
- Все 3 call site `ImportTypeCatalogIfPresentAsync` используют один и тот же контракт.
- Покрытие тестами: 19 новых тестов (12 sidecar + 1 preparer + 5 TypeCatalog + 1 уже был).

**Минусы:**
- `FamilyImportRequest`/`FamilyBatchImportItem`/`FamilyUpdateRequest` получили новое поле — это технический breaking change на уровне исходников (record ctor), но полностью обратно совместимо по умолчанию (`= null`).
- ViewModel стал более "толстым" по числу зависимостей (3 новых поля), но каждая зависимость имеет чёткую ответственность.

## Sources

- [Jeremy Tammik — "Type Catalog and Lookup Tables"](https://thebuildingcoder.typepad.com/blog/2014/09/lookup-table-and-type-catalog.html)
- [Autodesk Revit API — `Document.SaveAs` method](https://www.revitapidocs.com/2025/cb6faa76-3f3a-7c1e-c0a7-35f9bb6cbcfa.htm)
- ADR-018 (FamilyManager Refactoring — DI Patterns): patterns for splitting VM responsibilities into DI-managed services.
- ADR-017 (Attribute Extraction Foundation): established the `family_data_import_runs` / `extracted_attribute_values` schema that this fix populates.
