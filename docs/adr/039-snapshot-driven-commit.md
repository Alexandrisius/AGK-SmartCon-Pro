# ADR-039: Snapshot-driven Commit — единое открытие файла (Phase 27)

**Status:** accepted
**Date:** 2026-06-29
**Phase:** 27 (A + B)
**Supersedes (partially):** ADR-033 (bake-in — bake перемещён из Commit в Prepare для UC-1)

## Контекст

### Проблема

В модуле FamilyManager при импорте семейств (UC-1: файлы с диска, UC-2: активный
.rfa, UC-3: активный .rvt проект, UC-4: выделленные элементы) каждый семейство
открывалось **несколько раз**:

| UC | Открытий на семейство | Где |
|---|---|---|
| UC-1 с .txt | 3 | Prepare (OpenDocumentFile) + Commit (BakeAsync.OpenDocumentFile) + Commit (ExtractFromManagedFileAsync.OpenDocumentFile) |
| UC-2 | 2 | Prepare (активный doc) + Commit (ExtractFromManagedFileAsync.OpenDocumentFile) |
| UC-3/UC-4 (loadable) | 3 | Prepare (EditFamily) + Commit (ResolveTypesFromRfa.OpenDocumentFile) + Commit (ExtractFromManagedFile.OpenDocumentFile) |
| UC-3/UC-4 (system) | 2 | Prepare (EditFamily) + Commit (ExtractFromRvt.OpenDocumentFile) |

В логе UC-3 с 42 loadable семействами фиксировалось **84 повторных открытия** в
Commit-фазе (42× LoadableResolver + 42× ExtractFromManagedFile), каждое по
100-330мс — суммарно ~12 секунд лишней работы + 42 MFC family-upgrade диалога.

### Root cause

Phase 1 Prepare уже открывала документ, извлекала snapshot (типы, параметры,
значения, геометрия) и вычисляла content hash. Но snapshot **терялся** при
маппинге через batch dialog — Phase 3 Commit не имел доступа к snapshot и
вынужден был открывать файл повторно для:
1. `LoadableFamilyTypeResolver.ResolveTypesFromRfa` — получение типов
2. `RevitFamilyDataExtractionService.ExtractFromManagedFile` — extraction значений
3. `IFamilyTypeCatalogBaker.BakeAsync` — bake Type Catalog (UC-1 с .txt)

## Решение

### Phase 27A (commit 8804e89): Snapshot-driven Commit для UC-2/UC-3/UC-4

**Идея:** прокинуть snapshot через весь dialog round-trip и использовать его в
Commit через pure-C# маппер, без повторного открытия файла.

