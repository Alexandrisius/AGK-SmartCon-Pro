# ADR-041: FamilyManager Active Version Management

**Status:** accepted (rev #1 + rev #2 + rev #3 + rev #4 + rev #5)
**Date:** 2026-06-30
**Phase:** 28 (v2.0.0)
**Supersedes:** частично — [ADR-036 rev #3](036-active-family-type-sync.md#rev-3-2026-06-25-case-b--collapse-to-current-вместо-join-on-versionlabel) (collapse-to-current семантика отменена в rev #2)
**Related:** ADR-016 (Managed Storage Immutability — DeleteVersion exception),
ADR-022 (RBAC), ADR-030 (Stale Detection v2), ADR-035 (SHA-256 dedup removal),
ADR-036 (Active family type sync — superseded rev), ADR-039 (Snapshot-driven Commit),
ADR-040 (OverwriteCurrent semantics), Issue #88 (content-hash dedup)

## Revisions

### rev #5 (2026-06-30): UX Versions tab — compact tooltip-only preview (UC-2), cosmetic fixes

**Problem:** После аудита ADR-041 (rev #1–4) и двух итераций UI-рефакторинга
пользователь сформулировал требования: «всё должно быть компактно и строго».
Конкретно:

1. **UC-2 «просмотр выбранной версии без переключения активной».** Первая
   попытка с `LoadPreviewForSelectedVersionAsync` (перезагружал вкладку
   Атрибуты) вызывала layout-shift и тяжёлый reload при каждом клике —
   пользователь явно отверг: «не думаю что просто клик по строчке должен
   рендерить все новые значения окна свойств». Вторая попытка с
   `RowDetailsTemplate` (native WPF pattern от MS Learn) тоже отвергнута как
   слишком громоздкая. Финальное решение: **никаких row details / banner
   / preview reload** — только tooltip на ячейке «Типы», который разворачивается
   при наведении и показывает полный список типов через запятую.

2. **Колонка «Типы» показывала прочерк** хотя типы реально есть (в дереве
   и в выпадающем списке). Причина: `FamilyCatalogVersion.TypesCount`
   (из `catalog_versions.types_count`) часто NULL — столбец не
   заполняется при некоторых режимах импорта. При этом `family_types`
   строки реально существуют.

3. **Выравнивание данных «по левой стороне»** во всех колонках. В batch
   диалоге короткие колонки (RevitVersion, TypeCount, Status) уже
   центрированы через `ElementStyle CenterCell` — здесь это было
   пропущено.

**Дополнительно (log cosmetics):**
- `LocalCatalogProvider.Versions.cs:198` в catch-блоке `SetActiveVersionAsync`
  логировал `SmartConLogger.Error(...)` и затем `throw;` → двойное
  логирование одного исключения (повторно логировалось в
  `MakeActiveAsync` catch). Исправлено: убран Error-лог, оставлен `throw;`
  (паттерн уже применён в `DeleteVersionAsync` line 334-338).
- `LocalCatalogProvider.Versions.cs:363` Warn содержал полный абсолютный
  путь к `versionDir`. Заменён на leaf name (`Path.GetFileName`) — правило
  L8 (FilePath в scope = Path.GetFileName). CatalogItemId + VersionLabel
  уже в scope (lines 207-209), так что message не теряет идентификации.

**Fix — четыре подзадачи (compact simplification):**

1. **`FamilyVersionRow.TypeNames` + `TypeNamesTooltip` + `TypesCountDisplay`**
   (NEW properties). `FamilyVersionRow` хранит не только `TypesCount` (поле
   из БД, может быть NULL), но и список имён типов
   `IReadOnlyList<string> TypeNames` (по умолчанию пустой). Загрузка имён
   — через `IFamilyTypeRepository.GetTypesForItemVersionAsync
   (catalogItemId, row.VersionId, ct)` (метод уже существует с rev #2).
   `TypesCountDisplay` — computed string: возвращает
   `TypeNames.Count.ToString()` если список не пуст, иначе fallback на
   `TypesCount`, иначе `"—"`. Это решает проблему прочерка — реальное число
   типов берётся из `family_types` строк, а не из ненадёжного
   `catalog_versions.types_count`.
   `TypeNamesTooltip` — `string.Join(", ", TypeNames)` для tooltip на
   ячейке `Types`.
   Один roundtrip на версию — типичный item имеет 3-5 версий, overhead
   ~10-30ms.

2. **Удалён `LoadPreviewForSelectedVersionAsync` + `IsPreviewMode` /
   `PreviewVersionLabel` / `_previewLoadCts`** (rollback обеих предыдущих
   попыток — preview reload и row details). `OnSelectedVersionRowChanged`
   вернулся к минимальной реализации — только `NotifyCanExecuteChanged`
   для кнопок. Атрибуты вкладки остаются про **активную** версию до тех
   пор пока пользователь явно не нажмёт «Сделать активной» — это явный
   выбор пользователя и соответствует его запросу.

3. **`ElementStyle` для центрирования** (per MS Learn / SO best practice
   https://stackoverflow.com/questions/720732/text-alignment-in-a-wpf-datagrid
   + https://stackoverflow.com/questions/63513914/can-i-have-a-datagrid-styles-content-presenter-vary-by-column).
   В `Window.Resources` добавлены `CenteredTextCell` и `LeftTextCell`
   `Style TargetType=TextBlock` с `TextAlignment` + `HorizontalAlignment`
   + `VerticalAlignment`. Все 4 текстовых column'ы (`ColVersionLabel`,
   `ColVersionRevit`, `ColVersionDate`, `ColVersionTypes`) применяют
   `ElementStyle="{StaticResource CenteredTextCell}"`. Per MS Learn:
   `ElementStyle` — единственный правильный способ центрировать **текст**
   в ячейке (`CellStyle` центрирует только контейнер ячейки, а не сам
   TextBlock внутри, и потому выглядит "funky" по SO).

4. **Стилизация DataGrid** — выровнена с batch-диалогом:
   - `CellStyle="{DynamicResource CompactDataGridCell}"` убирает агрессивный
     синий фон выделенной ячейки (вижуал совпадает с `FamilyBatchImportView`);
   - `BorderBrush="{DynamicResource BorderAltBrush}"` вместо несуществующего
     `BorderBrush`;
   - active badge (`DataGridTemplateColumn.CellTemplate`) —
     `Background="{DynamicResource SavedSuccessBrush}"` (зелёный)
     вместо `AccentBrush` (синий) — меньше визуального конфликта с
     выделением строки + `HorizontalAlignment="Center"` (было `Left`);
   - `EnableRowVirtualization="False"` — в Properties dialog обычно <20
     версий, virtualization не нужна, зато без неё selection/hover работает
     корректнее.

**Логирование:** Обновлены категория `FMProperties` — убран scope
`LoadPreviewForSelectedVersionAsync` (метод удалён), все остальные scope'ы
сохранены (`LoadVersionsAsync`, `MakeActiveAsync`, `DeleteVersion`,
`SetActiveVersionAsync`, `DeleteVersionAsync` в repository).

**Ложная тревога аудита (StaleDetector):** В аудите был спекулятивный
`MEDIUM` риск «StaleDetector не инвалидируется после MakeActive в Properties
dialog». Проверено и опровергнуто: `StaleDetector.ComputeReason`
(`SmartCon.FamilyManager/Services/Stale/StaleDetector.cs:357-383`) принимает
`FamilyCatalogItem` как параметр — НЕ кэширует `CurrentVersionLabel`. После
закрытия Properties dialog `FamilyManagerMainViewModel.OpenProperties` (line
60) вызывает `LoadTreeAsync()`, который перечитывает `catalog_items` из БД и
строит `FamilyLeafNodeViewModel` с актуальным `CurrentVersionLabel`
(`FamilyManagerMainViewModel.Tree.cs:126-127`). Следующая проверка stale
автоматически использует новое значение — **без правок в StaleDetector и без
отдельной инвалидации**. Это подтверждается ADR-041 §«Почему OverwriteCurrent и
Stale detection автоматически следуют за активной».

**Tests:** Тесты rev #5 не добавлены. Новая логика — `TypesCountDisplay`
(computed string property) + tooltip binding в XAML — не имеют unit-testable
surface (pure data display). Загрузка `TypeNames` в `LoadVersionsAsync`
покрыта manual Revit test UC-2. Все runtime-пути обеспечиваются manual test
UC-1–UC-4, UC-8.

### rev #4 (2026-06-30): MakeActive закрывает активный .rfa так же как другие режимы

**Problem:** После rev #2/`MakeActive` команда в Properties dialog не закрывала редактируемый rfa-документ. В режимах `IncrementVersion`/`OverwriteCurrent` после `SaveAs` вызывается `CloseFamilyDocumentAsync(saveAsPath)` и Revit закрывает .rfa — пользователь видит каталог. Для `MakeActive` `saveAsPath` = null (SaveAs не было) → я добавил `if (... && !isMakeActive)` который пропускал close → rfa оставался открытым. Это нарушает симметрию UX: пользователь закрыл batch диалог и ожидает что редактор закроется одинаково во всех режимах.

**Fix:** Убрано `!isMakeActive` условие. `pathToClose = saveAsPath ?? placeholderFilePath` (где `placeholderFilePath = prepared.SourcePath` — путь к активному документу). `CloseFamilyDocumentAsync` ищет документ по этому пути в `app.Documents` и вызывает `docToClose.Close(false)`. Для MakeActive активный документ (редактируемый .rfa) находится по `prepared.SourcePath` и закрывается; для других режимов закрывается SaveAs-target.

**Tests:** Требует регрессии в Revit UI — не покрыто unit-тестом (Revit-зависимый flow). Подтверждено в логе `17:23:14.569 Closed family file: ...v3\...rfa`.

### rev #2 (2026-06-30): per-version type storage + active-version-aware reads

**Supersedes:** [ADR-036 rev #3](036-active-family-type-sync.md#rev-3-2026-06-25-case-b--collapse-to-current-вместо-join-on-versionlabel). Collapse-to-current семантика rev #3 ADR-036 отменена этой ревизией: она уничтожала типы предыдущих версий и делала откат через `SetActiveVersionAsync` бессмысленным. Теперь типы хранятся per-version через V18 миграцию, и `SyncTypesAsync` использует version-scoped DELETE (см. ADR-036 rev #4+).

**Problem:** В исходной реализации rev #1 откат версии (`SetActiveVersionAsync`)
переключал `catalog_items.current_version_label`, но типы в `family_types`
оставались от предыдущей активной версии — то есть типы не обновлялись ни в дереве,
ни в окне свойств после отката.

**Root cause:** три слоя багов:

1. **БД:** `family_types` имел `UNIQUE(catalog_item_id, type_name)` — одно имя типа на catalog_item. Поэтому `SyncTypesAsync` при активном импорте n-ой версии удалял **все строки** (`DELETE FROM family_types WHERE catalog_item_id = @itemId` без фильтра по версии) — типы предыдущей версии физически исчезали. После отката некуда было возвращаться.

2. **Чтение UI:** `GetTypesForItemAsync` (окно свойств) и `GetAllTypesBatchAsync` (дерево) читали **все** типы без фильтра по активной версии. Даже если бы типы v1+v2 хранились в БД одновременно, UI показал бы их объединение с дубликатами.

3. **Run info:**  `FamilyPropertiesViewModel.LoadAttributesDataAsync` использовал `GetLatestRunAsync` (ORDER BY `started_at_utc DESC`) — всегда возвращал run последнего импорта, а не активной версии. После отката на v1 ImportRunInfo/attribute values всё равно отображали данные v2.

**Fix — пять подзадач:**

1. **V18 миграция (см. Schema migration v18 ниже)** меняет UNIQUE на `(catalog_item_id, version_id, type_name)` через CREATE-COPY-DROP-RENAME. Плюс partial UNIQUE INDEX `ix_family_types_orchestrator_unique` на `(catalog_item_id, type_name) WHERE version_id IS NULL` — защищает orchestrator case (project families без version handle) где SQLite NULL != NULL ломает таблиц-левел UNIQUE.

2. **SyncTypesAsync — version-scoped DELETE:**
   - Orchestrator case (versionId == null AND fileId == null): `DELETE ... WHERE version_id IS NULL` (не трогает versioned типы)
   - Active import case (versionId != null): `DELETE ... WHERE version_id = @versionId` (не трогает другие версии)
   - INSERT: `ON CONFLICT(catalog_item_id, version_id, type_name)` — то же имя типа в другой версии = отдельная строка

3. **`GetTypesForItemAsync` / `GetAllTypesBatchAsync`:** correlated subquery на active_version_id (через `catalog_items.current_version_label = catalog_versions.version_label`) с orchestrator fallback (`version_id IS NULL AND NOT EXISTS active version`).

4. **`MakeActiveAsync`** (FamilyPropertiesViewModel): после успеха перезагружает `LoadAttributesDataAsync` — `AvailableTypes` + `AttributeRows` + `ImportRunInfo` локально обновляются.

5. **`IFamilyDataImportRunRepository.GetLatestRunForActiveVersionAsync`** — возвращает run той версии, которая сейчас активна, а не последнюю по дате. Используется в `LoadAttributesDataAsync` чтобы `run.VersionId` соответствовал активной версии (для `GetValuesForItemAsync` filtering).

**Migration:** Все существующие БД мигрируются V18. Так как v2.0.0 — breaking change, без реальных пользователей, нет backward-compatibility забот. Типы от удаляемых версий (через `DeleteVersionAsync`) всё ещё каскадно очищаются FK CASCADE (V17) — никаких изменений в `DeleteVersionAsync` не требуется.

**Tests:**
- `Migrate_FromV17` → schema_version = '18', V18 FK constraint присутствует.
- Существующий `SyncTypesAsync_ActiveImport_CollapsesAllVersionsToCurrent` → переименован в `SyncTypesAsync_ActiveImport_PreservesOtherVersionsTypes` + перевёрнута семантика (теперь expects что чужие версии сохранены, не удалены).
- Новые: `SyncTypesAsync_SameTypeNameInDifferentVersions_BothStored`, `GetTypesForItemAsync_NoActiveVersion_FallsBackToOrchestratorTypes`, `GetAllTypesBatchAsync_ActiveVersionFilter_PicksPerItemActiveVersion`, `SyncTypesAsync_OrchestratorScope_DoesNotDeleteVersionedTypes`.
- Для MakeActive ImportActiveFile flow: `ImportBatchAsync_MakeActive_Duplicate_DoesNotCreateNewVersion` (новый, см. rev #3).

## Контекст

### Проблема

В модуле FamilyManager актуальная версия семейства определялась как последняя
по номеру (vN) — через `catalog_items.current_version_label`, который
автоматически обновлялся только при импорте новой версии. Это означало, что:

- Пользователь не мог «откатить» семейство к более ранней проверенной версии
  (v2 при существующих v3, v4) — операция отсутствовала в UI.
- Удаление неактивных версий отсутствовало полностью (только `DeleteItemAsync`
  удалял всё семейство целиком).
- `family_types.version_id` и `extracted_attribute_values.version_id` имели
  мягкие ссылки на `catalog_versions.id` без FK — удаление версии оставляло
  orphan-строки в `family_types`/`extracted_attribute_values`.

### Бизнес-сценарии

- **UC-1.** Просмотр всех версий во вкладке «Версии» окна свойств.
- **UC-2.** Просмотр свойств/файлов выбранной версии без переключения активной.
- **UC-3.** Переключение активной версии с подтверждением (двухшаговый UX).
- **UC-4.** Удаление неактивной версии (hard delete, без перенумерации).
- **UC-5.** Batch-опция `MakeActive` в batch dialog для строк со статусом `Duplicate`.
- **UC-6.** Импорт новой версии после отката создаёт vN+1 от последнего
  использованного номера (v5 в примере v1..v4 с активной v2).
- **UC-7.** `OverwriteCurrent` перезаписывает активную версию (может не совпадать с последней).
- **UC-8.** Stale detection сравнивает с активной версией.

## Решение

### 1. Переиспользование `current_version_label` как «активной версии»

Колонка `catalog_items.current_version_label` уже является единственным
источником правды для активной версии. Все пути (Stale Detector через
`FamilyCatalogItem.CurrentVersionLabel`, File Resolver через SQL JOIN
`cv.version_label = ci.current_version_label`, ES-маркер через
`resolved.VersionLabel`) автоматически начинают использовать новое значение
после `UPDATE catalog_items SET current_version_label = @label`.

**Новая колонка `is_active` НЕ вводится** — она дублировала бы
`current_version_label`, рассинхронизировалась бы со временем и потребовала
бы CHECK-ограничение с full-table scan миграцией.

### 2. V17 миграция: FK ON DELETE CASCADE на `version_id`

`family_types` и `extracted_attribute_values` пересоздаются с FK:

```sql
FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE
```

Используется SQLite 12-step generic procedure (CREATE-COPY-DROP-RENAME),
аналогично V15. Pre-delete orphan rows перед recreate гарантирует, что
копирование не нарушит FK.

Дополнительно добавляется композитный индекс
`ix_catalog_versions_item_label ON catalog_versions(catalog_item_id, version_label)`
для быстрого поиска версии по label в `GetVersionByLabelAsync` и `SetActiveVersionAsync`.

### 3. `SetActiveVersionAsync` — синхронизация `content_hash`

При переключении активной версии на старую:

1. Прочитать `content_hash`/`hash_format_version` целевой строки
   `catalog_versions` (по `(catalog_item_id, version_label)`, ближайшая Revit).
2. `UPDATE catalog_items SET current_version_label = @label,
   content_hash = @hash, hash_format_version = @fmt, updated_at_utc = @now`.
3. Файл на диске не трогается — он уже в storage.

Синхронизация `content_hash` критична для консистентности дедупликации:
после отката на v2 (с хэшем A) каталог должен показывать `Duplicate` при
повторном импорте файла с тем же содержимым, что v2. Без шага 2.content_hash
item-уровня остался бы от v4 (хэш B) → повторный импорт v2-контента показал бы
`Existing` вместо `Duplicate`.

### 4. `DeleteVersionAsync` — hard delete с FK CASCADE

Алгоритм:

1. **Защита:** `current_version_label == versionLabel` → отказ (нельзя удалить
   активную).
2. **Транзакция:**
   - `DELETE FROM family_assets WHERE catalog_item_id = @itemId AND version_label = @label`
     (привязка по `(item_id, version_label)`, не FK к versions).
   - `DELETE FROM catalog_versions WHERE catalog_item_id = @itemId AND version_label = @label`
     → FK CASCADE удаляет: `family_files`, `family_types`,
     `extracted_attribute_values`, `family_nested_shared_families`.
3. **После коммита — файловая очистка:** `Directory.Delete({dbRoot}/files/{itemId}/{label}/, recursive)`.
   Снятие ReadOnly (ADR-016). Retry на блокировки (как `DeleteItemAsync`).

### 5. `MakeActive` опция в batch dialog для `Duplicate`

Когда пользователь импортирует файл-дубликат (контент уже есть в одной из
версий), он может выбрать `MakeActive` — система переключит
`current_version_label` на найденную версию (через `MatchedVersionLabel`).
**Файл не сохраняется повторно** в storage.

В `LocalFamilyImportService.ImportBatchAsync` добавлена ветка для `MakeActive`:

```csharp
if (item.Action == FamilyBatchImportAction.MakeActive)
{
    if (string.IsNullOrEmpty(item.ExistingCatalogItemId) ||
        string.IsNullOrEmpty(item.MatchedVersionLabel))
    {
        result = new FamilyImportResult(Success: false, ...);
    }
    else
    {
        var setResult = await _catalogProvider.SetActiveVersionAsync(
            item.ExistingCatalogItemId!, item.MatchedVersionLabel!, ct);
        result = new FamilyImportResult(
            Success: setResult.Success,
            CatalogItemId: item.ExistingCatalogItemId,
            VersionLabel: item.MatchedVersionLabel,
            WasSkipped: true, ...);
    }
}
```

### 6. Перенумерация после удаления НЕ выполняется

Подтверждено Exa-исследованием (SemVer spec, SharePoint versioning docs):
номера удалённых версий не переиспользуются, «дырки» в нумерации допустимы и
считаются нормой. Следующий импорт даёт vN+1 от последнего использованного
номера (`ComputeNextVersionLabelAsync` читает MAX по `published_at_utc`).

### 7. Запрет удаления активной версии (FM-041-INV)

Инвариант FM-041-INV-01: у каждого `catalog_items` существует ровно одна
активная версия (`current_version_label` ≠ NULL). Удаление активной версии
запрещено на уровне UI (`CanDeleteVersion`) и БД (`DeleteVersionAsync` failure
result).

### 8. V18 миграция — per-version хранение типов (rev #2)

Архитектурное противоречие между `family_types` и `extracted_attribute_values`
было замечено в rev #2:

| Таблица | UNIQUE constraint | Модель |
|---|---|---|
| `family_types` | `(catalog_item_id, type_name)` | **current-snapshot** — тип "100" может быть только один на catalog_item |
| `extracted_attribute_values` | `(catalog_item_id, version_id, type_id, parameter_name)` | **per-version** — значения могут сосуществовать |

Плюс `extracted_attribute_values.type_id` имеет FK ON DELETE CASCADE → при удалении типов cross-version `SyncTypesAsync` удалял **и типы v1, и значения v1**. То есть при импорте v2 данные v1 **полностью** уничтожались.

V18 миграция через CREATE-COPY-DROP-RENAME (паттерн V15/V17) меняет UNIQUE на `UNIQUE(catalog_item_id, version_id, type_name)`:

```sql
-- 1. Cleanup orphan rows (defensive — same as V17)
DELETE FROM family_types
WHERE version_id IS NOT NULL
  AND version_id NOT IN (SELECT id FROM catalog_versions);

-- 2. Recreate with per-version UNIQUE
DROP TABLE IF EXISTS family_types_v18;

CREATE TABLE family_types_v18 (
    id TEXT PRIMARY KEY,
    catalog_item_id TEXT NOT NULL,
    type_name TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    version_id TEXT,
    file_id TEXT,
    extraction_run_id TEXT,
    type_unique_id TEXT,
    FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
    FOREIGN KEY (version_id) REFERENCES catalog_versions(id) ON DELETE CASCADE,
    FOREIGN KEY (file_id) REFERENCES family_files(id) ON DELETE SET NULL,
    UNIQUE(catalog_item_id, version_id, type_name)
);

INSERT INTO family_types_v18 (id, catalog_item_id, type_name, sort_order,
    version_id, file_id, extraction_run_id, type_unique_id)
SELECT id, catalog_item_id, type_name, sort_order, version_id, file_id,
    extraction_run_id, type_unique_id
FROM family_types;

DROP TABLE family_types;
ALTER TABLE family_types_v18 RENAME TO family_types;

-- 3. Recreate indexes + partial UNIQUE INDEX for orchestrator
CREATE INDEX IF NOT EXISTS ix_family_types_item ON family_types (catalog_item_id);
CREATE INDEX IF NOT EXISTS ix_family_types_name ON family_types (type_name);
CREATE INDEX IF NOT EXISTS ix_family_types_version_id
    ON family_types (version_id) WHERE version_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ix_family_types_orchestrator_unique
    ON family_types (catalog_item_id, type_name)
    WHERE version_id IS NULL;
```

**`ix_family_types_orchestrator_unique`** — SQLite NULL != NULL quirk: с таблиц-левель `UNIQUE(catalog_item_id, version_id, type_name)` две строки `(item, NULL, "Type 100")` не нарушают constraint (NULL != NULL по семантике SQLite), но логически это один и тот же тип дважды. Partial UNIQUE INDEX для `WHERE version_id IS NULL` закрывает gap для orchestrator case.

### 9. `MakeActive` в ImportActiveFile flow — routing fix (rev #3)

`LocalFamilyImportService.ImportBatchAsync:478` уже имеет правильную диспетчеризацию `MakeActive` → `SetActiveVersionAsync`. Проблема была в `FamilyManagerMainViewModel.ProcessFamilyImportAsync` — он не имел специальной ветки `MakeActive`:

**До rev #3 (post-rev #2):** post-dialog precomputer (`BuildPrecomputedTripleAsync`) возвращал `v3` для existing item. `EnsureFamilyDirectories(v3)` создавал каталог. `SaveAs` сохранял .rfa в `v3/{name}.rfa`. `ImportFileAsync` вставлял новый `catalog_versions` row.

**После rev #3:**
```csharp
var isMakeActive = importItem.Action == FamilyBatchImportAction.MakeActive
    && importItem.Status == FamilyBatchImportStatus.Duplicate
    && !string.IsNullOrEmpty(importItem.ExistingCatalogItemId)
    && !string.IsNullOrEmpty(importItem.MatchedVersionLabel);

// ... existing isOverwrite branch ...

if (!isMakeActive && string.IsNullOrEmpty(resolvedManagedPath)) { /* error */ return; }

string? saveAsPath = null;
if (!isMakeActive)
{
    // skip EnsureFamilyDirectories + SaveAs для MakeActive
    _pathResolver.EnsureFamilyDirectories(...);
    saveAsPath = await _awaitableEvent.RaiseAsync<string?>(...);  // SaveAs
}

// Routing:
if (isMakeActive) {
    // ImportBatchAsync → SetActiveVersionAsync (см. LocalFamilyImportService.cs:478)
}
else if (isOverwrite) {
    // ImportBatchAsync → OverwriteCurrentAsync (ADR-040)
}
else {
    // ImportFileAsync → InsertVersion (для New/IncrementVersion)
}

// Закрытие документа:
if (importResult.Success) {
    var pathToClose = saveAsPath ?? placeholderFilePath;
    await CloseFamilyDocumentAsync(pathToClose);
}
```

Routing зеркалит существующий `isOverwrite` pattern: skip SaveAs/ExtractAttributes/EnsureFamilyDirectories, route through `ImportBatchAsync` (который имеет диспетчеризацию для `MakeActive`).

## Alternatives considered

### Per-version хранение типов (выбрано, rev #2)

✅ **Плюсы:**
- При откате данные уже в БД, никаких runtime-overhead
- Один источник правды — БД, никакой пере-извлечения
- Атрибут values уже per-version (V17 UNIQUE) — типы подтягиваются

⚠️ **Trade-offs:**
- V18 миграция необходима для всех существующих БД
- Дубликатные rows по `(item, NULL version_id, type_name)` закрыты через partial UNIQUE INDEX (см. Schema migration v18, шаг 3)
- Storage БД растёт на размер удалённых версий (типы + extracted values), но V17 FK CASCADE очищает их при `DeleteVersionAsync`

### Пере-извлечение из .rfa при MakeActive (отклонено, rev #2)

❌ **Минусы:**
- Открытие .rfa на Revit UI thread через `ExternalEvent` ≈ 400-500ms на версию
- Требует рабочий файл и доступный managed path (если .rfa повреждён — fail при откате)
- `extracted_attribute_values` тоже нужно пере-извлекать — ADSK extraction не idempotent (параметры с одинаковым именем могут иметь разные TypeIDs между сессиями)
- История «какие версии у этого семейства» не используется (просто snapshot)

### Гибрид: snapshot при импорте + lookup-таблица при откате (отклонено, rev #2)

Хранение типов в архивной таблице `family_types_history` (item_id, version_label, type_name, ...) при каждом импорте; при MakeActive копирование из history обратно в `family_types`.

❌ **Минусы:**
- Новая таблица с дублирующей семантикой (`family_types` + `family_types_history` расходятся)
- V17 FK CASCADE требует изменений (иначе не очищается)
- Больше кода, больше мест для ошибок

## Consequences

### Positive
- **Round-trip отката работает:** типы и значения активной версии сразу появляются в дереве / окне свойств / ImportRunInfo
- **MakeActive в batch диалоге — no-op на диске:** не создаёт v3, файл не пишется, переключение мгновенное
- **Multi-version consistency:** типы v1+v2+v3 сосуществуют в БД под per-version UNIQUE, что даёт честную «версионизацию»
- **Project families (orchestrator) не задеты:** `version_id IS NULL` scope не трогает versioned rows

### Negative / Trade-offs
- **Storage удваивается** для multi-version items: каждая версия держит свои типы+values. Для типичной семьи (3-5 типов, 30 параметров × 4 значения) — порядка 4 KB на версию, не критично
- **V18 миграция** — обязательна (нет downgrade path)
- **EditAttribute extraction для MakeActive пропущен** (т.к. types/values уже в БД); но это и нужно — повторное извлечение изменяло бы version_id в строках `family_types`
- **Таблицы `family_types` могут содержать дубликаты имён** (для разных version_id) — UI фильтрует через JOIN с активной версией. Если разработчик забудет фильтр в новом query, увидит дубликаты — поэтому `BuildAvailableActions` уже фильтрует, а related code ревьюирует
- **`extracted_attribute_values` теперь может относиться к старым version_id** — V17 FK CASCADE сохраняет referential integrity при удалении версии; UI фильтрует через active version subquery

## Слои

### rev #1 (initial ADR)

| Layer | Изменения |
|---|---|
| `SmartCon.Core/Models/FamilyManager/` | ADD `FamilyBatchImportAction.MakeActive` (enum value); ADD `SetActiveVersionResult.cs`, `DeleteVersionResult.cs` (records) |
| `SmartCon.Core/Services/Interfaces/` | ADD `IFamilyCatalogProvider.GetVersionByIdAsync`, `GetVersionByLabelAsync`; ADD `IWritableFamilyCatalogProvider.SetActiveVersionAsync`, `DeleteVersionAsync` |
| `SmartCon.FamilyManager/Services/LocalCatalog/FamilyCatalogSql.cs` | ADD `MigrateV17RecreateFamilyTypesWithVersionFk`, `MigrateV17RecreateExtractedAttributeValuesWithVersionFk`, `CreateV17Indexes`; UPDATE `CreateFamilyTypes`, `CreateExtractedAttributeValues`, `CreateIndexes`, `CreateFamilyTypesIndexes` (включают FK на version_id) |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogMigrator.cs` | ADD `MigrateV17Async` (registered after V16, before V8) |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogProvider.cs` | partial class declaration |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogProvider.Versions.cs` (NEW) | ADD `GetVersionByIdAsync`, `GetVersionByLabelAsync`, `SetActiveVersionAsync`, `DeleteVersionAsync` |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.cs` | ADD `MakeActive` dispatch branch in `ImportBatchAsync:478` |
| `SmartCon.FamilyManager/ViewModels/FamilyVersionRow.cs` (NEW) | VM row for «Версии» table |
| `SmartCon.FamilyManager/ViewModels/FamilyPropertiesViewModel.cs` | ADD `IFamilyCatalogProvider _catalogProvider` dependency |
| `SmartCon.FamilyManager/ViewModels/FamilyPropertiesViewModel.Versions.cs` (NEW) | ADD `LoadVersionsAsync`, `MakeActiveCommand`, `DeleteVersionCommand`, `Versions`/`SelectedVersionRow` properties |
| `SmartCon.FamilyManager/ViewModels/FamilyBatchImportRow.cs` | ADD `MakeActive` to `BuildAvailableActions` for `Duplicate` status |
| `SmartCon.FamilyManager/Services/FamilyManagerViewModelFactory.cs` | ADD `IFamilyCatalogProvider` dependency |
| `SmartCon.FamilyManager/Views/FamilyPropertiesView.xaml` | ADD «Версии» TabItem with DataGrid + Make Active/Delete buttons |
| `SmartCon.FamilyManager/Views/FamilyPropertiesView.xaml.cs` | ADD `InitializeVersionGridHeaders` (I-12 programmatic headers) |
| `SmartCon.UI/Converters/FamilyBatchImportActionConverter.cs` | ADD `MakeActive` case |
| `SmartCon.UI/StringLocalization.cs` | ADD 18 keys: `FM_Tab_Versions`, `FM_MakeActive`, `FM_DeleteVersion`, etc. |
| `SmartCon.Core/Services/LocalizationService.Keys.FamilyManager.cs` | ADD 18 RU/EN strings |

### rev #2 (per-version types)

| Layer | Изменения |
|---|---|
| `SmartCon.FamilyManager/Services/LocalCatalog/FamilyCatalogSql.cs` | ADD `MigrateV18RecreateFamilyTypesPerVersionUnique` (по образцу V17); UPDATE `CreateFamilyTypes` UNIQUE → `(catalog_item_id, version_id, type_name)`; UPDATE `CreateFamilyTypesIndexes` — добавить `ix_family_types_orchestrator_unique` partial unique index |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogMigrator.cs` | ADD `MigrateV18Async` (after V17, before V8) — registered в chain |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyTypeRepository.cs` | CHANGED `SyncTypesAsync`: orchestrator case → `DELETE WHERE version_id IS NULL`; active case → `DELETE WHERE version_id = @versionId`; `ON CONFLICT(catalog_item_id, version_id, type_name)`. CHANGED `GetTypesForItemAsync` / `GetAllTypesBatchAsync`: correlated subquery на active_version_id (через `current_version_label`), с orchestrator fallback (`version_id IS NULL AND NOT EXISTS active version`) |
| `SmartCon.Core/Services/Interfaces/IFamilyDataImportRunRepository.cs` | ADD `GetLatestRunForActiveVersionAsync` (interface) |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyDataImportRunRepository.cs` | ADD impl: JOIN family_data_import_runs к active version через subquery `cv.id = (SELECT cv.id FROM catalog_versions cv JOIN catalog_items ci ON ci.current_version_label = cv.version_label WHERE cv.catalog_item_id = @itemId LIMIT 1)` |
| `SmartCon.FamilyManager/ViewModels/FamilyPropertiesViewModel.Versions.cs` | CHANGED `MakeActiveAsync`: after success → reload `LoadAttributesDataAsync` (AvailableTypes + AttributeRows + ImportRunInfo обновляются) |
| `SmartCon.FamilyManager/ViewModels/FamilyPropertiesViewModel.cs` | CHANGED `LoadAttributesDataAsync`: `GetLatestRunAsync` → `GetLatestRunForActiveVersionAsync` (run-of-active-version для `ImportRunInfo` + `GetValuesForItemAsync(run.VersionId, ...)` фильтрация) |
| `src/SmartCon.Tests/FamilyManager/Repository/LocalFamilyTypeRepositoryTests.cs` | ADD 4 новых теста + переименование `SyncTypesAsync_ActiveImport_CollapsesAllVersionsToCurrent` → `SyncTypesAsync_ActiveImport_PreservesOtherVersionsTypes` (семантика перевёрнута) + ADD `SeedBareCatalogItemAsync` / `SetActiveVersionAsync` helpers |
| `src/SmartCon.Tests/FamilyManager/Repository/LocalCatalogMigratorTests.cs` | UPDATE ожиданий: `schema_version = "17"` → `"18"` (3 места) |
| `src/SmartCon.Tests/FamilyManager/Repository/LocalCatalogVersionManagementTests.cs` | UPDATE: `schema_version = "17"` → `"18"` |

### rev #3 (MakeActive routing in ImportActiveFile)

| Layer | Изменения |
|---|---|
| `SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.FamilyEdit.cs` | CHANGED `ProcessFamilyImportAsync`: ADD `isMakeActive` branch (`Action=MakeActive && Status=Duplicate && ExistingCatalogItemId && MatchedVersionLabel`). Routing: skip `EnsureFamilyDirectories` + `SaveAs` + `ExtractAttributesForLoadableTasks` + `CloseFamilyDocumentAsync(saveAsPath!)`. Route через `ImportBatchAsync` с batchItem.Action=MakeActive (existing branch в `LocalFamilyImportService.ImportBatchAsync:478`). |
| `SmartCon.Tests/FamilyManager/Repository/LocalFamilyImportServiceTests.cs` | ADD `ImportBatchAsync_MakeActive_Duplicate_DoesNotCreateNewVersion` — regression test pinning: `WasSkipped=true`, `VersionLabel=MatchedVersionLabel`, ≤1 `catalog_versions` row после вызова |

### rev #4 (CloseFamilyDocumentAsync для MakeActive)

| Layer | Изменения |
|---|---|
| `SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.FamilyEdit.cs` | CHANGED: убрано условие `!isMakeActive` для вызова `CloseFamilyDocumentAsync`. `pathToClose = saveAsPath ?? placeholderFilePath` (где `placeholderFilePath = prepared.SourcePath`). |

### rev #5 (UX compact tooltip preview + cosmetic)

| Layer | Изменения |
|---|---|
| `SmartCon.FamilyManager/ViewModels/FamilyVersionRow.cs` | ADD `TypeNames` (`IReadOnlyList<string>`) private-set свойство. ADD computed `TypeNamesTooltip` (string.Join(", ") или `"— нет типов —"`). ADD computed `TypesCountDisplay` string (`TypeNames.Count.ToString()` fallback `TypesCount` fallback `"—"` — решает проблему прочерка в колонке Типы). ADD `PublishedBy` property. ADD overload constructor `FamilyVersionRow(..., string? publishedBy, IReadOnlyList<string> typeNames)`. ADD `SetTypeNames` internal helper (используется `LoadVersionsAsync` для заполнения после row-конструирования). |
| `SmartCon.FamilyManager/ViewModels/FamilyPropertiesViewModel.Versions.cs` | CHANGED `LoadVersionsAsync`: после построения `rows` для каждой row вызывается `GetTypesForItemVersionAsync` чтобы заполнить `TypeNames` + `publishedBy` passed from FamilyCatalogVersion.PublishedBy (roundtrip per version, ~10-30ms — typical ≤5 versions). CHANGED `OnSelectedVersionRowChanged`: возвращён к минимальной реализации — только `NotifyCanExecuteChanged` для команд; никаких preview reload (тяжёлый путь ломал UX). CHANGED: `Error` логи в `MakeActiveAsync` и `DeleteVersion` теперь заканчиваются `[Action: ...]` (L9 consistency). REMOVE `LoadPreviewForSelectedVersionAsync` метод (был добавлен в первой попытке rev #5, удалён как нарушающий UX). REMOVE `_previewLoadCts` field. |
| `SmartCon.FamilyManager/ViewModels/FamilyPropertiesViewModel.cs` | REMOVE `[ObservableProperty] _isPreviewMode` + `[ObservableProperty] _previewVersionLabel` (были добавлены в первой попытке rev #5, удалён вместе с `LoadPreviewForSelectedVersionAsync`). |
| `SmartCon.FamilyManager/Views/FamilyPropertiesView.xaml` | CHANGED «Версии» TabItem: REMOVE preview banner (двигал таблицу — первая попытка rev #5). REMOVE `RowDetailsTemplate` (вторая попытка rev #5, слишком громоздко). ADD `Window.Resources`: `CenteredTextCell` + `LeftTextCell` `Style TargetType=TextBlock` с `TextAlignment=Center` + `HorizontalAlignment` + `VerticalAlignment` (per MS Learn / SO best practice https://stackoverflow.com/a/720732 + https://stackoverflow.com/a/63513914). CHANGED DataGrid: ADD `CellStyle="{DynamicResource CompactDataGridCell}"` (убирает синий фон ячейки — совпадает с batch диалогом); ADD `RowStyle="{DynamicResource CompactDataGridRow}"` (лёгкий selection + hover, совпадает с batch диалогом); ADD inline `ColumnHeaderStyle` с `HorizontalContentAlignment=Center` (центрирование заголовков колонок); FIX `BorderBrush="{DynamicResource BorderAltBrush}"` (вместо несуществующего `BorderBrush`); ADD `EnableRowVirtualization="False"`; ADD `ElementStyle="{StaticResource CenteredTextCell}"` на все 5 текстовых columns (VersionLabel, RevitMajorVersion, PublishedAtText, PublishedBy/ColVersionAuthor, TypesCountDisplay); CHANGED `ColVersionTypes` `Binding="{Binding TypesCountDisplay}"` (was `TypesCount` which could be NULL — теперь всегда показывает реальное число типов) + `ToolTipService.ToolTip="{Binding TypeNamesTooltip}"` (tooltip при наведении показывает все имена типов через запятую — компактно и строго); CHANGED `ColVersionActive` badge — `Background="{DynamicResource SavedSuccessBrush}"` (зелёный) вместо `AccentBrush` (синий) + `HorizontalAlignment="Center"` (было `Left`). ADD new column `ColVersionAuthor` (`Binding="{Binding PublishedBy, TargetNullValue='—'}"`) showing per-version author username. |
| `SmartCon.FamilyManager/Views/FamilyPropertiesView.xaml.cs` | CHANGED `InitializeVersionGridHeaders`: ADD `ColVersionAuthor.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Author) ?? "Author"` (I-12 programming установка заголовка колонки Author). |
| `SmartCon.FamilyManager/Services/LocalCatalog/FamilyCatalogSql.cs` | ADD `published_by TEXT` column to `CreateCatalogVersions` DDL (line 55). ADD const `MigrateV19AddPublishedByColumn` (line 770) — `ALTER TABLE catalog_versions ADD COLUMN published_by TEXT`. |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogMigrator.cs` | ADD registration `await MigrateV19Async(connection, ct)` after V18 (line 66). ADD `MigrateV19Async` implementation (lines 714-748): double idempotency (`schema_version >= 19` check + `ColumnExistsAsync("catalog_versions","published_by")` check); transaction safety (`BeginTransaction → Commit/Rollback`); logs success via `SmartConLogger.Info`. |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogProvider.cs` | CHANGED `ReadCatalogVersion` (line 484): reads `published_by` via `TryGetString(reader, "published_by")` — safe for legacy DBs without V19 migration (returns null). |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.Database.cs` | CHANGED `InsertVersionAsync` (line 198, signature + body): accepts `string? publishedBy = null` parameter, INSERT statement includes `published_by` column + `@publishedBy` SqliteParameter. CHANGED `UpdateVersionAsync` (line 233): accepts `string? publishedBy` parameter (no default — required), UPDATE statement sets `published_by = @publishedBy`. |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.cs` | CHANGED all paths that create `FamilyImportRequest`/`FamilyUpdateRequest` to pass `PublishedBy: item.PublishedByUser` or `request.PublishedBy` (depending on entry point). Specifically: line 218 (InsertVersionAsync call), line 313 (ImportFolderAsync), line 475 (ImportBatchAsync New/Increment branch), line 547 (FamilyUpdateRequest creation), line 725 (UpdateFamilyAsync InsertVersionAsync call). OverwriteCurrent path (line 448 Database.cs) also receives `item.PublishedByUser`. MakeActive path **skipped** (pointer switch only, no publication). |
| `SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.FamilyEdit.cs` | CHANGED `ProcessFamilyImportAsync`: 3 places call `_revitContext.GetUsername()` (cached string field from `RevitContext.cs:66-72`, NOT Revit API — readonly field access safe from async). Lines 457 (MakeActive batch item), 500 (OverwriteCurrent batch item), 534 (FamilyImportRequest new/Increment). All three populate `FamilyBatchImportItem.PublishedByUser`. |
| `SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Import.cs` | CHANGED `ImportFilesAsync` (line 189-190): `foreach (var si in selectedItems) si.PublishedByUser = _revitContext.GetUsername();` before `await _importService.ImportBatchAsync(...)`. Covers batch-import pipeline (UC Import Files button). |
| `SmartCon.Core/Models/FamilyManager/FamilyCatalogVersion.cs` | ADD `PublishedBy` record parameter (string?, optional, default null). XML doc comment `<param name="PublishedBy">`. |
| `SmartCon.Core/Models/FamilyManager/FamilyImportRequest.cs` | ADD `PublishedBy` record parameter (string?, optional, default null). |
| `SmartCon.Core/Models/FamilyManager/FamilyUpdateRequest.cs` | ADD `PublishedBy` record parameter (string?, optional, default null). |
| `SmartCon.Core/Models/FamilyManager/FamilyFolderImportRequest.cs` | ADD `PublishedBy` record parameter (string?, optional, default null). |
| `SmartCon.Core/Models/FamilyManager/FamilyBatchImportItem.cs` | ADD `PublishedBy` record parameter + ADD `PublishedByUser` mutable property `{ get; set; } = PublishedBy` (same pattern as `TargetCategoryId`/`TargetCategoryName` — positional param initializes mutable shadow property, post-construction mutation allowed for mass-edit in batch grid). |
| `SmartCon.Core/Services/LocalizationService.Keys.FamilyManager.cs` | ADD `ru["FM_Version_Column_Author"] = "Автор"; en["FM_Version_Column_Author"] = "Author";` (line 178). |
| `SmartCon.UI/StringLocalization.cs` | ADD `public const string FM_Version_Column_Author = "FM_Version_Column_Author";` (line 398). |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogProvider.Versions.cs` | CHANGED `SetActiveVersionAsync` catch (line ~198): убран `SmartConLogger.Error("SetActiveVersion failed: ...")` + оставлен `throw;` (как в `DeleteVersionAsync`) — убирает двойное логирование исключения. CHANGED `DeleteVersionAsync` file-cleanup Warn (line ~363): полный путь `versionDir` заменён на `leaf name` (`Path.GetFileName`) — правило L8 (file path в message без full path); scope уже содержит CatalogItemId+VersionLabel для корреляции. |
| `src/SmartCon.Tests/FamilyManager/Repository/LocalCatalogMigratorTests.cs` | UPDATE `"18"` → `"19"` schema_version expectations (4 места: lines 71, 121, 184, 453). |
| `src/SmartCon.Tests/FamilyManager/Repository/LocalCatalogVersionManagementTests.cs` | UPDATE `"18"` → `"19"` schema_version expectation (1 место: line 33). |

## Логирование (smartcon-logging)

Категории `FMVersion` + `FMProperties` (rev #1, vocabulary `SmartConLogger`):

- `SetActiveVersionAsync`: `BeginScope("FMVersion", ("Method","SetActiveVersionAsync"), ("CatalogItemId",...), ("VersionLabel",...))`
  + `Info("switched active version: prev=... new=... hashSynced=...")`
- `DeleteVersionAsync`: `BeginScope("FMVersion", ("Method","DeleteVersionAsync"), ...)`
  + `Info("deleted DB rows: versions=N assets=N")`
  + `Warn("failed to delete physical files at {path}: {ex} [Action: close any Revit document using this family and retry]")` (L9)
- `MakeActive` (in ImportBatchAsync): `BeginScope("LocalImport", ("Method","MakeActive"), ...)`
  + `Info("MakeActive: prev=... new=... success=... hashSynced=...")`
- `MakeActiveAsync` (FamilyPropertiesViewModel, rev #2): `BeginScope("FMProperties", ("Method","MakeActiveAsync"), ...)`
  + `Info("MakeActive succeeded: ... hashSynced=...")`
  + `Warn("MakeActive succeeded but Attributes reload failed: {ex.Message} [Action: закройте и откройте свойства снова; чтобы перечитать вкладку «Атрибуты» для новой активной версии]")` (L9)

Каждый WARN заканчивается `[Action: ...]` (правило L9 из smartcon-logging).

## Ключевые решения обоснования

### Почему НЕ добавляется колонка `is_active`

- Дублирует `current_version_label` → рассинхронизация.
- CHECK-ограничение «ровно одна active на item» требует full-table scan миграции
  (нельзя `ALTER TABLE ADD COLUMN ... CHECK`).
- `current_version_label` уже корректно используется во всех путях автоматически.

### Почему `DeleteVersion` hard delete, не soft delete

Пользователь явно выбрал удаление (с подтверждением в модальном диалоге).
Hard delete гарантирует, что повторный импорт того же контента определится
как `New` (бизнес-требование: «когда мы удаляем версию, должны вычищаться всё,
и новое семейство должно быть как new»).

### Почему `MakeActive` доступен только для `Duplicate`, не `Existing`

`Existing` означает, что имя найдено, но контент различается (в каталоге другой
файл). `MakeActive` требует конкретную существующую версию для переключения
(найденную через content-hash match) — для `Existing` нет версии для активации.

### Почему `OverwriteCurrent` и Stale detection автоматически следуют за активной

`OverwriteCurrentAsync` (ADR-040) уже использует `catalog_items.current_version_label`
через SQL JOIN в `FindCurrentVersionAsync`. Изменение `current_version_label`
через `SetActiveVersionAsync` автоматически делает `OverwriteCurrent` работающим с новой активной.

`StaleDetector.ComputeReason` сравнивает `loaded.VersionLabel != item.CurrentVersionLabel`
(`StaleDetector.cs:372-373`). После `SetActiveVersionAsync` следующая проверка
авто-актуализирует состояние семейства (без правок в `StaleDetector`).

## Verification

### Build
- R25: 0 warnings / 0 errors
- R24: 0 warnings / 0 errors
- R21: 0 warnings / 0 errors
- R19: 0 warnings / 0 errors

### Tests
- Исходные 1648 тестов pass (после ревизии schema_version='17'→'18' и фикса sync-тестов под per-version UNIQUE)
- 23 новых теста rev #1 (V17 миграция + SetActiveVersion/DeleteVersion): как перечислено в ADR-041 rev #1
- 5 новых тестов rev #2:
  - `SyncTypesAsync_SameTypeNameInDifferentVersions_BothStored`
  - `GetTypesForItemAsync_NoActiveVersion_FallsBackToOrchestratorTypes`
  - `GetAllTypesBatchAsync_ActiveVersionFilter_PicksPerItemActiveVersion`
  - `SyncTypesAsync_OrchestratorScope_DoesNotDeleteVersionedTypes`
  - `SyncTypesAsync_ActiveImport_PreservesOtherVersionsTypes` (перевёрнутая семантика; бывш. `SyncTypesAsync_ActiveImport_CollapsesAllVersionsToCurrent`)
- 1 новый тест rev #3: `ImportBatchAsync_MakeActive_Duplicate_DoesNotCreateNewVersion`
- rev #5: новых unit-тестов не добавлено (см. `rev #5 → Tests` выше в ревизии; пути покрываются manual Revit test UC-2)
- Итого: **1676/1676 pass** (Debug.R25 / Debug.R21 / Debug.R24 / Debug.R19)

### Manual Revit test (требуется пользователю)
- UC-1: открыть Properties → видеть вкладку Версии → список корректный.
- UC-2: выбрать v2 (не активную) в таблице «Версии». Колонка «Типы» показывает реальное число типов (через `TypesCountDisplay` — берётся из `TypeNames.Count`, а не из `catalog_versions.types_count` который может быть NULL). Tooltip на ячейке «Типы» показывает полный список имён типов через запятую. Никакого layout-shift / preview reload / banner / row details — компактно и строго. Чтобы увидеть полные атрибуты выбранной версии — нажать «Сделать активной».
- UC-3: кнопка «Сделать активной» → подтверждающий диалог → OK → бейдж «Активная» на v2, дерево обновилось, вкладка Атрибуты показывает типы/значения v2.
- UC-4: кнопка «Удалить» на неактивной v3 → подтверждающий диалог → OK → версия удалена из списка; проверка: каталог `{itemId}/v3/` не существует; типы и атрибуты для `version_id` v3 удалены.
- UC-5 (rev #3): импорт файла-дубликата через "Импорт активного файла" — dedup = Duplicate (hash matches archived version) → в выпадающем списке «Сделать активной». Выбрать → подтвердить импорт → НЕ должно создаться v3, активной стала matched archived version. Файл НЕ перезаписан (проверка mtime файла). Редактор rfa закрылся автоматически.
- UC-6: после UC-3 (активна v2) импорт изменённого файла → IncrementVersion → создана v5 (не v3).
- UC-7: при активной v2 выбрать OverwriteCurrent → перезаписан файл v2 (не v3/v4), `v2.content_hash` обновлён.
- UC-8: при активной v2 нажать «Проверить» → Stale сравнивает с типами v2 в проекте.
- UC-9 (rev #2): перезапустить между сценариями — версия должна меняться без потери данных.

## Sources

### Внешние (Exa)
- [SemVer 2.0.0 Specification](https://semver.org/) — `current_version_label` semantics, version number stability
- [SharePoint Versioning](https://support.microsoft.com/en-us/sharepoint/lists/documents-and-library/how-versioning-works-in-lists-and-libraries) — restore patterns, version number gaps after delete
- [ScaiLabs: Versioning and Deduplication](https://www.scailabs.ai/docs/scaidrive/core-concepts/versioning-and-deduplication) — restore creates new version, intermediate versions retained
- [keep-a-changelog #35: yanked versions](https://github.com/olivierlacan/keep-a-changelog/issues/35) — soft delete vs hard delete trade-offs
- [SQLite ALTER TABLE](https://www.sqlite.org/lang_altertable.html) — CREATE-COPY-DROP-RENAME 12-step procedure for adding FK constraints
- [SQLite ADD COLUMN restrictions](https://sqlite.org/forum/info/ffa52447275d247a) — NOT NULL without DEFAULT fails, FK enforcement on insert
- [Schema Evolution best practices](https://agenticdevelopercookbook.com/guidelines/implement/data/schema-evolution) — additive migrations, idempotency

### Внутренние ADR
- [ADR-016](016-familymanager-readonly-files.md) — Managed Storage Immutability (OverwriteCurrent + DeleteVersion exceptions)
- [ADR-022](022-familymanager-rbac.md) — RBAC matrix Owner/BimMaster can manage versions
- [ADR-030](030-phase-24-stale-detection-v2.md) — Stale detection reads `current_version_label`
- [ADR-033](033-bakein-type-catalog.md) — Type Catalog bake-in
- [ADR-036](036-active-family-type-sync.md) — SyncTypesAsync collapse to current version
- [ADR-039](039-snapshot-driven-commit.md) — Snapshot through dialog round-trip
- [ADR-040](040-overwritecurrent-semantics.md) — OverwriteCurrent UPDATEs `catalog_versions` in place

## Out of Scope

- ❌ История переключений активной версии (audit log) — пользователь явно отклонил.
- ❌ Перенумерация версий после удаления (SemVer, SharePoint — best practice = no renumbering).
- ❌ Soft-delete (пометка вместо удаления) — пользователь явно выбрал hard delete.
- ❌ Комментарий-список изменений при создании новой версии — зарезервировано под будущую фичу (колонка «Комментарий» во вкладке Версии уже отображается, остаётся пустой).
- ❌ MakeActive через ПКМ в дереве FamilyManager — только Properties dialog и batch dialog.
- ❌ Mass-удаление неактивных версий (только по одной за раз).
