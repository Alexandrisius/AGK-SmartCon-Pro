---
module: family-manager
---
# Интерфейсы FamilyManager — Зависимости (ADR-066)

> Загружать: при работе с routing-фитингами, shared nested families, `family_dependencies`.
> Источник истины: `src/SmartCon.Core/Services/Interfaces/IFamilyDependency*.cs`.

## IFamilyDependencyRepository

Персистентность связей parent→child (`family_dependencies`, V29, ADR-066).
Заполняется batch-import pipeline'ом (E1 — routing-фитинги), читается
sync-путём (`IFittingDependencyResolver`) для резолва правила трассировки
по связи вместо хрупкого поиска по имени. Реализация —
`LocalFamilyDependencyRepository` (DELETE+INSERT per parent_version_id,
дедуп в C# — SQLite NOCASE не складывает кириллицу).

**Файл:** `Services/Interfaces/IFamilyDependencyRepository.cs`

```csharp
public interface IFamilyDependencyRepository
{
    Task ReplaceForVersionAsync(
        string parentCatalogItemId,
        string parentVersionId,
        IReadOnlyList<FamilyDependencyInfo> dependencies,
        CancellationToken ct = default);

    Task<int> ReplaceForCurrentVersionAsync(
        string parentCatalogItemId,
        IReadOnlyList<FamilyDependencyInfo> dependencies,
        CancellationToken ct = default);

    Task<IReadOnlyList<FamilyDependencyInfo>> GetForCurrentVersionAsync(
        string parentCatalogItemId,
        CancellationToken ct = default);

    // E5 (#213, ADR-067): reverse-запросы для dependency guard'а и
    // скрепки в дереве — по ВСЕМ версиям родителей (архивная блокирует
    // удаление наравне с активной). Несколько связей одной версии
    // (разные kind/parts) схлопываются в одну reference.
    Task<IReadOnlyList<FamilyDependencyReference>> GetReferencingParentsAsync(
        string childCatalogItemId,
        CancellationToken ct = default);

    // Batch-вариант: один запрос на всё дерево; в словаре — только
    // дети, имеющие хотя бы одну входящую ссылку.
    Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyDependencyReference>>> GetReferencingParentsBatchAsync(
        IReadOnlyCollection<string> childCatalogItemIds,
        CancellationToken ct = default);

    // E2 (#209, V30): drift-детект — связи CURRENT-версии родителей, чья
    // зашитая версия ребёнка (child_version_label) СТРОГО СТАРЕЕ активной
    // (направленно, числовое сравнение «vN»; «зашита новее» — не дефект).
    // NULL-метки (legacy V29) не дрейфуют; только loadable-родители
    // (системные резолвят детей динамически на активной версии).
    // В словаре — только родители с хотя бы одной drifted-связью.
    // Читается amber-бейджем дерева и жёстким блоком загрузки.
    Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyDependencyDrift>>> GetDependencyDriftBatchAsync(
        IReadOnlyCollection<string> parentCatalogItemIds,
        CancellationToken ct = default);
}
```

---

## IFamilyDependencyCollector

Обнаружение семейств-зависимостей на Phase-1 prepare (ADR-066, EPIC #207).
E1 — routing-зависимости: фитинги из RoutingPreferenceManager-правил
системных MEPCurve-типов. Коллектор резолвит часть правила в ЖИВОЕ семейство
и возвращает его identity (`FamilyUniqueId`) — downstream никогда не ключует
по display-имени (I-05, #183). Реализация — `RevitFamilyDependencyCollector`
(SmartCon.Revit): индекс FamilySymbol по токену `"Family:Type"` без
строкового сплита (двоеточие внутри имени не ломает lookup), skip+
Warn для `IsEditable == false` / in-place.

**Файл:** `Services/Interfaces/IFamilyDependencyCollector.cs`

```csharp
public interface IFamilyDependencyCollector
{
    IReadOnlyList<FamilyDependencyDescriptor> CollectRoutingDependencies(
        Document document,
        SystemFamilySnapshot snapshot);

    // E2 (#209): shared nested семейства loadable-родителя. Скан ПЛОСКИЙ
    // (probe P1, SharedNestedCollectorTests): все уровни вложенности видны
    // в family-документе верхнего родителя — без рекурсии и cycle-guard.
    // FamilyUniqueId валиден только в переданном family-документе.
    IReadOnlyList<FamilyDependencyDescriptor> CollectSharedNestedDependencies(
        Document familyDocument);
}
```
