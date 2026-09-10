---
module: family-manager
---
# Модели FamilyManager — Автоназначение категории (#241, ADR-070)

> Модуль автокатегоризации при batch-импорте. Индекс моделей: [README.md](README.md).
> Исходные модели: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

Семейство автоматически получает категорию каталога, если выполнены все
условия хотя бы одной группы правил автоназначения (OR из AND-групп).
Условия строятся по атрибутам библиотеки (матчинг по имени параметра в
снапшоте импорта) и системным полям (категория Revit по ordinal, Part Type
по `FamilyFact.ValueKey`, имя семейства). Правила не зависят от
binding'ов категории — удаление привязки атрибута их не ломает.

## AssignmentRuleGroup

OR-группа правил автоназначения, привязанная к категории каталога. Семейство
матчится с категорией, если хотя бы одна включённая группа имеет все
включённые условия выполненными. Группа без условий не матчит ничего.

**Файл:** `Models/FamilyManager/AssignmentRuleGroup.cs`

```csharp
public sealed record AssignmentRuleGroup(
    string Id,
    string CategoryId,
    int SortOrder,
    bool IsEnabled,
    IReadOnlyList<AssignmentCondition> Conditions);
```

## AssignmentCondition

Одно AND-условие внутри группы. Ровно одно из `AttributeId`/`SystemField`
заполнено согласно `SourceKind` (гарантируется CHECK-констрейнтом таблицы
`category_assignment_conditions`). Числовые значения — в display-единицах
(как в `ValidationRule`); ordinal-значения (категория Revit, Part Type)
хранятся в `ValueText` инвариантными строками.

**Файл:** `Models/FamilyManager/AssignmentCondition.cs`

```csharp
public sealed record AssignmentCondition(
    string Id,
    string GroupId,
    AssignmentConditionSourceKind SourceKind,
    string? AttributeId,
    AssignmentSystemField? SystemField,
    ValidationRuleOperator Operator,
    string? ValueText,
    double? ValueNumber,
    double? MinValue,
    double? MaxValue,
    int SortOrder,
    bool IsEnabled);
```

## AssignmentConditionSourceKind

Вид источника условия: `Attribute` (атрибут библиотеки) или `System`
(системное поле).

**Файл:** `Models/FamilyManager/AssignmentConditionSourceKind.cs`

```csharp
public enum AssignmentConditionSourceKind { Attribute = 0, System = 1 }
```

## AssignmentSystemField

Системное поле условия (locale-invariant сравнение):

```csharp
public enum AssignmentSystemField
{
    RevitCategory = 0,    // BuiltInCategory ordinal
    PartType = 1,         // FamilyFact.ValueKey (enum ordinal строкой)
    FamilyName = 2,       // имя айтема/строки импорта
    SystemFamilyKey = 3,  // locale-invariant FamilyKey (ADR-064); только движок, вне UI-редактора
}
```

**Файл:** `Models/FamilyManager/AssignmentSystemField.cs`

## CategoryAutoAssignEngine

Pure Core реализация движка матчинга (`Services/Implementation/
CategoryAutoAssignEngine.cs`): OR между группами, AND внутри группы,
атрибутные условия делегируются `FamilyValidationEngine`, системные —
собственный evaluator по ordinal/инвариантным ключам. См. интерфейс
[ICategoryAutoAssignEngine](../../interfaces/family-manager/assignment-rules.md).

## AssignmentOperatorPolicy

Whitelist операторов для условий автоназначения — уже, чем у валидации:
негативные операторы (`NotEquals`, `NotContains`, `IsEmpty`) запрещены для
атрибутных условий, потому что отсутствующий параметр удовлетворяет им в
движке → ложные назначения. Для системных полей разрешены `Equals`/
`NotEquals` (+ `Contains` для текстовых). Реализовано defense-in-depth:
редактор фильтрует комбобокс, репозиторий скипает при чтении, движок
считает недопустимое условие невыполненным.