**Изменения:**
- `SnapshotExtractionMapper` (NEW, pure C#) — маппер `FamilySnapshot→FamilyExtractionResult`,
  `→FamilyTypeDescriptor[]`, `SystemFamilySnapshot→FamilyExtractionResult`
- Snapshot прокинут через: `PreparedFamilyItem` → `FamilyBatchImportItem` →
  `FamilyBatchImportRow` → `GetResultItems` → orchestrator/extractor
- `LoadableFamilyImportOrchestrator`: `ToTypeDescriptors` из snapshot вместо
  `ResolveTypesFromRfa` (42× re-open устранено)
- `ExtractAttributesForLoadableTasks`: `ToExtractionResult` из snapshot вместо
  `ExtractFromManagedFile` (42× re-open устранено)
- `SystemFamilyAttributeExtractor`: `ToExtractionResult` из snapshot вместо
  `ExtractFromRvt` (2× re-open устранено)

**Результат UC-3 лога:** 0 `OpenDocumentFile` в Commit, все 44 items "from
snapshot, no re-open", duration=40.4s (было ~52s).

### Phase 27B (commit b71473b): Bake в Prepare + snapshot-driven UC-1

**Идея:** перенести bake Type Catalog из Commit в Prepare — bake в уже открытом
документе, snapshot содержит baked типы, hash по baked содержимому.

**Изменения:**
- `IFamilyTypeCatalogBaker.BakeInExistingDocumentAsync(object familyDoc, catalog, ct)`
  — bake в уже открытом family document (без OpenDocumentFile/SaveAs/Close).
  `object familyDoc` — opaque Document (I-09).
- `RevitFamilyTypeCatalogBaker`: реализация через существующий private
  `BakeInFamilyDocument` (RunInTransaction + Regenerate, без file I/O)
- `FamilyImportPreparationService`: inject `IFamilyTypeCatalogBaker`;
  `PrepareSingleFileAsync` после OpenDocumentFile → проверка `.txt` sidecar →
  `BakeInExistingDocumentAsync` → snapshot содержит baked типы → hash по baked
- `StageLoadableFamiliesFromHeldOpenAsync` (NEW) — SaveAs каждого held-open
  документа в precomputed managed path после одобрения диалога
- `ShowBatchImportDialogAsync`: precompute triple + staging + snapshot-based
  extraction (вместо `ExtractTypesForImportedFamilies`)
- `CloseAllPreparedDocumentsAsync`: `IsValidObject` проверка перед `Close(false)`

**Результат UC-1 лога:** 0 `Bake.OpenDocumentFile`, 0 `ExtractFromManagedFile`,
2 `OpenDocumentFile` (оба в Prepare — единичные открытия).

### Bug fix: ADR-036 Bug #2 regression

При замене `ExtractAttributesForImportedFamilies` (FireAndForget) на
`ExtractAttributesForLoadableTasks` (sync) в `ProcessFamilyImportAsync` (UC-2),
порядок `LoadTreeAsync` → extraction остался старым. `LoadTreeAsync` вызывался
**ДО** extraction → дерево перезагружалось с 0 типов → extraction сохранял типы
в БД → дерево не перезагружалось → пользователь видел семейство без типоразмеров.

**Фикс:** перенёс `LoadTreeAsync` после `ExtractAttributesForLoadableTasks`
(FamilyEdit.cs:416→432).

### Bug fix: snapshot matching by CatalogItemId

В `ShowBatchImportDialogAsync` поиск `batchItem` для extraction шёл по
`string.Equals(s.FileName, imported.FileName)` — но `FamilyImportResult.FileName`
= `finalMetadata.FileName` (С расширением `.rfa`), а `FamilyBatchImportItem.FileName`
= `Path.GetFileNameWithoutExtension(filePath)` (БЕЗ расширения). `FirstOrDefault`
всегда возвращал null → "No snapshot" → типы не сохранялись.

**Фикс:** поиск по `PrecomputedCatalogItemId` (надёжный ключ), с fallback по
`ExistingCatalogItemId` и `ManagedFilePath`.

## Ключевые решения

1. **UniqueId для loadable = null** — корректное поведение, совпадает с develop.
   `FilteredElementCollector(familyDoc).OfClass(typeof(FamilySymbol))` всегда
   возвращает null для собственных типов (FamilySymbol не существует в family
   document, только в project document). Stale Detection использует VersionLabel
   (не UniqueId). `FamilyPlacementDropHandler` использует
   `!string.IsNullOrEmpty(UniqueId)` как признак system family — заполнение
   UniqueId для loadable **сломает placement**.

2. **Bake в Prepare, не в Commit** — bake модифицирует документ в памяти (создаёт
   типы). SaveAs из held-open записывает baked версию. Hash вычисляется по baked
   содержимому → два одинаковых .rfa с разными .txt = разные hash (правильная
   дедупликация).

3. **`Document` как `object` в Core интерфейсе** (I-09) —
   `BakeInExistingDocumentAsync(object familyDoc, ...)` принимает Document как
   opaque parameter. Core не вызывает его методы — только передаёт в Revit-слой.

4. **`IsValidObject` перед `Close(false)`** — после staging (SaveAs +
   ReleaseDocument) некоторые документы могут быть invalidated by Revit.
   `IsValidObject` проверка предотвращает "The referenced object is not valid"
   warnings.

## Слои

| Layer | Изменения |
|---|---|
| `SmartCon.Core/Services/Interfaces/` | `IFamilyTypeCatalogBaker.BakeInExistingDocumentAsync` (NEW) |
| `SmartCon.Core/Models/FamilyManager/` | `FamilyBatchImportItem.LoadableSnapshot/SystemSnapshot` (Phase A), `PreparedFamilyItem.LoadableSnapshot/SystemSnapshot` (Phase A), `FamilyTypeSnapshot.UniqueId` (Phase A) |
| `SmartCon.Revit/FamilyManager/` | `RevitFamilyTypeCatalogBaker.BakeInExistingDocumentAsync` (NEW) |
| `SmartCon.FamilyManager/Services/` | `SnapshotExtractionMapper` (NEW, Phase A), `FamilyImportPreparationService` (+baker inject, +bake in Prepare), `CloseAllPreparedDocuments` (+IsValidObject) |
| `SmartCon.FamilyManager/ViewModels/` | `FamilyManagerMainViewModel.Import` (+staging, +snapshot extraction, -ExtractTypesForImportedFamilies), `FamilyManagerMainViewModel.FamilyEdit` (LoadTree order fix), `FamilyManagerMainViewModel.Extract` (-ExtractFromManagedFileAsync) |

## Verification

### Build
- R25: 0 warnings / 0 errors
- R24: 0 warnings / 0 errors
- R21: 0 warnings / 0 errors

### Tests
- 1632/1632 passed (12 new SnapshotExtractionMapperTests в Phase A)

### Stress-test log (3058 строк)
- 0 ERR
- 8 WARN (все "snapshot has 0 types" — ожидаемо для семейств без FamilyManager.Types)
- 0 "No snapshot" (bug fix by CatalogItemId matching)
- 0 Bake.OpenDocumentFile (bake в Prepare)
- 0 ExtractFromManagedFile (snapshot-based extraction)
- 2 OpenDocumentFile (оба в Prepare — единичные открытия)
- UC-2: extraction → SyncTypes inserted=3 → LoadTree (correct order)
- UC-3: extraction 42 items → LoadTree (correct order)

## Sources

- [ADR-033](033-bakein-type-catalog.md) — Type Catalog bake-in (bake перемещён в Prepare)
- [ADR-036](036-active-family-type-sync.md) — Sync типов при импорте активного семейства (Bug #2 regression fix)
- Jeremy Tammik / thebuildingcoder.com — `Document.SaveAs`, `EditFamily`, family document transactions
- Autodesk forums — `Document.IsValidObject`, `OpenDocumentFile` + family document
