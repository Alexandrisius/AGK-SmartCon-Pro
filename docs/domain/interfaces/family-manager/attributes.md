---
module: family-manager-interfaces
---
# Интерфейсы FamilyManager — Атрибуты и репозитории

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## IFamilyTypeRepository

Хранение и чтение типоразмеров семейств (FamilyTypeDescriptor) для каталога.

**Файл:** `IFamilyTypeRepository.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyTypeRepository.cs`

```csharp
public interface IFamilyTypeRepository
{
    Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(string catalogItemId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(IEnumerable<string> catalogItemIds, CancellationToken ct = default);
    Task SaveTypesAsync(string catalogItemId, IReadOnlyList<FamilyTypeDescriptor> types, CancellationToken ct = default);
    Task<bool> HasTypesAsync(string catalogItemId, CancellationToken ct = default);
}
```

---

## IFamilyFactRepository (ADR-055)

Чтение подсистемы family facts для окна свойств: ординал Revit-категории итема (`catalog_items.revit_category_id`) + извлечённые факты (`family_facts`, schema V22) одним вызовом. Запись идёт через import-пути (`LocalFamilyImportService`) и задачу `family-facts-v1` — репозиторий читает.

**Файл:** `IFamilyFactRepository.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyFactRepository.cs`

```csharp
public interface IFamilyFactRepository
{
    Task<FamilyFactsData> GetForItemAsync(string catalogItemId, CancellationToken ct = default);
}
```

---

## ICategoryRepository

CRUD для дерева категорий каталога семейств.

**Файл:** `ICategoryRepository.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalCategoryRepository.cs`

---

## IAttributeDefinitionRepository

CRUD для определений атрибутов (AttributeDefinition). Заменяет устаревший IAttributePresetRepository.

**Файл:** `IAttributeDefinitionRepository.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalAttributeDefinitionRepository.cs`

```csharp
public interface IAttributeDefinitionRepository
{
    Task<IReadOnlyList<AttributeDefinition>> GetAllAsync(CancellationToken ct = default);
    Task<AttributeDefinition?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<AttributeDefinition?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<AttributeDefinition> CreateAsync(string name, string? group, CancellationToken ct = default);
    Task<AttributeDefinition> UpdateAsync(string id, string? name, string? group, bool? isActive, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, string? excludeId, CancellationToken ct = default);
}
```

---

## ICategoryAttributeBindingService

Управление связями категорий с атрибутами. Поддерживает эффективные (с учётом наследования) и прямые привязки.

**Файл:** `ICategoryAttributeBindingService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalCategoryAttributeBindingService.cs`

```csharp
public interface ICategoryAttributeBindingService
{
    Task<IReadOnlyList<CategoryAttributeBinding>> GetBindingsForCategoryAsync(string categoryId, CancellationToken ct = default);
    Task<IReadOnlyList<EffectiveCategoryAttribute>> GetEffectiveAttributesAsync(string? categoryId, CancellationToken ct = default);
    Task<CategoryAttributeBinding> CreateBindingAsync(string categoryId, string attributeId, int sortOrder, CancellationToken ct = default);
    Task<bool> DeleteBindingAsync(string bindingId, CancellationToken ct = default);
    Task<CategoryAttributeBinding> UpdateBindingAsync(string bindingId, int? sortOrder, bool? isEnabled, CancellationToken ct = default);
    Task<IReadOnlyList<CategoryAttributeBinding>> GetDirectBindingsAsync(string categoryId, CancellationToken ct = default);
    Task<IReadOnlyList<CategoryAttributeBinding>> GetBindingsForAttributeAsync(string attributeId, CancellationToken ct = default);
    Task DeleteBindingsForAttributeAsync(string attributeId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> GetBindingCountsAsync(IEnumerable<string> attributeIds, CancellationToken ct = default);
}
```

---

## ISharedParameterFileParser

Парсер файла общих параметров Revit (ФОП, .txt) без Revit API. Tab-delimited формат,
секции `*META` / `*GROUP` / `*PARAM`; порядок колонок берётся из заголовков секций
с фиксированным fallback. Кодировка определяется по BOM (UTF-8/UTF-16) с fallback UTF-8.

**Файл:** `ISharedParameterFileParser.cs`
**Реализация:** `SmartCon.Core/Services/Implementation/SharedParameterFileParser.cs`

```csharp
public interface ISharedParameterFileParser
{
    IReadOnlyList<SharedParameterEntry> ParseFile(string filePath);
    IReadOnlyList<SharedParameterEntry> ParseContent(string content);
}
```

---

## IFamilyManagerUserSettingsRepository

Хранение пользовательских настроек FamilyManager уровня машины (JSON-файл).
Используется для кэширования пути к файлу общих параметров (ФОП).

**Файл:** `IFamilyManagerUserSettingsRepository.cs`
**Реализация:** `SmartCon.Core/Services/Implementation/JsonFamilyManagerUserSettingsRepository.cs`

```csharp
public interface IFamilyManagerUserSettingsRepository
{
    FamilyManagerUserSettings Load();
    void Save(FamilyManagerUserSettings settings);
}
```

---

## IAttributePresetService

Управление пресетами атрибутов, определяющими какие параметры извлекать для каждой категории. Поддерживает наследование категорий.

**Файл:** `IAttributePresetService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalAttributePresetService.cs`

```csharp
public interface IAttributePresetService
{
    Task<IReadOnlyList<AttributePreset>> GetAllPresetsAsync(CancellationToken ct = default);
    Task<AttributePreset?> GetPresetForCategoryAsync(string? categoryId, CancellationToken ct = default);
    Task<IReadOnlyList<AttributePresetParameter>> GetEffectiveParametersAsync(string? categoryId, CancellationToken ct = default);
    Task<AttributePreset> CreatePresetAsync(string? categoryId, IReadOnlyList<AttributePresetParameter> parameters, CancellationToken ct = default);
    Task UpdatePresetAsync(string presetId, IReadOnlyList<AttributePresetParameter> parameters, CancellationToken ct = default);
    Task DeletePresetAsync(string presetId, CancellationToken ct = default);
}
```

---

## IAttributeValueRepository

Хранение и чтение извлечённых значений атрибутов (AttributeValue) для элементов каталога, типоразмеров и запусков импорта.

**Файл:** `IAttributeValueRepository.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalAttributeValueRepository.cs`

```csharp
public interface IAttributeValueRepository
{
    Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForItemAsync(string catalogItemId, string? versionId, CancellationToken ct = default);
    Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForTypeAsync(string typeId, CancellationToken ct = default);
    Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForRunAsync(string runId, CancellationToken ct = default);
    Task SaveValuesAsync(IReadOnlyList<ExtractedAttributeValue> values, CancellationToken ct = default);
    Task ReplaceSnapshotAsync(string catalogItemId, string? versionId, string runId, IReadOnlyList<ExtractedAttributeValue> values, CancellationToken ct = default);
    Task<int> DeleteValuesForRunAsync(string runId, CancellationToken ct = default);
    Task<int> GetFoundCountAsync(string catalogItemId, string? versionId, CancellationToken ct = default);
    Task<int> GetMissingCountAsync(string catalogItemId, string? versionId, CancellationToken ct = default);
}
```