**Файл:** `Models/FamilyManager/AssignmentOperatorPolicy.cs`

## CategoryAutoAssignInput

Нормализованный вход движка: `FamilyValidationInput` (значения параметров
типов из снапшота), словарь `attribute_id → имя`, системные значения
(ordinal категории, facts, имя семейства, FamilyKey), глубины категорий
каталога (`CategoryDepthsById` — число сегментов пути; для tie-break).

**Файл:** `Models/FamilyManager/CategoryAutoAssignInput.cs`

## CategoryAutoAssignResult

Tri-state результат: `NoMatch` / `Matched(categoryId)` /
`Ambiguous(candidateIds)`. Специфичность (#241): при нескольких
подошедших категориях побеждает самая глубокая в дереве — правило
родителя остаётся fallback'ом; `Ambiguous` — только ничья между
соседними категориями равной глубины (в кандидатах — только категории
максимальной глубины), которую разруливает пользователь через
рекомендательный пикер (иконка ⚠ в колонке «Категория» batch-диалога).
Без глубин (прямые вызовы движка в тестах) любое 2+ совпадение —
`Ambiguous`.

**Файл:** `Models/FamilyManager/CategoryAutoAssignResult.cs`

## CategoryAutoAssignPreloaded

Иммутабельный снапшот конфигурации правил на сессию batch-диалога:
включённые группы всех категорий + словари `attribute_id → имя` и
`category_id → путь`. Грузится один раз на диалог
(`ICategoryAutoAssignService.PreloadAsync`), оценка каждой строки —
синхронная.

**Файл:** `Models/FamilyManager/CategoryAutoAssignPreloaded.cs`

## RevitCategoryLabel

Одна выбираемая категория Revit для редактора условий: ordinal
(инвариантное хранимое значение) + локализованный label
(`LabelUtils.GetLabelFor` в языке сессии). Кураторский список MEP-категорий
— `RevitCategoryLabelService` (SmartCon.Revit). Ограничение: семья вне
кураторского списка никогда не сматчит правило по RevitCategory — для
таких семей используйте PartType / FamilyName / атрибутные условия.

**Файл:** `Models/FamilyManager/RevitCategoryLabel.cs`

## CategoryProvenance.AutoRule

Новый член enum (#241): категория назначена правилом автоназначения.
Автоматический provenance — re-derived при переименовании строки;
ручной выбор (Manual/Command) его блокирует. Tooltip ячейки категории:
«По правилу автоназначения».

**Файл:** `Models/FamilyManager/CategoryProvenance.cs`

## Batch-диалог: рекомендация и конфликт-иконка
`FamilyBatchImportRow` несёт `RecommendedCategoryIds/Paths` (рекомендация
правил для ЛЮБОЙ строки — New, Existing, Duplicate) и computed
`ShowRuleConflictIcon` — иконка ⚠ в колонке «Категория», пока текущая
категория не входит в рекомендованные. Клик по иконке открывает
`CategoryPickerViewModel` в режиме рекомендаций (только подходящие
категории + подзаголовок). Existing/Duplicate автоматически не
переназначаются — несовпадение только подсвечивается.

**Файлы:** `SmartCon.FamilyManager/ViewModels/FamilyBatchImportRow.cs`,
`CategoryPickerViewModel.cs`

## Редактор: «Взять условия родителя»

Кнопка в редакторе правил подкатегории (#241): правила ближайшего
предка с настроенными правилами копируются в редактор как НОВЫЕ
независимые строки (id null → при сохранении создаются заново).
Источник резолвится в дереве (`FindNearestAncestorWithRules` — подъём
по Parent до первого узла с `AssignmentRuleCount > 0`). Чисто
редакторский сахар: наследования правил нет, скопированное никак не
связано с родителем в рантайме.

**Файлы:** `AssignmentRulesEditorViewModel.cs` (CopyParentRules),
`CategoryTreeEditorViewModel.TreeOps.cs` (FindNearestAncestorWithRules)
