---
module: family-manager
---
# Модели FamilyManager — Атрибуты, дерево и пресеты

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

## AttributeScope

Область применения атрибута (тип/экземпляр).

**Файл:** `AttributeScope.cs`

```csharp
public enum AttributeScope
{
    Type,
    Instance
}
```

---

## AttributeDefinition

Определение атрибута (параметра) для извлечения из семейств. Заменяет устаревший `AttributePreset` (ADR-017).

**Файл:** `AttributeDefinition.cs`

```csharp
public sealed record AttributeDefinition(
    string Id,
    string Name,
    string? Group,
    bool IsActive,
    DateTimeOffset CreatedAtUtc);
```

---

## CategoryAttributeBinding

Связь категории с атрибутом — какие параметры извлекать для семейств данной категории.

**Файл:** `CategoryAttributeBinding.cs`

```csharp
public sealed record CategoryAttributeBinding(
    string Id,
    string CategoryId,
    string AttributeId,
    int SortOrder,
    bool IsEnabled);
```

---

## EffectiveCategoryAttribute

Эффективный атрибут категории с учётом наследования от родительских категорий.

**Файл:** `EffectiveCategoryAttribute.cs`

```csharp
public sealed record EffectiveCategoryAttribute(
    string AttributeId,
    string Name,
    string? Group,
    int SortOrder,
    bool IsEnabled,
    bool IsInherited,
    string? SourceCategoryId);
```

---

## SharedParameterEntry

Одна запись параметра, распарсенная из файла общих параметров Revit (ФОП, .txt).
Чистый data carrier — парсер живёт в Core и не трогает Revit API.
Группа ФОП переносится только как отображаемое имя (`GroupName`) и не импортируется
в пользовательские группы атрибутов.

**Файл:** `SharedParameterEntry.cs`

```csharp
public sealed record SharedParameterEntry(
    Guid ParameterGuid,
    string Name,
    string DataType,
    string? DataCategory,
    string? GroupName,
    string? Description);
```

---

## FamilyManagerUserSettings

Пользовательские настройки FamilyManager уровня машины (не привязаны к конкретной базе).
Хранятся в `%APPDATA%\SmartCon\FamilyManager\user-settings.json`. Сейчас — кэш пути к ФОП.

**Файл:** `FamilyManagerUserSettings.cs`

```csharp
public sealed record FamilyManagerUserSettings(string? SharedParametersFilePath);
```

---

## SharedParameterFileParser

Реализация `ISharedParameterFileParser` — pure C# парсер ФОП (.txt): tab-delimited строки,
порядок колонок из заголовков `*GROUP`/`*PARAM` с фиксированным fallback,
BOM-детекция кодировки (UTF-8/UTF-16 LE/BE). Бросает `InvalidDataException`,
если секция `*PARAM` отсутствует.

**Файл:** `SmartCon.Core/Services/Implementation/SharedParameterFileParser.cs`

---

## JsonFamilyManagerUserSettingsRepository

JSON-репозиторий для `FamilyManagerUserSettings`. Файл: `%APPDATA%\SmartCon\FamilyManager\user-settings.json`.

**Файл:** `SmartCon.Core/Services/Implementation/JsonFamilyManagerUserSettingsRepository.cs`

---

## CategoryNode

Узел дерева категорий. Формирует иерархию категорий каталога семейств.

**Файл:** `CategoryNode.cs`

```csharp
public sealed record CategoryNode(
    string Id,
    string Name,
    string? ParentId,
    int SortOrder,
    string FullPath,
    DateTimeOffset CreatedAtUtc);
```

---

## CategoryTree

Иммутабельное дерево категорий с быстрым поиском по ID и дочерним узлам.

**Файл:** `CategoryTree.cs`

```csharp
public sealed class CategoryTree
{
    public CategoryTree(IReadOnlyList<CategoryNode> nodes);
    public IReadOnlyList<CategoryNode> GetAllNodes();
    public CategoryNode? GetById(string id);
    public IReadOnlyList<CategoryNode> GetChildren(string? parentId);
    public IReadOnlyList<CategoryNode> GetRootNodes();
    public string GetFullPath(string id);
    public IReadOnlyList<string> GetDescendantIds(string categoryId);
    public string BuildFullPath(string id);
}
```

---

## AttributePreset

Набор параметров для извлечения из семейств, привязанный к категории. Дочерние категории наследуют параметры от родительских.

**Файл:** `AttributePreset.cs`

```csharp
public sealed record AttributePreset(
    string Id,
    string? CategoryId,
    IReadOnlyList<AttributePresetParameter> Parameters,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
```

---

## AttributePresetParameter

Описание одного параметра в пресете.

**Файл:** `AttributePresetParameter.cs`

```csharp
public sealed record AttributePresetParameter(
    string ParameterName,
    string? DisplayName,
    int SortOrder)
{
    public string DisplayText => DisplayName ?? ParameterName;
}
```

---

## AttributeValueStatus

Статус извлечённого значения.

**Файл:** `AttributeValueStatus.cs`

```csharp
public enum AttributeValueStatus
{
    Valid,
    Missing,
    Error
}
```

---

## ExtractedAttributeValue

Извлечённое значение атрибута из семейства.

**Файл:** `ExtractedAttributeValue.cs`

```csharp
public sealed record ExtractedAttributeValue(
    string Id,
    string CatalogItemId,
    string? VersionId,
    string AttributeId,
    string? TypeId,
    string? ValueText,
    AttributeValueStatus Status,
    DateTimeOffset ExtractedAtUtc);
```

---

## AttributeFilterCondition

Одно условие расширенного поиска FamilyManager (#87): источник (атрибут библиотеки или системное поле — словарь #241) + оператор + значение. Переиспользует `ValidationRuleOperator` и `AssignmentSystemField`. Условия объединяются по «И» и на уровне SQL (`LocalCatalogQueryBuilder`): атрибуты — EXISTS по `extracted_attribute_values` активной версии; системные поля — `ci.name` / `ci.revit_category_id` / факт `part_type` из `family_facts`.

**Файл:** `AttributeFilterCondition.cs`

```csharp
public sealed record AttributeFilterCondition(
    AssignmentConditionSourceKind SourceKind,
    string? AttributeId,
    string? AttributeName,
    AssignmentSystemField? SystemField,
    ValidationRuleOperator Operator,
    string? Value)
{
    public static readonly IReadOnlyList<ValidationRuleOperator> AttributeOperators;    // Equals/NotEquals/Contains/NotContains/HasValue/IsEmpty
    public static readonly IReadOnlyList<ValidationRuleOperator> SystemTextOperators;   // Equals/NotEquals/Contains/NotContains
    public static readonly IReadOnlyList<ValidationRuleOperator> SystemOrdinalOperators; // Equals/NotEquals/HasValue/IsEmpty
    public static IReadOnlyList<ValidationRuleOperator> OperatorsFor(
        AssignmentConditionSourceKind sourceKind, AssignmentSystemField? systemField);
    public static bool RequiresValue(ValidationRuleOperator op);
}
```

---

## AdvancedSearchFilter

Снимок расширенного поиска (#87): категория-охват (поддерево каталога, null = все категории) + условия по атрибутам. Комбинируется с обычным поиском по имени по «И»; хранится только в памяти сессии и сбрасывается при смене активной БД каталога.

**Файл:** `AdvancedSearchFilter.cs`

```csharp
public sealed record AdvancedSearchFilter(
    string? CategoryId,
    IReadOnlyList<AttributeFilterCondition> Conditions)
{
    public static AdvancedSearchFilter Empty { get; }
    public bool IsEmpty { get; } // нет категории и нет условий
}
```
