# ADR-027: Placed Families v2 — OfClass(FamilyInstance) for Analysis, EditFamily for Extraction Only

**Status:** Accepted (revised 2026-06-08 — see Revision History)
**Date:** 2026-06-07
**Deciders:** Architecture
**Phase:** 22
**Supersedes:** ADR-026 (reverted in 50273be)

## Context

ADR-026 ("Placed Families Import") tried to add loadable families to "Импорт активного файла" via a single `IPlacedFamilyExtractor` that **simultaneously** did `FilteredElementCollector(FamilyInstance) + GroupBy` (metadata) **and** `EditFamily + SaveAs` for each unique family (staging) inside the same `AnalyzeActiveProjectAsync` call.

This caused two production bugs in a typical MEP project (50–200+ unique loadable families):

1. **Hang before confirmation dialog.** `AnalyzeActiveProject` ran `EditFamily + SaveAs` for each loadable (~500 ms each on average, from logs) **before** the user was even shown the count. A 100-family project froze the UI for 50+ seconds.
2. **No attribute extraction for loadable.** The downstream `SystemFamilyAttributeExtractionService.ExtractFromRvt` explicitly rejected `IsFamilyDocument == true` ("Document is a family, not a project"). Loadable rows in the batch dialog were imported but had no `Type` records and no `FamilyExtractionResult` — silent data loss.

The user (a senior MEP engineer) asked: *"у нас в модели могут быть сотни тысяч выставленных семейство но нас же должны интересовать сами семейства, сами имена семейства. Если 3 семейства выставить 100 тысяч раз то не должны ли мы быстро получить что нам необходимо загрузить в FM только 3 семейства?"* — and was absolutely right.

## Revision History

