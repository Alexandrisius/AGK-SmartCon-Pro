# ADR-040: OverwriteCurrent — семантика перезаписи текущей версии

**Status:** accepted
**Date:** 2026-06-29
**Phase:** 28
**Related:** ADR-016 (Managed Storage Immutability — OverwriteCurrent exception),
ADR-036 (SyncTypesAsync collapse to current version), ADR-039 (snapshot-driven Commit)

## Контекст

### Проблема

В модуле FamilyManager режим импорта "Перезаписать текущую версию"
(`FamilyBatchImportAction.OverwriteCurrent`) был сломан для всех Use Cases:

| UC | Симптом | Root Cause |
|---|---|---|
| UC-2 (loadable, активное .rfa) | SQLite UNIQUE constraint на `catalog_versions` | `ProcessFamilyImportAsync` вызывал `ImportFileAsync` напрямую (не через `ImportBatchAsync`). `ImportFileAsync` ВСЕГДА `INSERT INTO catalog_versions` (не OR REPLACE/UPSERT). Для OverwriteCurrent `versionLabel = ExistingVersionLabel` (v1) уже существует → UNIQUE violation на `(catalog_item_id, version_label, revit_major_version)`. |
| UC-3 (system, активный .rvt) | Ничего не происходит — версия не инкрементируется, изменения не применяются | Каскад из 4 багов: (1) staging создавал orphan v2 файл (precomputer возвращает vN+1 для Existing); (2) `OverwriteCurrentAsync` не UPDATE `catalog_versions` — content_hash/types_count/published_at_utc устаревали; (3) `PrepareManagedRfaAsync` видел staged v2 внутри `{dbRoot}/files/` → `return null` → v1 не перезаписывался, `SyncTypesAsync` не вызывался; (4) `SystemFamilyImportOrchestrator` matching по `Path.GetFileName` (с расширением) vs `r.FileName` (без расширения) → `matchingResult=null` → `SaveSystemTypesAsync` не вызывался. |
| UC-3 (loadable) / UC-4 | Те же root causes, что UC-3 system | Те же orchestrator/staging/OverwriteCurrentAsync пути. |

### История

