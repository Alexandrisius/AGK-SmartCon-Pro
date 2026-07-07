---
module: family-manager-loadable
---
# Loadable Family Import модели (Phase 22)

> Загружать: при работе с импортом loadable families из активного проекта.
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

## LoadableFamilyInfo

Уникальное загружаемое семейство, размещённое в активном проекте.
Возвращается из `ILoadableFamilyScanner.GetUniqueFamilies(Document)`.
**Без `ElementId`** — только стабильные строковые идентификаторы (I-05).

**Файл:** `LoadableFamilyInfo.cs`

```csharp
public sealed record LoadableFamilyInfo(
    string FamilyName,
    string FamilyUniqueId,
    string CategoryName,
    int TypeCount);
```

---

## LoadableFamilyAttributeTask

Задача для `IFamilyDataExtractionService.Extract` после успешного
импорта loadable в managed storage.

**Файл:** `ILoadableFamilyImportOrchestrator.cs`

```csharp
public sealed record LoadableFamilyAttributeTask(
    string CatalogItemId,
    string ManagedRfaPath,
    string? VersionId,
    string? FileId,
    bool HasTypeCatalog);
```

---

## LoadableFamilyImportResult

Результат `ILoadableFamilyImportOrchestrator.ImportAndPersistTypesAsync`.

**Файл:** `ILoadableFamilyImportOrchestrator.cs`

```csharp
public sealed record LoadableFamilyImportResult(
    bool Success,
    string? Message,
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<LoadableFamilyAttributeTask> AttributeTasks);
```