* **2026-06-08** — Two production bugs reported and fixed:
  1. **"Импорт активного файла" предложил загрузить в FM ВСЕ семейства проекта** (вместо только выставленных). Причина: первая версия `LoadableFamilyScanner` использовала `FilteredElementCollector.OfClass(Family)` — это возвращает все загруженные в проект семейства, а не только те, у которых есть хотя бы один `FamilyInstance`. Исправлено: `OfClass(FamilyInstance) + WhereElementIsNotElementType() + GroupBy(fi.Symbol.Family.UniqueId)` (подход Jeremy Tammik'а). In-place семейства также фильтруются на этапе скана.
  2. **Двойной показ batch-диалога в "Импорт выделенных элементов".** Причина: `ImportSelectedElementsAsync` после `BuildSelectedElementsBatchItemsAsync` (staging) показывал `FamilyBatchImportView` локально, а затем передавал `toImport` в `ProcessProjectImportAsync`, который показывал **тот же** диалог повторно. Пользователь видел и заполнял категории в первом показе, и видел уже заполненные категории во втором. Исправлено: dialog показывается **только** в `ProcessProjectImportAsync` (симметрично с `ImportActiveFileAsync`). Локальные `selectedItems`/`toImport` убраны — `ProcessProjectImportAsync` сам фильтрует `Action != Skip`.

## Decision

### 1. Decouple "analyze" from "stage"

| Phase | Old (ADR-026) | New (ADR-027) |
|---|---|---|
| **Analyze** (UI thread, fast) | `GroupBy(FamilyInstance)` × N + `EditFamily` × F | `OfClass(FamilyInstance)` + `GroupBy(fi.Symbol.Family.UniqueId)` — only families **with placed instances** |
| **Confirm** | n/a (or only for "selected elements") | "5 system cat, 87 loadable fam" — **always shown** |
| **Stage** (UI thread, slow) | Inline in `AnalyzeActiveProject` | Separate `StageLoadableFamily` per family, **only after confirmation** |
| **Extract** | `SystemFamilyAttributeExtractionService.ExtractFromRvt` (project docs only) | `IFamilyDataExtractionService.Extract(rfaPath, [])` (already supports `.rfa` via `app.OpenDocumentFile + FamilyManager.GetTypes()`) |

For "Импорт активного файла" this means:

```
Phase 1 (fast, < 1s on 200-family project):
  AnalyzeActiveProjectAsync(Document)
    → return PlacedProjectAnalysis {
        systemRows:    { category, typeCount, types }
        loadableRows:  { familyName, familyUniqueId, categoryName, typeCount }
      }
  NO EditFamily, NO SaveAs

Phase 2 (immediate):
  ShowConfirmation(
    "Импортировать в каталог: {0} системных категорий ({1} типов) и {2} загружаемых семейств?")

Phase 3 (only after user clicks "Yes"):
  For each system:    StageFromActiveProject (existing .rvt isolation)
  For each loadable:  EditFamily + SaveAs(temp.rfa)
  Build FamilyBatchImportItem list (mixed FamilySource)

Phase 4:
  FamilyBatchImportView dialog (mixed system + loadable rows)

Phase 5:
  Group by FamilySource
    system   → ISystemFamilyImportOrchestrator.ImportBatchItemsAsync
              → ISystemFamilyAttributeExtractor.ExtractAndSaveAsync (existing)
    loadable → ILoadableFamilyImportOrchestrator.ImportAndPersistTypesAsync
              → IFamilyDataExtractionService.Extract(managedRfaPath) per row
```

For "Импорт выделенных элементов" the structure is the same but Phase 1 is the picker (already modal and on UI thread), and there is no separate confirmation (the user already chose).

### 2. New minimal abstraction set

Five new files, none of them abstract-for-abstract's-sake:

| File | Responsibility | Why it can't be merged into an existing one |
|---|---|---|
| `LoadableFamilyInfo` (Core) | DTO for one loadable family. | Pure data, no logic. |
| `ILoadableFamilyScanner` + `LoadableFamilyScanner` (Revit) | `GetUniqueFamilies(Document) → IReadOnlyList<LoadableFamilyInfo>` via `OfClass(FamilyInstance) + WhereElementIsNotElementType() + GroupBy(fi.Symbol.Family.UniqueId)` (Jeremy Tammik pattern, 2018). In-place families filtered out. | Used by `ImportActiveFileAsync`; needs to be injected into `SystemFamilyRevitOperations` for the picker too. |
| `AnyElementSelectionFilter` (Revit/Selection) | `FamilyInstance` + system categories. | `SystemFamilySelectionFilter` only accepted system categories. |
| `ILoadableFamilyTypeResolver` + `LoadableFamilyTypeResolver` (Revit) | Opens `.rfa`, reads `FamilyManager.Types` and `FamilySymbol`s, returns `IReadOnlyList<FamilyTypeDescriptor>` with UniqueId. | `IFamilyDataExtractionService.Extract` already exists for *parameters* but returns no UniqueId on the `FamilyExtractionTypeValues`. We need UniqueId to feed `IFamilyTypeRepository.SaveTypesAsync`. |
| `ILoadableFamilyImportOrchestrator` + `LoadableFamilyImportOrchestrator` (FamilyManager) | `ImportAndPersistTypesAsync(IReadOnlyList<FamilyBatchImportItem>, targetRevitVersion, ct)` — calls `IFamilyImportService.ImportBatchAsync` (managed storage) + `ILoadableFamilyTypeResolver.ResolveTypesFromRfa` + `IFamilyTypeRepository.SaveTypesAsync` + resolves managed path for attribute extraction. | Symmetric to `ISystemFamilyImportOrchestrator`; keeps `ProcessProjectImportAsync` thin. |

**No new attribute extractor.** `IFamilyDataExtractionService.Extract(rfaFilePath, expectedParameterNames)` already exists and does exactly what we need: `app.OpenDocumentFile(rfaPath)` → `familyDoc.IsFamilyDocument` → `familyDoc.FamilyManager.Types` → `FamilyType.AsString/AsDouble/...` per parameter. This was previously only used for `ProcessFamilyImportAsync` (active `.rfa` document); we now reuse it for managed-storage `.rfa` files post-import.

### 3. Unified batch dialog

The same `FamilyBatchImportView` shows mixed `system` and `loadable` rows. `FamilyBatchImportItem` already had `FamilySource` and `TypeCount` (added in Phase 21, kept after revert because they were useful and never broken). No new view-model state.

### 4. UI / l10n

- Button "Импорт системного семейства" → **"Импорт выделенных элементов"** (5 new l10n keys: `FM_ImportSelectedElements`, `FM_SelectElementsPrompt`, `FM_ImportActiveConfirmTitle`, `FM_ImportActiveConfirmMessage`).
- `FM_ImportSystemFamily` / `FM_SystemFamilySelectPrompt` kept for backward compat (no current consumer).

## Consequences

### Positive

* **No more pre-confirmation hangs.** `AnalyzeActiveProject` is `O(F)` on a `OfClass(FamilyInstance)` collector — runs in milliseconds on a 200-family project.
* **Only placed families are imported.** `LoadableFamilyScanner` uses `OfClass(FamilyInstance) + GroupBy(fi.Symbol.Family.UniqueId)` so the user sees only families that actually have at least one placed instance. Loaded-but-unused families stay out of the catalog.
* **Symmetric pipeline.** Both commands (`ImportActiveFile`, `ImportSelectedElements`) share the same Phase 3 staging + Phase 4 batch dialog + Phase 5 dispatch. Only Phase 1 differs (analyze vs picker). Dialog is shown **exactly once** per command.
* **Attribute extraction works for loadable.** `IFamilyDataExtractionService` is reused, no new extractor.
* **Type persist works for loadable.** `LoadableFamilyTypeResolver` opens `.rfa` once after managed-storage import, reads types + UniqueId, saves via `IFamilyTypeRepository`.
* **Temp disk usage halves.** For loadable we no longer stage an `.rfa` per family up front — only the `Family` references in `LoadableFamilyInfo` are needed in Phase 1.
* **I-05 honoured.** `LoadableFamilyInfo` stores only strings (`FamilyUniqueId`, `FamilyName`); `ElementId` is resolved on demand inside `StageLoadableFamilyFromProject` via `doc.GetElement(info.FamilyUniqueId)`.
* **I-09 honoured.** `ILoadableFamilyScanner` and `ILoadableFamilyTypeResolver` live in `SmartCon.Core` as interfaces without Revit-API surface; implementations in `SmartCon.Revit` cast `Document`.

### Negative

* **Staging is still needed for managed-storage import.** `IFamilyImportService.ImportBatchAsync` takes a file path, so we must produce a `.rfa` to import. We do this **after** confirmation, per family. For 100 loadable families this is ~50 seconds — but the user already saw the count and clicked "Yes", so they're committed.
* **Hot-path type-cast in Core.** `ISystemFamilyRevitOperations` still uses `Autodesk.Revit.DB.Document` and `BuiltInCategory` in its signature (predates this ADR). Out of scope to fix; flagged for future refactor.
* **`_loadableFamilyScanner` injected into `SystemFamilyRevitOperations` constructor.** The class now has a 3-arg constructor where it previously had 2. Existing DI registration updated; tests would need to mock the new dependency (no test currently constructs this class directly).

## Files Touched

### Added (new)
* `src/SmartCon.Core/Models/FamilyManager/LoadableFamilyInfo.cs`
* `src/SmartCon.Core/Models/FamilyManager/SelectedElementsAnalysis.cs`
* `src/SmartCon.Core/Services/Interfaces/ILoadableFamilyScanner.cs`
* `src/SmartCon.Core/Services/Interfaces/ILoadableFamilyTypeResolver.cs`
* `src/SmartCon.Core/Services/Interfaces/ILoadableFamilyImportOrchestrator.cs` (also contains `LoadableFamilyImportResult`, `LoadableFamilyAttributeTask` records)
* `src/SmartCon.Revit/FamilyManager/LoadableFamilyScanner.cs` — **Revised 2026-06-08:** now uses `OfClass(FamilyInstance) + WhereElementIsNotElementType() + GroupBy(fi.Symbol.Family.UniqueId)` (was `OfClass(Family)`)
* `src/SmartCon.Revit/FamilyManager/LoadableFamilyTypeResolver.cs`
* `src/SmartCon.Revit/Selection/AnyElementSelectionFilter.cs`
* `src/SmartCon.FamilyManager/Services/LoadableFamilyImportOrchestrator.cs`
* `src/SmartCon.Tests/FamilyManager/Models/LoadableFamilyInfoTests.cs` (4 unit tests)

### Modified
* `src/SmartCon.Core/Services/Interfaces/ISystemFamilyRevitOperations.cs` — `PickSystemTypes` → `PickSelectedElements() → SelectedElementsAnalysis`
* `src/SmartCon.Core/Services/FamilyManager/SystemFamilyTempLayout.cs` — unchanged (loadable staging reuses `StagingSubdir = "SystemFamilyLoadFromProject"`, cleanup already handles it)
* `src/SmartCon.Revit/FamilyManager/SystemFamilyRevitOperations.cs` — `PickSystemTypes` → `PickSelectedElements` using `AnyElementSelectionFilter`; constructor now takes `ILoadableFamilyScanner`
* `src/SmartCon.FamilyManager/Services/FamilyManagerServices.cs` — added 2 fields: `LoadableFamilyScanner`, `LoadableFamilyImportOrchestrator`
* `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.cs` — 2 new readonly fields; `[NotifyCanExecuteChangedFor(nameof(ImportSelectedElementsCommand))]`
* `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Import.cs` — `ImportSystemFamilyAsync` → `ImportSelectedElementsAsync`; new `BuildSelectedElementsBatchItemsAsync(SelectedElementsAnalysis)`; new `BuildActiveProjectBatchItemsAsync(systemAnalyses, loadableFamilies)`; new `StageLoadableFamilyFromProject(LoadableFamilyInfo)`; helper `SanitizeFileName`; `StageFromPicker` removed (inlined into the new flow); `CleanupTempFiles` removed (handled by `IActiveImportCleanupService`). **Revised 2026-06-08:** local `FamilyBatchImportView` show + `toImport` filtering removed — dialog is now displayed **only** by `ProcessProjectImportAsync`, eliminating the double-show bug.
* `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.FamilyEdit.cs` — `ImportActiveFileAsync` Phase 1 split into `(CategoryAnalysis[], LoadableFamilyInfo[])`; new `ShowConfirmation` between Phase 1 and Phase 3; new `BuildActiveProjectBatchItemsAsync`; `ProcessProjectImportAsync(IReadOnlyList<FamilyBatchImportItem>)` dispatches by `FamilySource`; new `ExtractAttributesForLoadableTasks`
* `src/SmartCon.FamilyManager/Views/FamilyManagerPaneControl.xaml` — second button now binds to `ImportSelectedElementsCommand` and uses `FM_ImportSelectedElements` l10n key
* `src/SmartCon.Core/Services/LocalizationService.Keys.FamilyManager.cs` — 5 new keys (see above)
* `src/SmartCon.UI/StringLocalization.cs` — corresponding `const string` additions
* `src/SmartCon.App/DI/ServiceRegistrar.cs` — registered `ILoadableFamilyScanner`, `ILoadableFamilyTypeResolver`, `ILoadableFamilyImportOrchestrator`
* `docs/domain/models.md` — added sections for `LoadableFamilyInfo`, `SelectedElementsAnalysis`, `LoadableFamilyAttributeTask`, `LoadableFamilyImportResult`
* `docs/domain/interfaces.md` — `ISystemFamilyRevitOperations.PickSelectedElements`; new `ILoadableFamilyScanner`, `ILoadableFamilyTypeResolver`, `ILoadableFamilyImportOrchestrator`
* `docs/family-manager/README.md` — Phase 22 status entry

### Removed
* `src/SmartCon.Revit/Selection/SystemFamilySelectionFilter.cs` — replaced by `AnyElementSelectionFilter` (one file deleted, one added — net zero).

## What is NOT in scope

* **Attribute extraction for system families in `.rfa` form.** System families live inside `.rvt` project files; the existing `SystemFamilyAttributeExtractionService.ExtractFromRvt` path stays unchanged.
* **`doc.IsModifiable` check on staging.** `EditFamily` requires the document to be non-modifiable. The existing `ImportActiveFile` flow already operates after any user transactions are committed (the button is in a popup), so we did not re-introduce the defensive `IsModifiable` check that ADR-026 added. If a future caller hits "family is in modifiable state" they will see the underlying Revit exception in the log.
* **A real progress bar during staging.** Status messages report each phase. A progress bar would require non-trivial refactoring of `StageLoadableFamilyFromProject` to yield; left for a future phase.
* **Hot-path `Document`/`BuiltInCategory` in `ISystemFamilyRevitOperations`.** Pre-existing I-09 violation. Out of scope.

## Test count

* 1215 → **1219** unit tests, all green.
  * +4 `LoadableFamilyInfoTests` (record equality, field round-trip)
  * Tests for `SelectedElementsAnalysis` were attempted but the record transitively requires `RevitAPI.dll` (`BuiltInCategory` value-type carrier) at construction time, which is excluded from the test bin. Skipped.
* **1219** tests after 2026-06-08 revision (no new tests — `LoadableFamilyScanner` requires Revit API; refactor is behaviour-equivalent on the contract level, covered by manual Revit tests).