Баг не регрессия content-hash дедупликации (Phase 1-10, ADR-039). Латентный
дефект с момента `6991189` (2026-05-29, "Batch Import v4 with
Increment/Overwrite/Skip modes") — первого коммита, добавившего
`OverwriteCurrentAsync`, `InsertVersionAsync`, `isOverwrite` блок. В `develop`
структура `ProcessFamilyImportAsync` и `OverwriteCurrentAsync` идентична `main`.
Content-hash дедупликация просто чаще создаёт `Existing`-статус (раньше без
content-hash все re-imports были "Existing by name", но пользователи реже
сталкивались с OverwriteCurrent т.к. не видели явного Duplicate-статуса).

### Исследование

- Лог `smartcon.log` от 22:54-22:55 (UC-2 loadable UNIQUE constraint + UC-3
  system silent no-op) дал полную runtime-картину
- Анализ кода через субагентов подтвердил 4 root causes
- Git-история (`git log -S`, `git show develop:...`) подтвердила идентичность
  структуры в develop и feature
- Тест `ImportBatchAsync_OverwriteCurrent_UpdatesCatalogItemName` покрывал
  OverwriteCurrent через `ImportBatchAsync` (правильный путь), но НЕ покрывал
  реальный путь UC-2 через `ImportFileAsync`

## Решение

### Семантика: UPDATE существующей версии

При `OverwriteCurrent` запись в `catalog_versions` UPDATE (а не INSERT новой
строки). Сохраняется `id` версии → FK references (`family_types.version_id`)
не ломаются, история не теряется. Это "true overwrite" — та же версия,
обновлённое содержимое.

### Архитектура: переиспользование `ImportBatchAsync` для всех UC

Все 4 Use Cases (`UC-1` batch dialog, `UC-2` active .rfa, `UC-3` active .rvt,
`UC-4` selected elements) проходят через `ImportBatchAsync` для OverwriteCurrent.
`ImportBatchAsync` диспетчеризует в `OverwriteCurrentAsync` (существующая логика,
`LocalFamilyImportService.cs:510`).

**UC-2 fix:** `ProcessFamilyImportAsync` для OverwriteCurrent создаёт
single-item `FamilyBatchImportItem` с `Action=OverwriteCurrent` и вызывает
`ImportBatchAsync` (вместо `ImportFileAsync`).

**UC-3/UC-4 fix:** staging проверяет `Action==OverwriteCurrent` → использует
`ExistingVersionLabel` path (v1, не vN+1 из precomputer). V1 файл перезаписывается,
orphan v2 не создаётся.

### `OverwriteCurrentAsync` — UPDATE `catalog_versions`

Добавлен `UpdateVersionAsync` (новый приватный метод):
```sql
UPDATE catalog_versions
SET content_hash = @contentHash,
    hash_format_version = @hashFmt,
    types_count = @typesCount,
    parameters_count = @paramsCount,
    published_at_utc = @publishedAtUtc
WHERE id = @versionId
```

`OverwriteCurrentAsync` также извлекает `finalMetadata` из перезаписанного файла
(`_metadataService.ExtractAsync(absolutePath, ct)`) для `types_count`/
`parameters_count`. `content_hash`/`hash_format_version` берутся из
`FamilyBatchImportItem.ContentHash`/`HashFormatVersion` (вычислены в Prepare-фазе).

### Staging — OverwriteCurrent path

Staging helpers (`StageSystemFamiliesFromMetadataAsync`,
`StageLoadableFamiliesFromMetadataAsync`, `StageLoadableFamiliesFromHeldOpenAsync`)
проверяют `item.Action == OverwriteCurrent && ExistingCatalogItemId != null &&
ExistingVersionLabel != null` → `managedPath = ComputeManagedFilePath(
ExistingCatalogItemId, ExistingVersionLabel, ...)`. V1 файл перезаписывается
(ReadOnly снимается → SaveAs/CreateCleanProject → ReadOnly возвращается).
Precomputer возвращает vN+1 (pure compute, не знает про Action) — staging
решает какой path использовать.

### Orchestrator matching — по CatalogItemId

`SystemFamilyImportOrchestrator` и `LoadableFamilyImportOrchestrator` matching
по `CatalogItemId` (точный ключ, не зависит от filename/расширения):
```csharp
var expectedCatalogItemId = item.ExistingCatalogItemId ?? item.PrecomputedCatalogItemId;
var matchingResult = importResult.Results.FirstOrDefault(r =>
    !string.IsNullOrEmpty(r.CatalogItemId) &&
    string.Equals(r.CatalogItemId, expectedCatalogItemId, StringComparison.OrdinalIgnoreCase));
```
Fallback по `Path.GetFileNameWithoutExtension(r.FileName)` (defensive, для edge
cases когда CatalogItemId не задан — например UC-1 New path).

### I-16 (Managed Storage Immutability) — уточнение

I-16 формулируется точнее: ".rfa/.rvt файлы в managed storage — read-only
после импорта, КРОМЕ явного OverwriteCurrent (выбранного пользователем через
batch dialog)". См. [ADR-016](016-familymanager-readonly-files.md) §"Exception:
OverwriteCurrent".

## Слои

| Layer | Изменения |
|---|---|
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.Database.cs` | ADD `UpdateVersionAsync` (NEW); FIX `OverwriteCurrentAsync` (ADD `finalMetadata` extraction + `UpdateVersionAsync` call) |
| `SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.FamilyEdit.cs` | FIX `StageSystemFamiliesFromMetadataAsync` + `StageLoadableFamiliesFromMetadataAsync` (OverwriteCurrent path); FIX `ProcessFamilyImportAsync` (ImportBatchAsync для OverwriteCurrent) |
| `SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Import.cs` | FIX `StageLoadableFamiliesFromHeldOpenAsync` (OverwriteCurrent path) |
| `SmartCon.FamilyManager/Services/SystemFamilyImportOrchestrator.cs` | FIX matching logic (по CatalogItemId) |
| `SmartCon.FamilyManager/Services/LoadableFamilyImportOrchestrator.cs` | FIX matching logic (по CatalogItemId) |
| `SmartCon.Tests/FamilyManager/Repository/LocalFamilyImportServiceTests.cs` | Расширить существующий тест + 2 новых (UpdateVersionAsync, OverwriteCurrent full flow) |
| `SmartCon.Tests/FamilyManager/ViewModels/` | NEW: 4 теста для VM + orchestrator |

**Не меняются:**
- `LocalFamilyImportPrecomputer.cs` — precomputer остаётся pure compute,
  возвращает vN+1 для Existing. Staging решает какой path использовать.
- `IFamilyImportService` — нет нового метода, переиспользуем `ImportBatchAsync`.
- `LocalFamilyImportService.cs` `ImportFileAsync` — остаётся как есть для
  New/IncrementVersion path (всегда INSERT новой версии).

## Verification

### Build
- R25: 0 warnings / 0 errors
- R24: 0 warnings / 0 errors
- R21: 0 warnings / 0 errors

### Tests
- Все существующие тесты pass (включая расширенный `ImportBatchAsync_OverwriteCurrent_UpdatesCatalogItemName`)
- 6 новых тестов pass (UpdateVersionAsync, orchestrator matching, staging path, UC-2 routing, types sync, negative test)

### Manual Revit test (требуется пользователю)
- UC-2 loadable OverwriteCurrent: импорт активного .rfa → изменения применяются, версия не инкрементируется, content_hash обновляется, типы синхронизируются
- UC-3 system OverwriteCurrent: импорт активного .rvt с системной категорией → изменения применяются, версия не инкрементируется, типы синхронизируются
- UC-3 loadable OverwriteCurrent: аналогично UC-2
- UC-4 OverwriteCurrent: аналогично UC-3

## Sources

- [ADR-016](016-familymanager-readonly-files.md) — Managed Storage Immutability (OverwriteCurrent exception)
- [ADR-036](036-active-family-type-sync.md) — SyncTypesAsync collapse to current version
- [ADR-039](039-snapshot-driven-commit.md) — Snapshot-driven Commit (precomputer возвращает vN+1 для Existing)
- `smartcon.log` 2026-06-29 22:54-22:55 — runtime-картина бага
- Git-история: `6991189` (Batch Import v4), `bd768f0` (precomputer), `5240214` (content-hash dedup start)
