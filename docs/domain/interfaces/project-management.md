---
module: project-management-interfaces
---
# Интерфейсы ProjectManagement

> Загружать: при работе с Share Project / ISO 19650.
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## IShareProjectSettingsRepository

CRUD для настроек модуля ShareProject. Хранение — per-project: `DataStorage` в
`ExtensibleStorage` активного `Document` (ADR-013).

**Файл:** `SmartCon.Core/Services/Interfaces/IShareProjectSettingsRepository.cs`
**Реализация:** `SmartCon.Revit/Storage/RevitShareProjectSettingsRepository.cs`
**Сериализация:** `SmartCon.Core/Services/Storage/ShareSettingsJsonSerializer.cs` (pure C#)

```csharp
public interface IShareProjectSettingsRepository
{
    ShareProjectSettings Load();
    void Save(ShareProjectSettings settings);
    string ExportToJson(ShareProjectSettings settings);
    ShareProjectSettings ImportFromJson(string json);
}
```

---

## IModelPurgeService

Очистка модели: удаление элементов по категориям + purge неиспользуемых.

**Файл:** `SmartCon.Core/Services/Interfaces/IModelPurgeService.cs`
**Реализация:** `SmartCon.Revit/Sharing/RevitModelPurgeService.cs`

```csharp
public interface IModelPurgeService
{
    int Purge(Document doc, PurgeOptions options, List<string> keepViewNames);
}
```

---

## IFileNameParser

Парсинг имени файла по шаблону, трансформация статуса, валидация.

**Файл:** `SmartCon.Core/Services/Interfaces/IFileNameParser.cs`
**Реализация:** `SmartCon.Revit/Sharing/RevitFileNameParser.cs`

```csharp
public interface IFileNameParser
{
    string? TransformForExport(string fileName, FileNameTemplate template, List<FieldDefinition> fieldLibrary);
    (bool IsValid, string ErrorMessage) Validate(string fileName, FileNameTemplate template, List<FieldDefinition> fieldLibrary);
    ValidationResult ValidateDetailed(string fileName, FileNameTemplate template, List<FieldDefinition> fieldLibrary);
    Dictionary<string, string> ParseBlocks(string fileName, FileNameTemplate template);
}
```

---

## IViewRepository

Получение списка видов из Revit документа для отображения в UI.

**Файл:** `SmartCon.Core/Services/Interfaces/IViewRepository.cs`
**Реализация:** `SmartCon.Revit/Sharing/RevitViewRepository.cs`

```csharp
public interface IViewRepository
{
    List<ViewInfo> GetAllViews(Document doc);
}
```
