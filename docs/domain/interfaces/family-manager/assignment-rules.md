---
module: family-manager
---
# Интерфейсы FamilyManager — Автоназначение категории (#241, ADR-070)

> Модуль автокатегоризации при batch-импорте. Индекс интерфейсов: [README.md](README.md).
> Исходные интерфейсы: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## ICategoryAutoAssignEngine

Pure Core движок матчинга: вход — `CategoryAutoAssignInput` + все группы
правил, выход — tri-state `CategoryAutoAssignResult`. Атрибутные условия
делегируются `IFamilyValidationEngine` (идентичная семантика операторов,
display-units-first, all-types AND); системные условия вычисляются по
locale-invariant ordinal/invariant-key сравнению. Никакого Revit API.

Реализация: `CategoryAutoAssignEngine` (SmartCon.Core).

**Файл:** `Services/Interfaces/ICategoryAutoAssignEngine.cs`

```csharp
public interface ICategoryAutoAssignEngine
{
    CategoryAutoAssignResult Evaluate(
        CategoryAutoAssignInput input,
        IReadOnlyList<AssignmentRuleGroup> groups);
}
```

## ICategoryAutoAssignService

Оркестрация на стороне FamilyManager: `PreloadAsync` загружает правила +
атрибуты + пути категорий один раз на диалог; `Evaluate` синхронно оценивает
снапшот семьи (маппинг через `SnapshotValidationMapper`, семейное имя можно
переопределить для переименованных строк).

Реализация: `CategoryAutoAssignService` (SmartCon.FamilyManager, internal).

**Файл:** `Services/Interfaces/ICategoryAutoAssignService.cs`

```csharp
public interface ICategoryAutoAssignService
{
    Task<CategoryAutoAssignPreloaded> PreloadAsync(CancellationToken ct = default);
    CategoryAutoAssignResult Evaluate(
        CategoryAutoAssignPreloaded preloaded,
        FamilySnapshot? loadableSnapshot,
        SystemFamilySnapshot? systemSnapshot,
        string? familyNameOverride = null);
}
```

## IAssignmentRuleRepository

Персистентность правил автоназначения в catalog.db (таблицы
`category_assignment_rule_groups` + `category_assignment_conditions`,
schema v31, FK CASCADE на categories/attribute_definitions, CHECK на
взаимоисключаемость attribute/system). Неизвестные operator/kind/field при
чтении скипаются с Warn (forward-compat). `UpdateGroupAsync` — частичное
обновление (null = не трогать); `UpdateConditionAsync` — full-shape.

Реализация: `LocalAssignmentRuleRepository` (SmartCon.FamilyManager, I-14:
SQLite только через `LocalCatalogDatabase`).

**Файл:** `Services/Interfaces/IAssignmentRuleRepository.cs`

```csharp
public interface IAssignmentRuleRepository
{
    Task<IReadOnlyList<AssignmentRuleGroup>> GetGroupsWithConditionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AssignmentRuleGroup>> GetGroupsForCategoryAsync(string categoryId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> GetEnabledGroupCountsAsync(CancellationToken ct = default);
    Task<AssignmentRuleGroup> CreateGroupAsync(string categoryId, CancellationToken ct = default);
    Task<bool> UpdateGroupAsync(string groupId, int? sortOrder, bool? isEnabled, CancellationToken ct = default);
    Task<bool> DeleteGroupAsync(string groupId, CancellationToken ct = default);
    Task<AssignmentCondition> CreateConditionAsync(...);
    Task<bool> UpdateConditionAsync(AssignmentCondition condition, CancellationToken ct = default);
    Task<bool> DeleteConditionAsync(string conditionId, CancellationToken ct = default);
}
```

## IRevitCategoryLabelService

Кураторский список выбираемых модельных категорий Revit с локализованными
лейблами для редактора условий. Реализация в SmartCon.Revit
(`RevitCategoryLabelService`): `LabelUtils.GetLabelFor(BuiltInCategory)`
через cached reflection — перегрузка существует только с Revit 2020, на
рантайме Revit 2019 (R19) прямой вызов убил бы JIT, поэтому enum-name
fallback (паттерн #153, multi-version-guide).

**Файл:** `Services/Interfaces/IRevitCategoryLabelService.cs`

```csharp
public interface IRevitCategoryLabelService
{
    IReadOnlyList<RevitCategoryLabel> GetModelCategories();
}
```

## Точки интеграции в batch-диалог

`FamilyBatchImportViewModel` принимает `ICategoryAutoAssignService?`
(nullable — обратная совместимость тест-фикстур; production передаёт всегда).
`RunInitialGateAsync` = начальная гейт-валидация → preload правил →
автоназначение eligible-строк на UI-потоке → повторная гейт-валидация
назначенных строк. Все три продакшн-точки конструирования диалога
(файлы, активный файл, выделенные элементы) передают сервис.
