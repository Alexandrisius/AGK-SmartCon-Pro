---
module: family-manager-interfaces
---
# Интерфейсы FamilyManager — Stale Detection

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## IFamilyVersionStore

CRUD для ES-маркера `SmartCon_FamilyVersion_v1` (ADR-030). Маркер хранится на `Family` для загруженных семейств в активном проекте. Все методы синхронные — вызываются из Revit main thread (I-01), запись обёрнута в транзакцию `ITransactionService` (I-03).

**Файл:** `IFamilyVersionStore.cs`

```csharp
public interface IFamilyVersionStore
{
    FamilyVersion? ReadFromLoadedFamily(Document doc, ElementId familyId);
    void WriteToLoadedFamily(Document doc, ElementId familyId, FamilyVersion version);
    IReadOnlyDictionary<ElementId, FamilyVersion?> ReadManyFromDocument(
        Document doc, IEnumerable<ElementId> familyIds);
}
```

---

## IStaleDetector

On-demand проверка актуальности семейств в активном проекте (ADR-030, Issue #69; системные семейства — Issue #104). Все проверки читают ES-маркер через `IFamilyVersionStore` (in-memory). Сессионный снимок **мержится** при каждой проверке (другие категории сохраняются) и **прунится** при успешном Update (только обновлённые). `InvalidateCache` — полный сброс (D-10: Edit/смена БД); `InvalidateItems` — селективный сброс только импортированных (batch-импорт, иначе бейджи прошлых семейств пропадали). `GetMergedSnapshot` никогда не возвращает `null` (#220): холодный/инвалидированный кэш стартует мерж с пустого снимка — apply-путь бейджей после DnD не превращается в silent no-op. Контентная верификация (#180, #218): когда маркер сам не может свидетельствовать (отсутствует / совпадает с текущей / указывает на сиротский id), доказывается контент по FHV10-хэшу; сиротский id (item удалён и переимпортирован под новым id) лечится перезаписью маркера с правильным id при совпадении контента.

**Файл:** `IStaleDetector.cs`

```csharp
public interface IStaleDetector
{
    Task<StaleCheckResult> CheckFamilyAsync(
        string catalogItemId, string familyName, Document doc,
        ElementId familyId, CancellationToken ct);

    Task<IReadOnlyList<StaleCheckResult>> CheckCategoryAsync(
        IReadOnlyList<string>? categoryIds, Document doc, CancellationToken ct);

    Task<StaleCheckResult?> CheckSystemFamilyAsync(
        string catalogItemId, string displayName, Document doc, CancellationToken ct);

    FamilyStaleSnapshot? GetCachedSnapshot();
    FamilyStaleSnapshot GetMergedSnapshot(IReadOnlyList<StaleCheckResult> newResults);
    void MarkUpdated(IReadOnlyCollection<string> catalogItemIds);
    void InvalidateCache();
    void InvalidateItems(IReadOnlyCollection<string> catalogItemIds);
    IReadOnlyDictionary<string, bool>? GetSystemTypeStaleMap(string catalogItemId);
    void MarkSystemTypeUpdated(string catalogItemId, string typeKey);
}
```

---

## IStaleFamilyUpdater

Обновление семейств в активном проекте — перезагрузка текущей версии из каталога (ADR-030, Issue #69 AC). После успешного обновления пишет новый `FamilyVersion`-маркер через `IFamilyVersionStore`. Хост **обязан** вызвать `IStaleDetector.MarkUpdated([id])` после. `UpdateBatchAsync` обрабатывает семейства последовательно и отчитывается о прогрессе через `IProgress<>`. Контентная верификация едина для family-doc и проекта (#222): pre-verify skip, арбитраж failed-reload и post-verify по FHV10-хэшу (файловый снапшот ограничен embedded-набором типов ТОЛЬКО в проекте — type-set rule; в family-doc full-vs-full). `UpdateFamilyAsync` возвращает `StaleFamilyUpdateResult` с per-type отчётом об изменённых типах (#222); `fromVersionLabel` (из снимка stale / маркера ES) управляет отчётом, `null` — отчёт пропускается (batch).

**Файл:** `IStaleFamilyUpdater.cs`

```csharp
public interface IStaleFamilyUpdater
{
    Task<StaleFamilyUpdateResult> UpdateFamilyAsync(
        string catalogItemId,
        bool overwriteParameterValues,
        string? fromVersionLabel,
        CancellationToken ct);

    Task<StaleBatchUpdateResult> UpdateBatchAsync(
        StaleUpdateRequest request,
        IProgress<StaleBatchUpdateProgress>? progress = null,
        CancellationToken ct = default);
}
```

---

## IStaleCategoryAggregator

Чистая логика агрегации результатов проверки по дереву категорий (ADR-030). Используется `MainViewModel.ApplyStaleResultsToTreeAsync` для обновления `CategoryNodeViewModel.HasStale` и `StaleCount` после stale check. Вспомогательный интерфейс `ICategoryNodeInfo` абстрагирует `CategoryNodeViewModel`, сохраняя Core независимым от UI (I-09) — реализации лежат в `SmartCon.FamilyManager` (adapter поверх `CategoryNodeViewModel`).

**Файл:** `IStaleCategoryAggregator.cs`

```csharp
public interface IStaleCategoryAggregator
{
    IReadOnlyDictionary<string, bool> AggregateByCategory(
        IReadOnlyList<StaleCheckResult> results,
        IReadOnlyDictionary<string, IReadOnlyList<string>> categoryIndex);

    IReadOnlyDictionary<string, IReadOnlyCollection<string>> BuildCatalogToCategoryMap(
        IEnumerable<string> catalogItemIds,
        IEnumerable<ICategoryNodeInfo> rootNodes);
}

public interface ICategoryNodeInfo
{
    string CategoryId { get; }
    IReadOnlyList<ICategoryNodeInfo> Children { get; }
}
```

---

**FamilyManagerServices Aggregate (Phase 4b):**

`public sealed record FamilyManagerServices(...)` с 30 readonly properties, заменяет 30-param ctor `FamilyManagerMainViewModel`. **Файл:** `SmartCon.FamilyManager/ViewModels/FamilyManagerServices.cs`. **DI:** `AddSingleton<FamilyManagerServices>()` (auto-resolve).

---

## ISystemTypeVersionStore

CRUD для ES-маркера `SmartCon_FamilyVersion_v1` на системных типах (`ElementType`) проекта (Issue #104, ADR-061). Та же схема и payload, что у `IFamilyVersionStore`; отличается только носитель маркера. Реализация общая — `RevitFamilyVersionStore` реализует оба интерфейса (один singleton); inline-запись внутри транзакции синхронизации — через `WriteEntityToElement`.

**Файл:** `ISystemTypeVersionStore.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyVersionStore.cs`

```csharp
public interface ISystemTypeVersionStore
{
    FamilyVersion? ReadFromType(Document doc, ElementId typeId);
    void WriteToType(Document doc, ElementId typeId, FamilyVersion version);
    IReadOnlyDictionary<ElementId, FamilyVersion?> ReadManyFromTypes(
        Document doc, IEnumerable<ElementId> typeIds);
}
```
