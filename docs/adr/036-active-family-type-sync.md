# ADR-036: Sync типов при импорте активного семейства (Issue #85)

**Status:** accepted
**Date:** 2026-06-25
**Phase:** 27
**Issue:** [#85](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/85)
**Supersedes (partially):** ADR-031 (FireAndForget UI marshalling — bug #2 был пропущен в Phase 4c)

## Revisions

### rev #2 (2026-06-25): SystemFamilyImportOrchestrator — передача реальных VersionId/FileId
**Problem:** Регрессия из rev #1 — `SystemFamilyImportOrchestrator.SaveSystemTypesAsync`
конструировал descriptors с реальными `VersionId = versionId` и `FileId = fileId`
(из `matchingResult`), но в `SyncTypesAsync` вызов передавал `null/null`. Внутри
`SyncTypesAsync` SQL INSERT использовал `@versionId`/`@fileId` из параметра
метода (которые были null), а не из `types[i].VersionId`/`types[i].FileId`.
Реальные значения терялись.

**Fix:** Передавать `versionId` и `fileId` в `SyncTypesAsync`:
```csharp
await _typeRepository.SyncTypesAsync(catalogItemId, versionId, fileId, "no-run", descriptors);
```
Теперь descriptors' `version_id` и `file_id` корректно сохраняются в БД.

**Тест:** `LocalFamilyTypeRepositoryTests.SyncTypesAsync_SyncDescriptor_VersionIdAndFileIdPersisted`
(см. Call sites ниже).

### rev #3 (2026-06-25): Case (b) — collapse-to-current (вместо JOIN-on-versionLabel)
**Problem:** Оригинальная семантика DEC-001 "Filter по `(versionId, fileId)` через
JOIN на `catalog_versions`" была корректна по смыслу (удалять только типы текущей
версии), но в production каждый импорт создаёт новый `catalog_versions` GUID
(`LocalFamilyImportService.ImportFileAsync:138`, `var versionId = Guid.NewGuid().ToString()`).
UNIQUE constraint `(catalog_item_id, version_label, revit_major_version)` обходится
через `revit_major_version` (разный Revit Major → разные rows для одного label).
JOIN по `version_label` находил только одну из строк — старые импорты оставались
как ghost.

**Fix:** Изменил case (b) с `JOIN-by-versionLabel DELETE` на
`DELETE WHERE catalog_item_id = @itemId` (collapse-to-current). Разрушает
multi-version type set, но для активного импорта это OK — пользователь
хочет видеть только текущую версию. `catalog_versions` history сохраняется.

**Изменения в SQL:**
- Старый (rev #1): `DELETE WHERE catalog_item_id = @itemId AND (version_id = @versionId OR (version_id IS NULL AND @versionId IS NULL))`
- Новый (rev #3): `DELETE WHERE catalog_item_id = @itemId` (без фильтра по version)

### rev #4 (2026-06-25): V8 recreate — все 4 FK constraints
**Problem:** `MigrateV8RecreateExtractedAttributeValues` содержал только 2 FK
(catalog_item_id, extraction_run_id). Если БД с `attribute_id NOT NULL` (edge case)
проходила через V8 recreate ПОСЛЕ V15 (через `EnsureCriticalColumnsAsync`), FK
на `attribute_id` и `type_id` терялись.

**Fix:** Добавил все 4 FK в V8 SQL:
```sql
FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
FOREIGN KEY (type_id) REFERENCES family_types(id) ON DELETE CASCADE,
FOREIGN KEY (attribute_id) REFERENCES attribute_definitions(id) ON DELETE CASCADE,
FOREIGN KEY (extraction_run_id) REFERENCES family_data_import_runs(id) ON DELETE CASCADE,
```

### rev #5 (2026-06-25): Bug #5 — async void lambda в IDispatcher.InvokeAsync
**Problem:** `FamilyManagerMainViewModel.RefreshTreeViaExternalEventAsync:573`
передавал `async () => { await RefreshAccessAndLoadTreeAsync(); }` как Action в
`_dispatcher.InvokeAsync`. Компилятор генерирует async void state machine —
нарушение I-13 (exception swallowing) и потеря тестируемости.

**Fix:** Вынес async-работу в отдельный именованный метод `RefreshTreeOnUiThreadAsync`.
В `InvokeAsync` передаётся `() => { _ = RefreshTreeOnUiThreadAsync(); }` — синхронный
Action, fire-and-forget через discard.

### rev #6 (2026-06-25): cross-cutting.md — неверный пример IDispatcher
**Problem:** `docs/domain/interfaces/cross-cutting.md:88` содержал
`await _dispatcher.InvokeAsync(() => LoadTreeAsync())`. `LoadTreeAsync` возвращает
Task, `() => LoadTreeAsync()` это `Func<Task>`, не `Action`. **Не компилируется.**
Реальный код (FamilyEdit.cs:1034) использует `() => { _ = LoadTreeAsync(); }`.

**Fix:** Заменил строку 88 + добавил комментарий о причине discard-обёртки.

## Контекст

### 1. Проблема (Issue #85)

В модуле FamilyManager при использовании workflow «Редактировать → Импорт активного семейства» есть два связанных бага, приводящих к расхождению состояния БД и UI:

**Bug #1 (ghost types).** При удалении типа в Revit и последующем импорте активного семейства удалённый тип **остаётся** в `family_types`. При повторении цикла edit/import в БД копится мусор, нет штатного способа очистить.

**Bug #2 (UI не обновляется).** При добавлении нового типа в Revit и импорте активного семейства новый тип **не появляется** в дереве до ручного нажатия Refresh или «Загрузить в проект». UX-сюрприз.

### 2. Root cause (подтверждено чтением кода)

#### 2.1. Bug #1 — UPSERT не удаляет отсутствующие

`src/SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyTypeRepository.cs:159-209` — `SaveTypesForRunAsync` использует `INSERT ... ON CONFLICT(catalog_item_id, type_name) DO UPDATE`, но **не удаляет** типы, которых больше нет в импортируемом `.rfa`.

Сравнение: `SaveTypesAsync` (строки 120-157) использует DELETE+INSERT и **работает корректно** в `LoadableFamilyImportOrchestrator:91` (project case). Это подтверждает что архитектурно верная семантика — DELETE+INSERT в одной транзакции.

#### 2.2. Bug #2 — FireAndForget без UI marshalling

`src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.FamilyEdit.cs:1006-1022` — `ExtractAttributesForImportedFamilies` в `FireAndForget` **не вызывает** `LoadTreeAsync` после сохранения типов.

Корректный паттерн уже реализован в `ExtractTypesForImportedFamilies` (`FamilyManagerMainViewModel.Import.cs:265-288`) и зафиксирован в **ADR-031 правило #1**. Этот код был пропущен в Phase 4c post-mortem — bug не пойман потому что:
- `LoadTreeAsync` в `Import.cs:415` (`ProcessFamilyImportAsync`) обновляет UI ДО FireAndForget — пользователь видит импортированное семейство, но типы ещё не сохранены в БД → UI не показывает новые типы.
- В `ExtractTypesForImportedFamilies` FireAndForget `dispatcher.InvokeAsync(LoadTreeAsync)` обновляет UI ПОСЛЕ save.
- В `ExtractAttributesForImportedFamilies` FireAndForget save без LoadTreeAsync → UI не обновляется.

### 3. Архитектурные ограничения

- **I-05** (хранение ссылок): `Element`/`Connector`/`FamilyType` не хранятся между транзакциями. Только `ElementId`/`FamilyTypeDescriptor` (record).
- **I-09** (Core): `SmartCon.Core` не вызывает Revit API. `IFamilyTypeRepository` — pure C# интерфейс.
- **I-16** (managed storage immutability): `.rfa` файлы в managed storage read-only. Изменения = новая версия.
- **ADR-031** (FireAndForget UI marshalling): любой FireAndForget, обновляющий UI, должен явно маршалить через `IDispatcher.InvokeAsync`.
- **ADR-025 §M-019-003** (migration backlog): `Application.Current?.Dispatcher` → `IDispatcher` — открытый пункт, теперь закрывается этим ADR.
- **ADR-033** (bake-in): `IFamilyTypeCatalogBaker` уже bake-ит типы из `.txt` в managed `.rfa` при импорте с диска. Этот ADR применяется к импорту **активного** файла (без bake-in, типы берутся прямо из открытого `.rfa`).

## Решение

### DEC-001: Унификация `SaveTypesAsync` + `SaveTypesForRunAsync` → `SyncTypesAsync`

Создать единый метод `IFamilyTypeRepository.SyncTypesAsync(catalogItemId, versionId, fileId, runId, types, ct)` с семантикой **DELETE+INSERT в одной транзакции**.

**Почему один метод, а не два:**
- `SaveTypesAsync` уже использует DELETE+INSERT (line 120-157) и работает корректно в project case.
- `SaveTypesForRunAsync` использует UPSERT — имеет баг.
- Унификация = одна семантика = один тест-сьют = меньше места для багов.

**Почему не UPSERT с EXCLUDED + DELETE WHERE NOT IN:**
- `INSERT ... ON CONFLICT DO UPDATE` + отдельный `DELETE` в одной транзакции — это рабочий вариант, но усложняет логику.
- Простой DELETE+INSERT (как в `SaveTypesAsync`) проще, понятнее, и устраняет проблему orphan `extraction_run_id` в существующих данных (при UPSERT старые runId сохраняются, при DELETE+INSERT обновляются на текущий).

**Сигнатура (final):**
```csharp
Task<IReadOnlyDictionary<string, string>> SyncTypesAsync(
    string catalogItemId,
    string? versionId,
    string? fileId,
    string runId,
    IReadOnlyList<FamilyTypeDescriptor> types,
    CancellationToken ct = default);
```

**Алгоритм (rev #3, collapse-to-current):**
1. `BEGIN TRANSACTION`
2. `DELETE FROM family_types WHERE catalog_item_id = @itemId` (без фильтра по version — collapse-to-current)
3. Для каждого типа: `INSERT ... ON CONFLICT(catalog_item_id, type_name) DO UPDATE SET ... RETURNING id` (idempotency guard)
4. `COMMIT` или `ROLLBACK` на исключении

> **rev #3 (collapse-to-current)**: вместо JOIN по `version_label` теперь делаем
> DELETE всех family_types для catalog_item. Это разрушает multi-version type set,
> но `catalog_versions` history сохраняется. Подробности и обоснование — в
> секции "Revisions" выше. Тест: `SyncTypesAsync_ActiveImport_CollapsesAllVersionsToCurrent`.

### DEC-002: Schema migration V15 — FOREIGN KEY на `extracted_attribute_values.type_id`

**Проблема:** `extracted_attribute_values.type_id` — nullable TEXT без FOREIGN KEY. При удалении типов из `family_types` (DEC-001) останутся orphan rows в `extracted_attribute_values`.

**Решение:** Добавить `FOREIGN KEY (type_id) REFERENCES family_types(id) ON DELETE CASCADE` через миграцию V15.

**SQL (recreate таблицы, так как SQLite не поддерживает `ALTER TABLE ADD CONSTRAINT`):**
```sql
-- 1. Clean up orphan rows
DELETE FROM extracted_attribute_values
WHERE type_id IS NOT NULL
  AND type_id NOT IN (SELECT id FROM family_types);

-- 2. Recreate with FK (rev #4: все 4 FK constraints)
CREATE TABLE extracted_attribute_values_new (
    ... все колонки + 4 FK constraints:
    FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id) ON DELETE CASCADE,
    FOREIGN KEY (type_id) REFERENCES family_types(id) ON DELETE CASCADE,
    FOREIGN KEY (attribute_id) REFERENCES attribute_definitions(id) ON DELETE CASCADE,
    FOREIGN KEY (extraction_run_id) REFERENCES family_data_import_runs(id) ON DELETE CASCADE
);
INSERT INTO extracted_attribute_values_new SELECT * FROM extracted_attribute_values;
DROP TABLE extracted_attribute_values;
ALTER TABLE extracted_attribute_values_new RENAME TO extracted_attribute_values;
CREATE INDEX ...;
```

> **rev #4 (V8 SQL parity):** `MigrateV8RecreateExtractedAttributeValues` теперь
> также содержит все 4 FK — иначе V8 recreate ПОСЛЕ V15 (через `EnsureCriticalColumnsAsync`
> на DB с `attribute_id NOT NULL`) терял бы FK на `type_id` и `attribute_id`.
> `PRAGMA foreign_keys = OFF/ON` удалён — это no-op внутри транзакции (SQLite
> limitation), pre-delete orphan rows делает recreate безопасным.

### DEC-003: Fix Bug #2 — `LoadTreeAsync` после save в `ExtractAttributesForImportedFamilies`

Добавить `_dispatcher.InvokeAsync(() => LoadTreeAsync())` после `SaveExtractionResultAsync` в FireAndForget (FamilyEdit.cs:1006-1022), по образцу `ExtractTypesForImportedFamilies` (Import.cs:265-288).

```csharp
FireAndForget(async () =>
{
    try
    {
        foreach (var (catalogItemId, result, versionId, fileId) in extractionResults)
        {
            await _dataImportService.SaveExtractionResultAsync(
                catalogItemId, result, versionId, fileId, CancellationToken.None);
        }
    }
    catch (Exception ex) { SmartConLogger.Warn(...); }

    try
    {
        await _dispatcher.InvokeAsync(() => LoadTreeAsync());
    }
    catch (Exception ex)
    {
        SmartConLogger.Warn($"Tree reload after save failed: {ex.Message} [Action: нажмите Refresh чтобы обновить дерево]");
    }
}, nameof(ExtractAttributesForImportedFamilies));
```

### DEC-004: M-019-003 migration — `IDispatcher` в FamilyManagerMainViewModel

**Проблема (ADR-025 §M-019-003):** `Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher` в ctor VM — ненадёжный в net48 Revit addin (известный баг WPF в Revit).

**Решение:**
- Добавить `IDispatcher Dispatcher` в `FamilyManagerServices` (record, 62→63 props).
- В `FamilyManagerMainViewModel.ctor` заменить `private readonly Dispatcher _uiDispatcher` → `private readonly IDispatcher _dispatcher`, инициализация: `_dispatcher = services.Dispatcher`.
- Во всех 4 местах использования (`RefreshTreeViaExternalEventAsync`, `OnPlacementCompleted`, `SetStatusOnUiThread`) заменить `_uiDispatcher.X` → `_dispatcher.X`.

> **Scope:** Этот ADR не мигрирует `CategoryPickerViewModel:120-124` и `CategoryTreeEditorViewModel:74-79` — они вне scope bug-фикса. Их миграция остаётся в backlog M-019-003 для отдельного PR (если понадобится).

> **Регрессия не ожидается:** ADR-031 уже подтвердил, что `WpfDispatcher` (реализация `IDispatcher`) net48-safe.

## Слои

| Layer | Типы |
|---|---|
| `SmartCon.Core/Services/Interfaces/` | `IFamilyTypeRepository.SyncTypesAsync` (replaces `SaveTypesAsync` + `SaveTypesForRunAsync`) |
| `SmartCon.FamilyManager/Services/LocalCatalog/` | `LocalFamilyTypeRepository.SyncTypesAsync` |
| `SmartCon.FamilyManager/Services/LocalCatalog/` | `FamilyCatalogSql.MigrateV15AddAttributeValuesForeignKey` |
| `SmartCon.FamilyManager/Services/LocalCatalog/` | `LocalCatalogMigrator.MigrateV15Async` |
| `SmartCon.FamilyManager/Services/FamilyManagerServices.cs` | +1 record member: `IDispatcher Dispatcher` |
| `SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.cs` | `_uiDispatcher` → `_dispatcher` (M-019-003) |
| `SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.FamilyEdit.cs` | `ExtractAttributesForImportedFamilies`: +LoadTreeAsync |
| `SmartCon.FamilyManager/Services/LocalCatalog/FamilyDataImportService.cs` | `_typeRepository.SaveTypesForRunAsync(...)` → `SyncTypesAsync(...)` |
| `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.TypeCatalog.cs` | `SaveTypesForRunAsync` → `SyncTypesAsync` |
| `SmartCon.FamilyManager/Services/LoadableFamilyImportOrchestrator.cs` | `SaveTypesAsync` → `SyncTypesAsync(catalogItemId, null, null, "no-run", types, ct)` |
| `SmartCon.FamilyManager/Services/SystemFamilyImportOrchestrator.cs` | `SaveTypesAsync` → `SyncTypesAsync(catalogItemId, null, null, "no-run", types, ct)` |

## Call sites

| Файл | Было | Стало |
|---|---|---|
| `FamilyDataImportService.cs:125` | `SaveTypesForRunAsync(catalogItemId, versionId, fileId, runId, types, ct)` | `SyncTypesAsync(catalogItemId, versionId, fileId, runId, types, ct)` |
| `LocalFamilyImportService.TypeCatalog.cs:200` | `SaveTypesForRunAsync(catalogItemId, versionId, null, runId, types, ct)` | `SyncTypesAsync(catalogItemId, versionId, null, runId, types, ct)` |
| `LoadableFamilyImportOrchestrator.cs:91` | `SaveTypesAsync(match.CatalogItemId!, types, ct)` | `SyncTypesAsync(match.CatalogItemId!, null, null, "no-run", types, ct)` |
| `SystemFamilyImportOrchestrator.cs:100` | `SaveTypesAsync(catalogItemId, descriptors)` | `SyncTypesAsync(catalogItemId, null, null, "no-run", descriptors)` |

> **Breaking change:** `IFamilyTypeRepository.SaveTypesAsync` и `SaveTypesForRunAsync` удаляются. Это part of v2.0.0 (как и V14 sha256 removal).

## Тестирование

### Unit-тесты (xUnit, multi-target net8.0-windows для R25)

`src/SmartCon.Tests/FamilyManager/Repository/LocalFamilyTypeRepositoryTests.cs`:

| Тест | Что проверяет |
|---|---|
| `SyncTypesAsync_TwoTypes_BothAppearInGet` (existing, переименовать) | Базовый insert + read |
| `SyncTypesAsync_CalledTwice_ReplacesExistingTypes` (existing) | DELETE+INSERT работает |
| `SyncTypesAsync_PropertiesCorrectlyStored` (existing) | SortOrder/Id сохраняются |
| `GetTypesForItemAsync_NoTypes_ReturnsEmptyList` (existing) | Read без типов |
| `GetAllTypesBatchAsync_MultipleItems_AllReturned` (existing) | Batch read |
| `HasTypesAsync_*` (existing) | Boolean check |
| **NEW** `SyncTypesAsync_RemovesTypesMissingFromNewList` | **Bug #1 регрессионный**: 3 типа → save с 1 → остался 1 |
| **NEW** `SyncTypesAsync_EmptyList_DeletesAll` | Empty list = delete all |
| **NEW** `SyncTypesAsync_ActiveImport_CollapsesAllVersionsToCurrent` | rev #3: collapse-to-current — все версии для catalog_item удаляются |
| **NEW** `SyncTypesAsync_SyncDescriptor_VersionIdAndFileIdPersisted` | rev #2: SystemFamilyImportOrchestrator — реальные VersionId/FileId сохраняются в БД |
| **NEW** `SyncTypesAsync_DescriptorSortOrderIsUsed` | Bug #7 fix: `@sort = types[i].SortOrder`, не `@sort = i` |
| **NEW** `SyncTypesAsync_CascadeDeletesAttributeValues` | FK CASCADE на extracted_attribute_values работает |

`src/SmartCon.Tests/FamilyManager/Repository/LocalCatalogMigratorTests.cs` (если существует, иначе создать):

| Тест | Что проверяет |
|---|---|
| **NEW** `MigrateV15_AddsAttributeValuesForeignKey` | FK создан в extracted_attribute_values.type_id |
| **NEW** `MigrateV15_CleansOrphanAttributeValuesBeforeAddingFK` | Orphan rows удалены перед recreate |
| **NEW** `MigrateV15_IsIdempotent` | Повторный вызов не падает |

### Manual testing (Revit)

| # | Сценарий | Ожидаемый результат |
|---|---|---|
| 1 | Edit family → delete type → Import Active | Удалённый тип исчезает из БД и UI (Bug #1 fix) |
| 2 | Edit family → add new type → Import Active | Новый тип появляется в UI без Refresh (Bug #2 fix) |
| 3 | Edit family → add 2 new types → Import Active | Оба появляются |
| 4 | Edit family → rename type → Import Active | Старое имя удалено, новое добавлено |
| 5 | Multi-version: edit v1 → delete type, then edit v2 → add same name → import | Оба импорта работают, типы не пересекаются |
| 6 | First run after upgrade from V14 | Migration V15 применяется, нет ошибок, БД работоспособна |

## Совместимость

- R19/R21/R24/R25 — multi-version build, не затрагивается.
- net8.0-windows + net48 — оба используют `IDispatcher`/`WpfDispatcher` без изменений.
- `Microsoft.Data.Sqlite` — поддержка `PRAGMA foreign_keys` (стандарт с SQLite 3.6.19).

## Sources

- [ADR-025](025-refactoring-migration-backlog.md) — M-019-003 migration backlog
- [ADR-031](031-fireandforget-ui-marshalling.md) — FireAndForget + UI marshalling
- [ADR-033](033-bakein-type-catalog.md) — Type Catalog bake-in (контекст для UC-2)
- Issue [#85](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/85) — баг-репорт
- [SQLite ALTER TABLE](https://www.sqlite.org/lang_altertable.html) — limitation on ADD CONSTRAINT
- [SQLite PRAGMA foreign_keys](https://www.sqlite.org/pragma.html#pragma_foreign_keys)

## Consequences

### Positive

- **Bug #1 fix:** удалённые в Revit типы корректно удаляются из `family_types` через `SyncTypesAsync`. Мусор не копится.
- **Bug #2 fix:** новые типы сразу видны в UI без ручного Refresh. UX соответствует ожиданиям.
- **FK CASCADE на `extracted_attribute_values.type_id`** — orphan attribute values невозможны (DB-level guarantee).
- **M-019-003 closed:** `FamilyManagerMainViewModel` использует инжектируемый `IDispatcher` вместо глобального `Application.Current?.Dispatcher`. Testable, net48-safe.
- **API simplification:** один метод (`SyncTypesAsync`) вместо двух (`SaveTypesAsync` + `SaveTypesForRunAsync`).

### Negative

- **Breaking change в `IFamilyTypeRepository`** (v2.0.0): удаление `SaveTypesAsync` + `SaveTypesForRunAsync`. Все call sites обновлены в этом PR. Внешние потребители (если есть) требуют миграции.
- **Миграция V15 recreate таблицы** на больших БД (100k+ строк в `extracted_attribute_values`) может занять несколько секунд. Это **одноразовая** операция при первом запуске после обновления.
- **Filter по (versionId, fileId) в SyncTypesAsync** — означает, что если есть несколько версий одного семейства с разными типами, удаление идёт только для текущей. Это **намеренно** (мультиверсионная семантика) и **не баг**.

### Risks

- **Orphan attribute values в существующих БД** — V15 миграция очищает их перед recreate. Edge case: если FK constraint не может быть создан (corrupted data) — миграция бросит исключение и откатится. Это безопасное поведение — пользователь увидит ошибку в `smartcon.log` и обратится за поддержкой.
- **`SaveTypesAsync` → `SyncTypesAsync` в orchestrator'ах** — orchestrator'ы не передают `versionId`/`fileId` (эти данные находятся в `importResult` в `LoadableFamilyImportOrchestrator` и в `matchingResult` в `SystemFamilyImportOrchestrator`). Передаём `null/null/"no-run"` — для project case это означает "удалить все типы этого catalog item перед вставкой новых". Это **намеренно** — для system/loadable families из проекта это правильная семантика (нет мультиверсий).
- **`IDispatcher` в `FamilyManagerServices`** — `WpfDispatcher` уже зарегистрирован в DI (`ServiceRegistrar.cs:160`). Никаких дополнительных регистраций не нужно.

## Verification

- **Build R25:** `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25` — 0 warnings / 0 errors
- **Build R24/R21/R19:** аналогично
- **Tests:** `dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25` — все тесты зелёные (1379+ → 1386+ после новых)
- **Manual Revit test:** 6 сценариев из таблицы выше
