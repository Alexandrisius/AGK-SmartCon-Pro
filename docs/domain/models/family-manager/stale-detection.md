---
module: family-manager
---
# Модели FamilyManager — Stale Detection

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

## FamilyVersion

Per-family version marker, хранимый в ExtensibleStorage (Schema `SmartCon.FamilyVersion.v1`, ADR-030). Содержит ID записи каталога, метку загруженной версии, время загрузки в проект и версию Revit, которой загружали. `CurrentSchemaVersion` — номер текущей схемы payload (используется при будущих миграциях). `Empty` — sentinel для «маркер не прочитан».

**Файл:** `FamilyVersion.cs`

```csharp
public sealed record FamilyVersion(
    int SchemaVersion,
    string CatalogItemId,
    string VersionLabel,
    DateTimeOffset LoadedAtUtc,
    int SourceRevitVersion)
{
    public const int CurrentSchemaVersion = 1;

    public static FamilyVersion Empty { get; } =
        new(0, string.Empty, string.Empty, DateTimeOffset.MinValue, 0);
}
```

---

## StaleCheckResult

Результат проверки актуальности одного семейства (Issue #69, ADR-030). Содержит enum `StaleReason` (причина, по которой семейство считается устаревшим: `NoEntityStorage` для семейств без ES-маркера, `VersionMismatch`, `RevitVersionMismatch`, `NotInCatalog`, `ContentDrift` — маркер актуален, но контент отредактирован локально после записи маркера, #180) и record `StaleCheckResult` с версиями из каталога и из ES.

**Файл:** `StaleCheckResult.cs`

```csharp
public enum StaleReason
{
    None = 0,
    NoEntityStorage = 1,
    VersionMismatch = 2,
    RevitVersionMismatch = 3,
    NotInCatalog = 4,
    ContentDrift = 5
}

public sealed record StaleCheckResult(
    string CatalogItemId,
    string FamilyName,
    string? CurrentVersionLabel,
    string? LoadedVersionLabel,
    bool IsStale,
    StaleReason Reason);
```

---

## StaleUpdateRequest

Параметры операции обновления устаревших семейств (одиночного или пакетного). `OverwriteParameterValues` пробрасывается в `FamilyLoadOptions.OverwriteParameterValues`. `Recursive` зарезервирован для будущего использования (сейчас всегда `true`).

**Файл:** `StaleUpdateRequest.cs`

```csharp
public sealed record StaleUpdateRequest(
    IReadOnlyList<string> CatalogItemIds,
    bool OverwriteParameterValues,
    bool Recursive = true);
```

---

## StaleBatchUpdateResult

Агрегированный результат пакетного обновления (record `StaleBatchUpdateResult` со счётчиками и списком failed ID для retry) и payload прогресса (record `StaleBatchUpdateProgress`) для `IProgress<>` callback.

**Файл:** `StaleBatchUpdateResult.cs`

```csharp
public sealed record StaleBatchUpdateResult(
    int TotalRequested,
    int SuccessCount,
    int FailedCount,
    IReadOnlyList<string> FailedCatalogItemIds);

public sealed record StaleBatchUpdateProgress(
    int Completed,
    int Total,
    string CurrentFamilyName);
```

---

## FamilyStaleSnapshot

Сессионный снимок результатов проверки актуальности (ADR-030, D-06). Заполняется `IStaleDetector` и инвалидируется при Load/Update/Edit и смене БД. Используется VM для обновления индикаторов категорий без повторного чтения ES. `Empty` — sentinel для «снимок ещё не построен».

**Файл:** `FamilyStaleSnapshot.cs`

```csharp
public sealed record FamilyStaleSnapshot(
    IReadOnlyDictionary<string, StaleCheckResult> Results,
    DateTimeOffset CheckedAtUtc)
{
    public static FamilyStaleSnapshot Empty { get; } =
        new(new Dictionary<string, StaleCheckResult>(), DateTimeOffset.MinValue);
}
```

---

## CategoryStaleStats

Per-category roll-up статистика для stale detection (ADR-030 Phase 24). Возвращается из `IStaleCategoryAggregator.AggregateByCategory` и маппится на `CategoryNodeViewModel.HasStale` / `StaleCount`.

**Файл:** `CategoryStaleStats.cs` (в `SmartCon.Core/Services/Interfaces/`, рядом с `IStaleCategoryAggregator`)

```csharp
public sealed record CategoryStaleStats(bool HasStale, int StaleCount)
{
    public static CategoryStaleStats Empty { get; } = new(false, 0);
}
```

**Семантика:**
- `HasStale = true` если любое catalog item, привязанное к этой категории (с recursive parent expansion), stale.
- `StaleCount` — количество stale items **напрямую** в этой категории (не считая подкатегории — это encoded в `HasStale` родителя).
- `Empty` — дефолт для категории без stale items (используется в `AggregateByCategory`).

---

## StaleSnapshotLogic

Pure static helper для merge / prune snapshot (ADR-030 Phase 24, fix-merge-snapshot bug). Вынесен в Core чтобы unit-тесты могли проверить логику merge без поднятия Revit API.

**Файл:** `StaleSnapshotLogic.cs` (в `SmartCon.Core/Services/Interfaces/`)

```csharp
public static class StaleSnapshotLogic
{
    public static FamilyStaleSnapshot MergeInto(
        FamilyStaleSnapshot? existing,
        IReadOnlyList<StaleCheckResult> newResults,
        DateTimeOffset now);

    public static FamilyStaleSnapshot RemoveFrom(
        FamilyStaleSnapshot? existing,
        IReadOnlyCollection<string> catalogItemIds,
        DateTimeOffset now);
}
```

**Семантика (исправляет баг «Проверить на одной папке теряет stale на другой»):**

- `MergeInto`: **добавляет/перезаписывает** entries из `newResults` в существующий snapshot. Entries для **других** catalog item IDs (других категорий) сохраняются. Это значит что `CheckCategory(catA)` затем `CheckCategory(catB)` сохраняет stale маркеры обеих категорий.
- `RemoveFrom`: **удаляет** entries по `catalogItemIds`. Возвращает тот же snapshot instance если ничего не удалено (zero-allocation). Используется после успешного Update — stale маркер удаляется, остальные сохраняются.
- Both methods **не мутируют** входной snapshot — создаётся новый `FamilyStaleSnapshot`.

---

## LoadableMarkerLogic

Issue #84 / Phase 24 (ADR-030): pure static helper, который после успешного импорта loadable-семейства из активного проекта в каталог (через «Импорт активного файла» / «Импорт выделенных») переписывает `SmartCon_FamilyVersion_v1` ExtensibleStorage маркер на Family-элементе в активном проекте. Без этого шага следующий «Проверить» сразу помечает каждое свеже-импортированное loadable как `StaleReason.NoEntityStorage`, хотя это семейство в проекте и есть авторитетный источник vN+1 для новой строки каталога.

**Файл:** `LoadableMarkerLogic.cs` (в `SmartCon.Core/Services/Interfaces/`)

```csharp
public static class LoadableMarkerLogic
{
    public static Task<MarkerWriteSummary> WriteMarkersForImportedLoadablesAsync(
        IReadOnlyList<FamilyBatchImportItem> loadableItems,
        IReadOnlyList<LoadableFamilyAttributeTask> attributeTasks,
        IFamilyVersionWriter versionWriter,
        int targetRevit,
        CancellationToken ct);

    public sealed record MarkerWriteSummary(
        int SuccessCount,
        int SkippedCount,
        int FailedCount,
        int Total);
}
```

**Семантика:**

- Для каждого `LoadableFamilyAttributeTask` (успешно импортированное loadable) ищет соответствующий `FamilyBatchImportItem` по `PrecomputedCatalogItemId` и вызывает `IFamilyVersionWriter.WriteVersionMarkerAsync(catalogItemId, item.FileName, item.PrecomputedVersionLabel, targetRevit, ct)`. Версия маркера = та же, что попала в каталог (`v1` для нового, `vN+1` для ре-импорта).
- Само содержимое `Family` в проекте **не меняется** — меняется только метаданные маркера.
- Per-family try/catch: исключение на одном семействе **не прерывает** батч — пишется `Warn` с `[Action: ...]` и счётчик `FailedCount++`. Каталог уже принял запись, откатывать импорт нельзя.
- `SkippedCount` растёт если `task.CatalogItemId` пуст или не нашлось matching batch item (теоретический edge case, в orchestrator'е такого не бывает).
- System families (`FamilySource == "system"`) **не появляются** на входе — `ProcessProjectImportAsync` фильтрует их upstream. По дизайну (ADR-030 §Out of Scope) у них нет in-project `Family` элемента в смысле Revit API (`OST_PipeCurves` и т.п. — это `MEPCurve` / `Wall`).
- Aggregate `MarkerWriteSummary`: `SuccessCount + SkippedCount + FailedCount == Total`.

**Используется в:**
- `FamilyManagerMainViewModel.FamilyEdit.cs:WriteVersionMarkersForImportedLoadablesAsync` — тонкая обёртка, делегирующая в этот helper. Вызывается из `ProcessProjectImportAsync` после `LoadableFamilyImportOrchestrator.ImportAndPersistTypesAsync` и перед `_staleDetector.InvalidateCache()`.

**Pure logic** — никакого Revit API в сигнатуре (только `IFamilyVersionWriter`). Позволяет unit-тестировать через hand-written fake `IFamilyVersionWriter` без поднятия Revit (`src/SmartCon.Tests/FamilyManager/Stale/LoadableMarkerLogicTests.cs` — 8 тестов на empty batch / single / multiple / no-match skip / full failure / partial failure isolation / targetRevit passthrough / null-args throws).

---

## SystemTypeStaleLogic

Чистая логика агрегации ES-маркеров типов системного элемента каталога в единый stale-вердикт (Issue #104, ADR-061 + revision 2026-08-05). Системный элемент каталога = N типов; он устарел, если ЛЮБОЙ из его присутствующих в проекте типов имеет НЕСОВПАДАЮЩИЙ маркер (первая ненулевая причина в порядке типов). Тип без маркера (шаблонный/в обход каталога) — НЕ stale: неизвестное происхождение ≠ доказанное устаревание. Не содержит зависимостей от Revit API — юнит-тестируемо (`SystemTypeStaleLogicTests`), по образцу `StaleSnapshotLogic`.

**Файл:** `SystemTypeStaleLogic.cs` (в `SmartCon.Core/Services/Interfaces/`)

```csharp
public static class SystemTypeStaleLogic
{
    public static StaleReason ComputeReason(
        FamilyVersion marker,
        string catalogItemId,
        string? currentVersionLabel,
        int targetRevit);

    public static (bool IsStale, StaleReason Reason, string? LoadedLabel) Aggregate(
        IReadOnlyList<FamilyVersion?> markers,
        string catalogItemId,
        string? currentVersionLabel,
        int targetRevit);
}
```

**Семантика:**

- `Aggregate` принимает маркеры ТОЛЬКО типов, присутствующих в проекте (caller отфильтровывает незагруженные); пустой список = не stale (тип не загружен — как unloadable семейство).
- `ComputeReason` повторяет правила loadable: чужой `CatalogItemId` или другой `VersionLabel` → `VersionMismatch`; другой `SourceRevitVersion` → `RevitVersionMismatch` (при обоих > 0).
- Используется `StaleDetector` (system-ветки Check) и `SystemFamilySyncOrchestrator.IsProjectTypeCurrent` (fast-path размещения) — одинаковый вердикт в обоих местах.

---

## SystemTypeRef

Ссылка на системный тип по ПОЛНОЙ идентичности — (семья, имя). Голое имя типа
неоднозначно: «Стандарт» существует и в «Conduit with Fittings», и в
«Conduit without Fittings» (Issue #183). Используется в
`ISystemTypeSyncOrchestrator.SyncTypes` — оркестратор синхронизирует типы
только по полной паре; `FamilyName == null` — legacy-поведение (первый
матч по имени).

**Файл:** `SystemTypeRef.cs`

```csharp
public sealed record SystemTypeRef(
    string Name,
    string? FamilyName = null,
    string? FamilyKey = null);
```

Issue #190 (ADR-064): `FamilyKey` — locale-invariant идентичность семьи
(`family_types.family_key`, схема V27); при наличии предпочтительнее
локализованного `FamilyName`.

---

## SystemTypeIdentityKey

Канонический identity-ключ системного типа — `"TOKEN|NAME"` (upper-invariant):
TOKEN = `family_key`, если есть, иначе локализованный `family_name`, иначе
пусто (legacy pre-V26). Единственное определение формата ключа (Issue #190/#191,
ADR-064) — используется stale-детектором, репозиторием типов (карта результатов
`SyncTypesAsync`), атрибутным пайплайном (`FamilyDataImportService`) и деревом,
чтобы все слои строили ОДИН ключ для ОДНОГО дескриптора.

**Файл:** `SystemTypeIdentityKey.cs`

```csharp
public static class SystemTypeIdentityKey
{
    public static string Build(string? familyKey, string? familyName, string typeName);
}
```

---

## SystemFamilyKeys

Locale-invariant токены системных семей (Issue #190, ADR-064; #215 — duct
Shape) — значения `family_types.family_key`. Вычисляются `SystemFamilyKeyResolver` (SmartCon.Revit)
из per-category discriminator'ов: `IsWithFitting` (Conduit/CableTray),
`WallType.Kind`, `StairsType.ConstructionMethod`, `MEPCurveType.Shape`
(DuctType — ТРИ семейства: round/rectangular/oval, FHV7); для односемейных
категорий — `SingleFamily` (`"Single"`). Константы в Core, чтобы FamilyManager сравнивал
ключи без ссылок на Revit API (I-09).

**Файл:** `SystemFamilyKeys.cs`

```csharp
public static class SystemFamilyKeys
{
    public const string SingleFamily = "Single";
    public const string ConduitWithFittings = "Conduit.WithFittings";
    public const string ConduitWithoutFittings = "Conduit.WithoutFittings";
    public const string CableTrayWithFittings = "CableTray.WithFittings";
    public const string CableTrayWithoutFittings = "CableTray.WithoutFittings";
    public const string WallBasic = "Wall.Basic";   // + WallCurtain, WallStacked, WallUnknown
    public const string StairsAssembled = "Stairs.Assembled";  // + StairsCastInPlace, StairsPrecast, StairsUnknown
    public const string DuctRound = "Duct.Round";   // + DuctRectangular, DuctOval, DuctUnknown (#215, FHV7)
}
```

---

## TypePresenceState

Трёхзначное состояние присутствия типа в активном проекте (Issue #187) —
управляет точкой-индикатором в дереве каталога: серая (тип не загружен),
синяя (загружен и актуален), оранжевая (загружен, но устарел).
Вычисляется из двух флагов узла типа: `IsInProject` (presence-снимок
активного документа) и `IsStaleInProject` (per-type stale-карта
`StaleDetector` для системных типов; для loadable — leaf-флаг `IsStale`).

**Файл:** `TypePresenceState.cs`

```csharp
public enum TypePresenceState
{
    NotInProject,     // серый — типа нет в активном проекте
    InProject,        // синий — загружен и актуален
    StaleInProject,   // оранжевый — загружен, но устарел относительно каталога
}
```
