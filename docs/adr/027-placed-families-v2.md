# ADR-027: Placed Families v2 — OfClass(FamilyInstance) for Analysis, EditFamily for Extraction Only

**Status:** Accepted (revised 2026-07-31 — Phase 2 implemented, see Revision History)
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

* **2026-07-31** — **Phase 2 implemented.** Все 14 системных категорий (позже +#197/#199 → 16) размещают
  инстансы в staged мини-проекте (см. §"Phase 2 — implemented" ниже, заменяет
  прежний §"Phase 2 TODO"). Ключевые решения: handler'ы сами управляют
  транзакциями (StairsEditScope нельзя внутри активной транзакции); версионный
  гейт импорта (потолки R22+, ограждения R25+) блокирует категории без
  placement API с styled-окном; крыша строится `NewExtrusionRoof`, т.к.
  `NewFootPrintRoof` требует UI-контекст и падает в фоновом документе.
  Покрытие: 9 интеграционных тестов `SystemCategoryPlacementTests`
  (R25 72/72, net48 Revit 2023 71+1skip).
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
* `docs/domain/models/family-manager-loadable.md` — added sections for `LoadableFamilyInfo`, `SelectedElementsAnalysis`, `LoadableFamilyAttributeTask`, `LoadableFamilyImportResult`
* `docs/domain/interfaces/family-manager-system.md` — `ISystemFamilyRevitOperations.PickSelectedElements`; new `ILoadableFamilyScanner`, `ILoadableFamilyTypeResolver`, `ILoadableFamilyImportOrchestrator` в [`family-manager-loadable.md`](../domain/interfaces/family-manager-loadable.md)
* `docs/family-manager/README.md` — Phase 22 status entry

### Removed
* `src/SmartCon.Revit/Selection/SystemFamilySelectionFilter.cs` — replaced by `AnyElementSelectionFilter` (one file deleted, one added — net zero).

## What is NOT in scope

* **Attribute extraction for system families in `.rfa` form.** System families live inside `.rvt` project files; the existing `SystemFamilyAttributeExtractionService.ExtractFromRvt` path stays unchanged.
* **`doc.IsModifiable` check on staging.** `EditFamily` requires the document to be non-modifiable. The existing `ImportActiveFile` flow already operates after any user transactions are committed (the button is in a popup), so we did not re-introduce the defensive `IsModifiable` check that ADR-026 added. If a future caller hits "family is in modifiable state" they will see the underlying Revit exception in the log.
* **A real progress bar during staging.** Status messages report each phase. A progress bar would require non-trivial refactoring of `StageLoadableFamilyFromProject` to yield; left for a future phase.

## Phase 2 (implemented 2026-07-31): Placement handlers for all 14 system categories

The `SystemCategoryRegistry` (now in `src/SmartCon.Revit/FamilyManager/SystemCategoryRegistry.cs`,
public by `TemplateCollisionResolver` precedent) exposes placement handlers for **all 14
system categories**. Every staged mini-project now carries placed instances of its types —
the reference is visually inspectable and the snapshot extractor reads types from instances
(first branch of `ExtractSystemCategoryFromStagedProject`, not the "all category types" fallback).

### Category → API matrix

| Category | Placement API | Versions |
|---|---|---|
| `OST_PipeCurves` | `Pipe.Create` | All |
| `OST_FlexPipeCurves` | `FlexPipe.Create` | All |
| `OST_DuctCurves` | `Duct.Create` | All |
| `OST_FlexDuctCurves` | `FlexDuct.Create` | All |
| `OST_Conduit` | `Conduit.Create` | All |
| `OST_CableTray` | `CableTray.Create` | All |
| `OST_Walls` | `Wall.Create` | All |
| `OST_Floors` | `Floor.Create(CurveLoop)` / legacy `NewFloor` | R22+ / R19–R21 |
| `OST_Roofs` | `NewExtrusionRoof` (open gable profile) — **not** `NewFootPrintRoof`, see trap #3 below | All |
| `OST_Ceilings` | `Ceiling.Create(CurveLoop)` | **R22+ only** → version gate |
| `OST_Stairs` | `StairsEditScope` + `ChangeTypeId` + `StairsRun.CreateStraightRun` | All |
| `OST_StairsRailing` | `Railing.Create(CurveLoop)` | **R25+ only** → version gate |
| `OST_PipeInsulations` | programmatic host `Pipe` + `PipeInsulation.Create` | All |
| `OST_DuctInsulations` | programmatic host `Duct` + `DuctInsulation.Create` | All |
| `OST_DuctLinings` (#197) | programmatic host `Duct` + `DuctLining.Create` | All |
| `OST_Wire` (#199) | `Wire.Create(WiringType.Chamfer, …)` + plan view (`ViewPlan.Create` fallback) | All |

> **Railing category correction (Issue #182, 2026-08-03):** railing instances and
> `RailingType` live in **`OST_StairsRailing`**, not `OST_Railings` (Tammik,
> tbc/a/0619_retrieve_railings). The registry used `OST_Railings` until 2026-08-03,
> which made railings unpickable in the element picker and invisible to
> `AnalyzeActiveProject`. Field databases with the old id cannot exist (the
> category was unimportable both ways) — no migration needed.

### Per-Revit support summary (version gates)

| Revit | Blocked categories |
|---|---|
| 2019–2021 | `OST_Ceilings` (needs 2022+), `OST_StairsRailing` (needs 2025+) |
| 2022–2024 | `OST_StairsRailing` (needs 2025+) |
| 2025+ | none — all 16 categories |

### Not supported (tracked as follow-up issues)

> **Product decision (2026-08-03):** неподдерживаемая категория скрывается
> ЦЕЛИКОМ — не предлагается в пикере и не попадает в batch-диалог (источник
> правды: `SystemCategoryRegistry.Entries` + `SystemCategoryPlacementAvailability`).
> Type-only режимов и per-category исключений НЕ делаем — категория
> активируется только когда её можно поддержать end-to-end. Причина
> фиксируется комментарием в коде со ссылкой на эту таблицу и issue.

| Category | Issue | Reason |
|---|---|---|
| `OST_Ramps` (пандусы) | #198 | no public `Ramp.Create` API (verified revitapidocs 2021–2026) — waiting for Revit API; hidden entirely per the product decision above (NO type-only fallback) |
| `OST_CurtainWallPanels` (панель витража) | #196 | non-editable family — исключена из loadable-импорта сканером (`Family.IsEditable == false`); системный путь (типы панелей) — возможный follow-up |

### Decisions (Phase 2)

1. **Handlers manage their own transactions.** `StairsEditScope` cannot be started inside
   an active transaction (revitapidocs), so the registry dropped the old
   "one outer transaction for the whole grid" model: each handler opens its own
   `ITransactionService.RunInTransaction` (I-03); the stairs handler drives the scope
   and runs the inner transaction through the service inside it. This was the signature
   change pre-announced in the old Phase 2 TODO.
2. **Version gate blocks the import, not just the placement.** Categories whose placement
   API is missing on the running Revit (ceilings <2022, railings <2025) are excluded from
   "Импорт активного файла" / "Импорт выделенных элементов" with a styled `ShowInfo`
   dialog listing each blocked category and its required version: an instance-less
   mini-project is not a valid reference. Single source of truth:
   `SystemCategoryPlacementAvailability` (Core, `BuiltInCategory → minVersion`).
   Sync from an existing reference on an older Revit is NOT gated (CompoundStructure
   sync works everywhere).
3. **Roofs are extruded, not footprint.** `NewFootPrintRoof` throws
   `Autodesk.Revit.Exceptions.ArgumentNullException` in a background document
   (staged mini-project is never active; verified by probe test in real Revit —
   the legacy Creation method needs a UI context). `NewExtrusionRoof` with an OPEN
   profile (closed loops are rejected as "Invalid profile") works in background
   documents on all versions. `ReferencePlane` is created on the template's first
   `ViewPlan` (`doc.ActiveView` is null for background documents).
   **Legacy `NewFloor` (R19–R21) assessed as safe:** unlike the footprint API it
   was the mainstream floor-creation API for years (2019–2021 docs have no
   UI-context caveats; batch floor creation in background documents was standard
   practice). A reflection probe on Revit 2023 is not applicable — the method was
   removed from the public API surface after 2022. Final confirmation belongs to
   the manual test matrix on a machine with Revit 2019–2021 (no such Revit on the
   current dev machine — the integration suite ran via `-p:RevitVersion=2023`,
   which compiles the `Floor.Create` branch).
4. **Stairs: `ChangeTypeId` after `StairsEditScope.Start`, railings removed AFTER
   `scope.Commit`.** `Start` creates an empty stairs with the default type; the handler
   switches it to the catalog type, adds one straight run sized from
   `MaxRiserHeight`/`MinTreadDepth` and the level height (an over-long baseline makes
   Revit pad the run with extra risers past the top level). Default railings
   materialize only at `scope.Commit` — they are deleted in a follow-up transaction so
   the reference contains exactly the catalog type.
5. **Insulation hosts are created programmatically.** `PipeInsulation.Create` /
   `DuctInsulation.Create` require a host (pipe/duct/fitting/accessory); the mini-project
   has none, so the handler places a 1m host from the first template type in the same
   transaction (base system types always exist and cannot be deleted — product invariant).
   The host is harmless for extraction/hash (they filter by the insulation category).
   Note: an empty default template has NO insulation types at all (they materialize only
   on the UI "Add Insulation" click, CF-4720) — integration tests seed from MEP templates
   (`Systems/Mechanical/Plumbing-Default*.rte` ship PipeIns=2, DuctIns=2).
6. **`CanPlaceElementType` guard in the active project.** `PostRequestForElementTypePlacement`
   throws `ArgumentException` for interactively unplaceable types (insulations need a host).
   `SystemFamilyPlacementService` now checks `UIDocument.CanPlaceElementType` and reports
   `SystemPlacementResult.LoadedManualPlacementRequired` — the synced type stays in the
   project and the user is told to place it manually instead of a crashed command.

### Test coverage

`src/SmartCon.IntegrationTests/FamilyManager/SystemCategoryPlacementTests.cs` — 9 tests:
instance per category with the target type; host↔insulation link (`HostElementId`,
`GetInsulationIds`); stairs `ChangeTypeId` + run + zero default railings; version-gate
matrix (registry + Core); staged extraction finds types via placed instances.
Suite: **R25 72/72, net48 (Revit 2023) 71+1skip** (railing skipped below 2025 by design).

## Test count

* 1215 → **1219** unit tests, all green.
  * +4 `LoadableFamilyInfoTests` (record equality, field round-trip)
  * Tests for `SelectedElementsAnalysis` were attempted but the record transitively requires `RevitAPI.dll` (`BuiltInCategory` value-type carrier) at construction time, which is excluded from the test bin. Skipped.
* **1219** tests after 2026-06-08 revision (no new tests — `LoadableFamilyScanner` requires Revit API; refactor is behaviour-equivalent on the contract level, covered by manual Revit tests).
* **Phase 2 (2026-07-31):** 2525 unit tests + 72 integration tests (R25) / 71+1skip (net48 Revit 2023), all green. `SystemCategoryPlacementAvailability` is not unit-tested for the same `BuiltInCategory`-carrier reason as `SelectedElementsAnalysis` — covered by the integration gate tests.
