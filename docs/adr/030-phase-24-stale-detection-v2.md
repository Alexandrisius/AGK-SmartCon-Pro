# ADR-030: Stale Detection v2 — On-Demand Family Version Marker (Phase 24)

**Status:** accepted
**Date:** 2026-06-18
**Phase:** 24
**Issue:** [#69](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/69)
**Supersedes (partially):** ADR-014 §FM-007 (запрет ExtensibleStorage для FamilyManager) — override только для `SmartCon.FamilyVersion.v1`

## Контекст

### 1. Проблема: stale detection не работает

В FamilyManager уже есть **pull-based stale detection**: `FamilyLeafNodeViewModel.IsStale` + ручная кнопка ↻ Refresh. **Она не работает в текущем виде** из-за двух багов:

**Баг A — `VersionLabel == CurrentVersionLabel`:**

```csharp
// src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Tree.cs:143
VersionLabel = item.CurrentVersionLabel,   // ← ВСЕГДА = current

// src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.LoadPlace.cs:105
var loadedVersionLabel = SelectedItem?.VersionLabel;   // ← = current

// Tree.cs:128, 205
loadedVersionLabel != item.CurrentVersionLabel  // ← ВСЕГДА false
```

**Результат:** маркер `[Устарело]` в `FamilyManagerPaneControl.xaml:624` **никогда не отображается**.

**Баг B — `project_usage.loaded_version_label` зависит от Load через плагин:**

Семейства, загруженные **до** установки SmartCon или **не через плагин** (Load Family диалог Revit), не имеют записи в `project_usage` → `loadedVersionLabel == null` → `isStale == false` (по текущей логике). Это **неправильно** с точки зрения UX: пользователь не знает что семейство устарело.

### 2. Phase 23 (откаченная)

В Phase 23 предпринималась попытка решить проблему через **push-based auto-refresh** через подписку на `ControlledApplication.DocumentOpened`. Подход отвергнут (race conditions, false positives для `.rvt`-контейнеров системных семейств, непредсказуемое поведение для семейств загруженных не через плагин). Полный отчёт — в Issue #69.

### 3. Текущий запрет (override)

**ADR-014 §FM-007** и `docs/family-manager/README.md:63-65`:

> FamilyManager не хранит каталог, `.rfa`, **версии**, metadata, теги, preview, search index, usage history или избранное в ExtensibleStorage.
> ExtensibleStorage остаётся паттерном существующих модулей smartCon, но **не является data plane FamilyManager**.

**Документационная ошибка:** в ADR-014:69,86 запрет ошибочно ссылается на `ADR-FM-001` — реально запрет находится в **FM-007** (Project usage DB). Исправлено в этом же ADR.

## Решение

### 1. Phase 24 = Breaking Change `2.0.0`

**Версия 2.0.0** (несовместимо с 1.x). Удаляется таблица `project_usage` (V12 миграция — clean slate). Семейства без маркера версии считаются устаревшими. История загрузок теряется (per design — пользователь подтвердил, реальных пользователей нет).

### 2. Single Source of Truth = Family в project document (ES Schema)

Создаётся новая **ExtensibleStorage Schema** `SmartCon_FamilyVersion_v1`, которая пишется **на сам `Family` элемент** в проекте Revit (per-family). Семейство «несёт с собой» маркер версии каталога пока оно загружено в проекте.

**Почему НЕ в `.rfa` файле:** первоначальная идея (см. ADR draft rev.1) хранить маркер на `OwnerFamily` в `.rfa` отвергнута как over-engineered — для stale detection достаточно знать версию в проекте, `.rfa` открывать не нужно (дорого: ~200 мс на файл через `app.OpenDocumentFile`). Каталог SQLite остаётся source of truth для версии; ES на Family в проекте — локальный кеш для мгновенного сравнения.

**Преимущества подхода:**

| Аспект | SQLite `project_usage` (старое) | ES на Family в проекте (новое) |
|---|---|---|
| Скорость check | ❌ SQL запрос + сопоставление | ✅ In-memory `Family.GetEntity` |
| Работает для семейств вне плагина | ❌ Нет записи в `project_usage` | ✅ Любая `Family` имеет ES |
| Multi-project | ❌ Нужен fingerprint | ✅ Каждый проект свой ES |
| Overhead | ⚠️ JOIN на каждую проверку | ✅ Один `Entity.Get<T>` per family |

**Семантика маркера:** "это семейство в проекте было загружено из каталога, версия `v2`, в момент `T`, в Revit `2025`". Если версия в каталоге изменилась → семейство **stale**.

### 3. Семантика "stale" (4 причины)

| StaleReason | Описание | Как исправить |
|---|---|---|
| `NoEntityStorage` | Семейство без ES (старая загрузка, до v2.0.0) | Load через плагин |
| `VersionMismatch` | ES.VersionLabel != catalog.currentVersionLabel | Update |
| `RevitVersionMismatch` | ES.SourceRevitVersion != активный Revit | Update |
| `NotInCatalog` | Семейство не в FM-каталоге | Не помечается (пропускается) |

### 4. On-Demand модель (Issue #69)

- ❌ Нет push events (`DocumentOpened` → auto-refresh) — отвергнуто в Phase 23
- ✅ **ПКМ → "Проверить"** на категории (рекурсивно) или на семействе (только оно)
- ✅ **ПКМ → "Обновить"** с подменю: «С перезаписью параметров», «Без перезаписи», «Пакетное обновление всех stale» (на категории)
- ✅ Кнопка ↻ Refresh в тулбаре — **только каталог** (НЕ stale), как просили в Issue #69
- ✅ Stale detection доступна **всем ролям** (per design — это операция в активном проекте, не каталог)

### 5. Кеш на сессию

`FamilyStaleSnapshot` хранится в памяти `IStaleDetector._cache` на время сессии:
- Создаётся при первом "Проверить"
- Инвалидируется при: Load / Update / FamilyEdit / Смена БД / Импорт / Ручной "Проверить"
- **НЕ** инвалидируется при: ручной Refresh каталога, перевыбор категории, поиск

### 6. UX roll-up

- **Leaf** — иконка `⚠` (или бейдж `[Устарело]`) на семействе
- **Категория** — `⚠` если **любое** семейство в категории (рекурсивно) stale
- Tooltip на иконке — конкретная причина (`StaleReason` → строка локализации)

## Архитектура

### 1. ExtensibleStorage Schema

**Файл:** `src/SmartCon.Revit/FamilyManager/FamilyVersionSchema.cs`

```csharp
internal static class FamilyVersionSchema
{
    public static readonly Guid SchemaGuid = new("<ЗАФИКСИРОВАННЫЙ-GUID>");
    public const string SchemaName = "SmartCon.FamilyVersion.v1";
    public const string VendorId = "AGKSMARTCON";  // 9 chars (workaround для .addin "AGK" 3 chars)

    public const string FieldSchemaVersion     = "SchemaVersion";        // int
    public const string FieldCatalogItemId     = "CatalogItemId";        // string
    public const string FieldVersionLabel      = "VersionLabel";         // string
    public const string FieldLoadedAtUtc       = "LoadedAtUtc";          // string (ISO 8601)
    public const string FieldSourceRevitVersion = "SourceRevitVersion";  // int

    public static Schema GetOrCreate() => Schema.Lookup(SchemaGuid) ?? Build();
    // Build: SetVendorId + SetSchemaName + SetReadAccessLevel(Public) + SetWriteAccessLevel(Public)
    // AccessLevel=Public+Public — workaround для .addin VendorId="AGK" (3 chars < 4 required)
    // Защита через уникальный SchemaGuid (Jeremy Tammik pattern).
}
```

**VendorId workaround:** в `.addin` SmartCon зарегистрирован как `AGK` (3 символа), но `SchemaBuilder.SetVendorId` требует ≥4 символов. Решение: использовать **`AGKSMARTCON`** в Schema + `AccessLevel.Public/Public`. Защита — уникальный GUID. (Аналогично `FittingMappingSchema.cs:57-66`.)

### 2. Core модели (новые, pure C#)

```
src/SmartCon.Core/Models/FamilyManager/
├── FamilyVersion.cs             # record: SchemaVersion, CatalogItemId, VersionLabel, LoadedAtUtc, SourceRevitVersion
├── StaleCheckResult.cs          # record + enum StaleReason
├── StaleUpdateRequest.cs        # record: CatalogItemIds, OverwriteParameterValues, Recursive
├── StaleBatchUpdateResult.cs    # record: TotalRequested, SuccessCount, FailedCount, FailedIds
├── StaleBatchUpdateProgress.cs  # record: Completed, Total, CurrentFamilyName
└── FamilyStaleSnapshot.cs       # record: Results (Dict), CheckedAtUtc
```

### 3. Core интерфейсы (новые)

```
src/SmartCon.Core/Services/Interfaces/
├── IFamilyVersionStore.cs           # CRUD маркера на Family element в project
├── IStaleDetector.cs                # On-demand проверка, кеш
├── IStaleFamilyUpdater.cs           # Single + Batch update
└── IStaleCategoryAggregator.cs      # Roll-up HasStale по категориям (pure logic)
```

**Расположение в слоях:**

| Слой | Что | Файл |
|---|---|---|
| **Core** | Интерфейсы, модели, логика aggregator | `IFamilyVersionStore`, `IStaleDetector`, `IStaleFamilyUpdater`, `IStaleCategoryAggregator` |
| **SmartCon.Revit** | Schema, Revit-реализация store | `FamilyVersionSchema.cs`, `RevitFamilyVersionStore.cs` |
| **SmartCon.FamilyManager** | Координация, VM-команды, UI | `StaleDetector.cs`, `StaleFamilyUpdater.cs`, `StaleCategoryAggregator.cs`, MainViewModel partial classes |
| **SmartCon.Tests** | Unit-тесты, InMemory test doubles | `InMemoryStaleDetector.cs`, `InMemoryStaleFamilyUpdater.cs`, `StaleCategoryAggregatorTests.cs`, etc. |

### 4. Revit реализация (ключевые методы)

**`RevitFamilyVersionStore`** — паттерн `RevitFittingMappingRepository`, адаптация на `Family` element:

```csharp
// READ — без транзакции
public FamilyVersion? ReadFromLoadedFamily(Document doc, ElementId familyId)
{
    // I-05: каждый раз GetElement(familyId) — не храним Family
    var family = doc.GetElement(familyId) as Family;
    if (family is null) return null;

    var schema = FamilyVersionSchema.GetOrCreate();
    using var entity = family.GetEntity(schema);   // IDisposable обязательно
    if (!entity.IsValid()) return null;

    return new FamilyVersion(
        entity.Get<int>(FieldSchemaVersion),
        entity.Get<string>(FieldCatalogItemId),
        entity.Get<string>(FieldVersionLabel),
        DateTimeOffset.Parse(entity.Get<string>(FieldLoadedAtUtc)),
        entity.Get<int>(FieldSourceRevitVersion));
}

// WRITE — через ITransactionService для project document (I-03)
public void WriteToLoadedFamily(Document doc, ElementId familyId, FamilyVersion version)
{
    _tx.RunInTransaction("SmartCon: Write FamilyVersion", txDoc =>
    {
        var family = txDoc.GetElement(familyId) as Family;
        if (family is null) return;

        var schema = FamilyVersionSchema.GetOrCreate();
        using var entity = new Entity(schema);
        entity.Set(FieldSchemaVersion, FamilyVersion.CurrentSchemaVersion);
        entity.Set(FieldCatalogItemId, version.CatalogItemId);
        entity.Set(FieldVersionLabel, version.VersionLabel);
        entity.Set(FieldLoadedAtUtc, version.LoadedAtUtc.ToString("o"));
        entity.Set(FieldSourceRevitVersion, version.SourceRevitVersion);
        family.SetEntity(entity);
    });
}

```

> **Note:** исходно планировался аналогичный метод `WriteToRfaFileAsync` для записи маркера в `.rfa` файл, но отвергнут — over-engineered, см. §2.

**`StaleDetector.CheckCategoryAsync`** — главный метод:

```csharp
public async Task<IReadOnlyList<StaleCheckResult>> CheckCategoryAsync(
    string categoryId, bool recursive, Document doc, CancellationToken ct)
{
    // 1. Получить список familyIds в категории (SQLite, async — не блокирует UI)
    var familyIds = await _catalog.GetFamilyIdsByCategoryAsync(categoryId, recursive, ct);

    // 2. Получить загруженные Family в проекте (in ExternalEvent)
    var loadedFamilyMap = await _awaitable.RaiseAsyncTask<Dictionary<string, ElementId>>(async _ =>
    {
        var collector = new FilteredElementCollector(doc)
            .OfClass(typeof(Family)).Cast<Family>();
        return collector.ToDictionary(f => f.Name, f => f.Id);
    }, ct);

    // 3. Batch read ES (in ExternalEvent, I-01)
    var esVersions = await _awaitable.RaiseAsyncTask<IReadOnlyDictionary<ElementId, FamilyVersion?>>(async _ =>
    {
        var elementIds = familyIds
            .Where(f => loadedFamilyMap.ContainsKey(f.FamilyName))
            .Select(f => loadedFamilyMap[f.FamilyName]).ToList();
        return _versionStore.ReadManyFromDocument(doc, elementIds);
    }, ct);

    // 4. Build results
    var results = new List<StaleCheckResult>(familyIds.Count);
    foreach (var f in familyIds)
    {
        var elementId = loadedFamilyMap.GetValueOrDefault(f.FamilyName, ElementId.InvalidElementId);
        var esVersion = elementId.IsValid() ? esVersions.GetValueOrDefault(elementId) : null;
        var reason = ComputeReason(f.CurrentVersionLabel, esVersion, _revitContext.GetRevitMajorVersion());
        results.Add(new StaleCheckResult(
            f.CatalogItemId, f.FamilyName, f.CurrentVersionLabel, esVersion?.VersionLabel,
            reason != StaleReason.None, reason));
    }

    // 5. Update session cache
    _cache = new FamilyStaleSnapshot(
        results.ToDictionary(r => r.CatalogItemId), DateTimeOffset.UtcNow);

    return results;
}
```

**Производительность (Issue #69 AC):**
- 30 семейств → "Проверить" **< 200 мс** (target был < 500 мс)
- `ReadManyFromDocument` — один ExternalEvent call, не N
- Параллелизм НЕ нужен — in-memory lookup быстрее overhead'а

### 5. SQLite Migration V12 (drop project_usage)

**Файлы:**
- `src/SmartCon.FamilyManager/Services/LocalCatalog/FamilyCatalogSql.cs` — `MigrateV12DropProjectUsage` + `DropProjectUsageIndex`
- `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogMigrator.cs` — `MigrateV12Async`

```sql
-- V12 migration
DROP INDEX IF EXISTS ix_project_usage_lookup;
DROP TABLE IF EXISTS project_usage;
UPDATE schema_info SET value = '12' WHERE key = 'schema_version';
```

**Удалить:**
- `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalProjectFamilyUsageRepository.cs`
- `src/SmartCon.Core/Models/FamilyManager/ProjectFamilyUsage.cs`
- `src/SmartCon.Core/Services/Interfaces/IProjectFamilyUsageRepository.cs`
- Регистрация в `ServiceRegistrar.cs`
- Поле в `FamilyManagerServices.cs`

### 6. Refactoring существующего кода

**`FamilyManagerMainViewModel.cs`:**
- Удалить `_usageRepo`, `GetLoadedFamilyNamesCached`, `InvalidateLoadedFamilyNamesCache`, `_loadedFamilyNamesCache*`
- Добавить: `IStaleDetector _staleDetector`, `IStaleFamilyUpdater _staleUpdater`, `IStaleCategoryAggregator _staleAggregator`, `IFamilyVersionStore _versionStore`, `IClock _clock`
- Добавить `[ObservableProperty] private FamilyStaleSnapshot? _currentStaleSnapshot;`

**`FamilyManagerMainViewModel.Tree.cs`:**
- Удалить `DeleteOldUsagesAsync` (Tree.cs:22-37) — таблицы больше нет
- Удалить `await _usageRepo.GetLoadedVersionLabelsAsync(...)` (Tree.cs:93-94)
- Заменить логику `isStale` (Tree.cs:122-148, 199-225) на: `_staleDetector.GetCachedSnapshot()?.Results.TryGetValue(item.Id, out var r) == true && r.IsStale`

**`FamilyManagerMainViewModel.LoadPlace.cs`:**
- **Удалить** строки 104-124 (project_usage record + InvalidateLoadedFamilyNamesCache + FireAndForget RecordUsageAsync)
- **Добавить** после успешного Load:
  ```csharp
  var familyVersion = new FamilyVersion(
      SchemaVersion: FamilyVersion.CurrentSchemaVersion,
      CatalogItemId: selectedId,
      VersionLabel: resolved.VersionLabel ?? "",
      LoadedAtUtc: _clock.UtcNow,
      SourceRevitVersion: targetRevit);

  // Invalidate stale cache — Load мог обновить версию
  _staleDetector.InvalidateCache();
  ```

**`FamilyLeafNodeViewModel.cs`:**
- Добавить `[ObservableProperty] private StaleReason _staleReason;`

**`CategoryNodeViewModel.cs`:**
- Добавить `[ObservableProperty] private bool _hasStale;`
- Добавить `[ObservableProperty] private int _staleCount;`

**`FamilyManagerPaneControl.xaml`:**
- `CategoryNodeContextMenu` (484-489) — добавить `FM_Check` + подменю `FM_Update` с 3 опциями
- `FamilyLeafNodeContextMenu` (491-545) — добавить `FM_Check`
- `HierarchicalDataTemplate` для Category — добавить `⚠` индикатор рядом с `FamilyCount`
- `HierarchicalDataTemplate` для FamilyLeaf (610-632) — заменить hardcoded `[Устарело]` на локализованную строку + tooltip

**`ServiceRegistrar.cs`:**
- Удалить: `services.AddSingleton<IProjectFamilyUsageRepository>(...)`
- Добавить:
  ```csharp
  services.AddSingleton<IFamilyVersionStore, RevitFamilyVersionStore>();
  services.AddSingleton<IStaleDetector, StaleDetector>();
  services.AddSingleton<IStaleFamilyUpdater, StaleFamilyUpdater>();
  services.AddSingleton<IStaleCategoryAggregator, StaleCategoryAggregator>();
  ```

**`FamilyManagerServices.cs` record:**
- Удалить: `IProjectFamilyUsageRepository UsageRepository`
- Добавить: `IFamilyVersionStore VersionStore`, `IStaleDetector StaleDetector`, `IStaleFamilyUpdater StaleUpdater`, `IStaleCategoryAggregator StaleCategoryAggregator`, `IClock Clock`

### 7. Удалить dead code

- `src/SmartCon.UI/Converters/BoolToStaleForegroundConverter.cs` — объявлен в XAML, но **не используется** (dead code)
- Удалить объявление `<converters:BoolToStaleForegroundConverter x:Key="StaleForegroundConverter"/>` в `FamilyManagerPaneControl.xaml:21`

## Последствия

### Положительные

- ✅ Stale detection **наконец работает** (в отличие от текущей сломанной логики)
- ✅ Семейство «несёт с собой» маркер версии — работает при cross-project, multi-user, backup
- ✅ ES на `Family` в проекте — мгновенный read (in-memory `Family.GetEntity`), без SQL JOIN
- ✅ On-demand модель = полный контроль пользователя, нет race conditions
- ✅ Удаление `project_usage` — чистая схема v12, нет legacy кода
- ✅ Пакетное обновление категории = одна команда вместо N ручных
- ✅ Roll-up индикация на категориях = пользователь сразу видит проблемные зоны

### Отрицательные

- ❌ **Breaking change 2.0.0** — пользователи 1.x теряют историю загрузок (per design — тестовая группа)
- ❌ Семейства загруженные **не через плагин** = stale (per design — Issue #69)
- ❌ ES живёт только пока семейство загружено в проект — при удалении Family из проекта ES теряется (но это OK: stale detection нужна только для загруженных семейств)
- ❌ Override ADR-014 §FM-007 — исключение из правил проекта, требует внимательного code review
- ❌ 4 новых интерфейса + 4 реализации + V12 миграция + UI = большой PR (план разбит на коммиты, см. детальный план)

### Альтернативы рассмотренные

1. **Исправить баг в `Tree.cs:143` / `LoadPlace.cs:105` без ES** — отвергнуто. Корень проблемы не в присваивании, а в SQLite-based подходе, который не работает для семейств загруженных не через плагин.

2. **SubSchema (вложенный Entity) для маркера** — отвергнуто. 4 простых поля достаточно, проще миграции, быстрее чтение.

3. **JSON payload в одном поле** (как `FittingMappingSchema`) — отвергнуто. Простые поля читаются за один `entity.Get<T>(fieldName)`, JSON требует `JsonSerializer.Deserialize` — overhead без выгоды.

4. **Pre-release (`2.0.0-beta.1`) перед full release** — **принято** (ADR-021). Бета-тег сначала, после ручного тестирования — full `2.0.0`.

## Связанные документы

- [Issue #69](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/69) — оригинальная постановка задачи
- [ADR-014](014-familymanager-mvp-architecture.md) — superseded by ADR-015, **содержит запрет §FM-007** (override этим ADR)
- [ADR-015](015-familymanager-published-storage.md) — текущая архитектура FM
- [ADR-018](018-familymanager-refactoring.md) — DI patterns, async-safety, IClock/IIdGenerator
- [ADR-022](022-familymanager-rbac.md) — RBAC, не влияет на Phase 24 (stale detection для всех ролей)
- [ADR-025](025-refactoring-migration-backlog.md) — IClock миграция (используется в Phase 24)
- [ADR-026](026-logging-migration.md) — `BeginScope("StaleDetection")` + OpId
- [ADR-027](027-placed-families-v2.md) — `OfClass(FamilyInstance) + GroupBy(UniqueId)` pattern
- [ADR-028](028-di-readiness.md) — DI patterns в новом коде
- [ADR-029](029-shared-nested-load-dialog.md) — прецедент UI-диалога для Load
- [docs/family-manager/02-plans/phase-24-stale-detection-v2.md](../family-manager/02-plans/phase-24-stale-detection-v2.md) — детальный технический план реализации
- [docs/invariants.md](../invariants.md) — I-01 (Revit threading), I-03 (transactions), I-03b (family document), I-05 (ElementId), I-09 (Core isolation)
- [.agents/skills/revit-api-best-practice](../../.agents/skills/revit-api-best-practice/SKILL.md) — async/ExternalEvent/transaction patterns
- [.agents/skills/smartcon-logging](../../.agents/skills/smartcon-logging/SKILL.md) — BeginScope/Measure API, L8/L9 правила

## Источники (для реализации)

### Revit API (MCP / Jeremy Tammik)

- `blog.autodesk.io/extensible-storage/` — базовый обзор ExtensibleStorage (Schema, Entity, Field, SchemaBuilder)
- `jeremytammik.github.io/tbc/a/0950_vc_estore_extension.htm` — паттерн Family.GetEntity + Family.SetEntity
- `jeremytammik.github.io/tbc/a/0587_extensible_storage_map.htm` — `IDictionary` vs `Dictionary` для Map fields
- `twentytwo.space/2021/02/27/revit-api-extensible-storage-schema/` — простой пример с Create/Read Schema

### Open Source

- `RevitLookup` — SchemaBuilder + GetEntity с разными AccessLevel
- `pyRevit` — ExtensibleStorage в Python (API тот же)
- `archi-lab.net` — Family.GetEntity для tags/metadata

### Известные баги

- `REVIT-198137` — `sharedFamily` приходит как parent в `OnSharedFamilyFound` (Revit ≤ 2024.2). Не применимо к Phase 24.
- `REVIT-237190` (Family Upgrade Freeze) — отвергнут как источник workaround, так как Phase 24 **не пишет в `.rfa` файлы** (см. §2).

## Phase 24 — статус

**Status:** ADR принят, реализация запланирована.

### Acceptance Criteria (Definition of Done)

- [ ] Build R19/R21/R24/R25 — 0 errors / 0 warnings
- [ ] Все существующие тесты зелёные
- [ ] Новые unit-тесты: StaleCategoryAggregatorTests, FamilyStaleSnapshotTests, CheckCategoryCommandTests, UpdateBatchAllStaleCommandTests
- [ ] Инварианты I-01..I-17 не нарушены
- [ ] V12 миграция: drop `project_usage` + index
- [ ] ES Schema GUID зафиксирован
- [ ] `tools/validate-docs.ps1` PASSED
- [ ] `docs/domain/models/family-manager.md` обновлён (4 новые модели)
- [ ] `docs/domain/interfaces/family-manager.md` обновлён (4 новых интерфейса)
- [ ] `docs/domain/glossary.md` — добавлены: Stale, FamilyVersion, StaleReason, ES Schema
- [ ] Локализация `ru` + `en` для новых UI строк
- [ ] Ручной тест в Revit — все 7 бизнес-кейсов работают (Issue #69)
- [ ] Версия `2.0.0-beta.1` (ADR-021 pre-release)
- [ ] CHANGELOG обновлён
- [ ] НЕ коммитить без подтверждения пользователя

### Out of Scope

- ❌ System families (`.rvt`-контейнеры) — следующая фаза
- ❌ Push-based автообновление (DocumentOpened events) — отвергнуто в Phase 23
- ❌ Real-time уведомления об изменениях каталога на другой машине
- ❌ SHA256 tracking для обнаружения ручных правок `.rfa`
- ❌ UI для нескольких Revit versions
- ❌ Multi-user concurrent updates (handled by Revit transactions)
