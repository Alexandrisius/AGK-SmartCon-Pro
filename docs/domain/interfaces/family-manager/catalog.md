---
module: family-manager-interfaces
---
# Интерфейсы FamilyManager — Провайдеры каталога и поиск

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## IFamilyCatalogProvider

Чтение каталога семейств: поиск, получение версий и файлов. Все методы — async.

**Файл:** `IFamilyCatalogProvider.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogProvider.cs`

```csharp
public interface IFamilyCatalogProvider
{
    FamilyCatalogCapabilities GetCapabilities();
    Task<IReadOnlyList<FamilyCatalogItem>> SearchAsync(FamilyCatalogQuery query, CancellationToken ct = default);
    Task<FamilyCatalogItem?> GetItemAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<FamilyCatalogVersion>> GetVersionsAsync(string catalogItemId, CancellationToken ct = default);
    Task<FamilyCatalogVersion?> GetVersionByIdAsync(string catalogItemId, string versionId, CancellationToken ct = default);
    Task<FamilyCatalogVersion?> GetVersionByLabelAsync(string catalogItemId, string versionLabel, int targetRevitMajorVersion = 0, CancellationToken ct = default);
    Task<FamilyFileRecord?> GetFileAsync(string fileId, CancellationToken ct = default);
    Task<int> GetItemCountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<int>> GetAvailableRevitVersionsAsync(string catalogItemId, CancellationToken ct = default);
    Task<FamilyCatalogItem?> FindByNormalizedNameAsync(string normalizedName, CancellationToken ct = default);
    Task<ContentHashMatch?> FindByContentHashAcrossVersionsAsync(string hexHash, int hashFormatVersion, string familySource, CancellationToken ct = default);
    Task<IReadOnlyList<FamilyCatalogItem>> GetItemsBySourceAsync(string familySource, CancellationToken ct = default);
}
```

---

## IWritableFamilyCatalogProvider

Запись в каталог: импорт, обновление, удаление записей. Импорт копирует файлы в managed storage.

**Файл:** `IWritableFamilyCatalogProvider.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogProvider.cs`

```csharp
public interface IWritableFamilyCatalogProvider
{
    Task<FamilyImportResult> ImportAsync(FamilyImportRequest request, CancellationToken ct = default);
    Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default);
    Task<FamilyCatalogItem> UpdateItemAsync(string id, string? name, string? description, string? category, IReadOnlyList<string>? tags, ContentStatus? status, string? manufacturer = null, CancellationToken ct = default);
    Task<bool> DeleteItemAsync(string id, CancellationToken ct = default);
    Task<SetActiveVersionResult> SetActiveVersionAsync(string catalogItemId, string versionLabel, CancellationToken ct = default);
    Task<DeleteVersionResult> DeleteVersionAsync(string catalogItemId, string versionLabel, CancellationToken ct = default);
}
```

---

## IFamilySearchService

Поиск семейств и типов в активном документе Revit. Все операции выполняются в контексте ExternalEvent (I-01).

**Файл:** `IFamilySearchService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilySearchService.cs`

```csharp
public interface IFamilySearchService
{
    bool IsFamilyLoaded(string familyName);
    IReadOnlyList<string> GetFamilyTypeNames(string familyName);
    bool HasFamilyType(string familyName, string typeName);
}
```

---

## IFamilyMetadataExtractionService

Извлечение метаданных из `.rfa`. MVP: метаданные файлового уровня (имя, размер, хеш, timestamps). Post-MVP: глубокое извлечение через Revit API.

**Файл:** `IFamilyMetadataExtractionService.cs`

```csharp
public interface IFamilyMetadataExtractionService
{
    Task<FamilyMetadataExtractionResult> ExtractAsync(string filePath, CancellationToken ct = default);
}
```

---

## IRevitFileInfoReader

Чтение информации о версии Revit из `.rvt` и `.rfa` файлов (без загрузки в проект).

**Файл:** `IRevitFileInfoReader.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFileInfoReader.cs`

```csharp
public interface IRevitFileInfoReader
{
    int? ReadRevitVersion(string filePath);
}
```

---

## IFamilyDataExtractionService

Извлечение данных (параметров) из `.rfa` файла по заданному списку имён параметров. Возвращает значения по типоразмерам.

**Файл:** `IFamilyDataExtractionService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyDataExtractionService.cs`

```csharp
public sealed record FamilyExtractionTypeResult(string TypeName, int SortOrder);

public sealed record FamilyExtractionValueResult(
    string ParameterName,
    AttributeScope? ParameterScope,
    string? StorageType,
    string? ValueText,
    string? ValueRaw,
    double? ValueNumber,
    string? UnitTypeId,
    AttributeValueStatus Status,
    string? Message);

public sealed record FamilyExtractionTypeValues(
    string TypeName,
    int SortOrder,
    IReadOnlyList<FamilyExtractionValueResult> Values);

public sealed record FamilyExtractionResult(
    bool Success,
    IReadOnlyList<FamilyExtractionTypeValues> Types,
    IReadOnlyList<FamilyExtractionValueResult>? UntypedValues,
    string? ErrorMessage,
    int RevitMajorVersion,
    IReadOnlyList<string>? SharedNestedFamilyNames = null)
{
    /// <summary>
    /// Non-null accessor for SharedNestedFamilyNames. Returns Array.Empty&lt;string&gt;()
    /// when the field is null (legacy callers, test fixtures). Production paths
    /// (RevitFamilyDataExtractionService.ExtractFromManagedFile) always populate
    /// the field — see ADR-034 §2.
    /// </summary>
    public IReadOnlyList<string> SharedNestedFamilyNamesSafe =>
        SharedNestedFamilyNames ?? Array.Empty<string>();
}

public interface IFamilyDataExtractionService
{
    FamilyExtractionResult Extract(string rfaFilePath, IReadOnlyList<string> expectedParameterNames);
    FamilyExtractionResult Extract(Autodesk.Revit.DB.Document familyDocument, IReadOnlyList<string> expectedParameterNames);

    /// <summary>
    /// Single entry point for all managed-storage import paths. Opens the
    /// .rfa via Revit API and reads already-baked type data. Type Catalog
    /// simulation (ADR-032) is replaced by bake-in during import (ADR-033).
    /// Must be called on the Revit UI thread.
    ///
    /// ADR-034 §2 (V3): the same open-close cycle also collects the names
    /// of shared nested families declared by the parent family and populates
    /// <c>result.SharedNestedFamilyNames</c>. This avoids a second
    /// <c>OpenDocumentFile</c> per .rfa that the V2 implementation did (which
    /// doubled the MFC family-upgrade dialog count and violated the project
    /// rule "open the family once").
    /// </summary>
    FamilyExtractionResult ExtractFromManagedFile(
        string managedRfaPath,
        IReadOnlyList<string> expectedParameterNames,
        CancellationToken ct = default);
}
```

---

## IFamilyFinder

Поиск загруженного `Family` элемента в активном проекте по имени (ADR-030 Phase 24, I-09 compliance). Вынесен из VM в Core/Revit, чтобы `FamilyManagerMainViewModel` не зависел от `Autodesk.Revit.DB`. Реализация — `RevitFamilyFinder` в `SmartCon.Revit/FamilyManager/` использует `FilteredElementCollector.OfClass(Family)`.

**Файл:** `IFamilyFinder.cs`

```csharp
public interface IFamilyFinder
{
    ElementId? FindByName(Document doc, string familyName);
}
```

**Threading:** вызывается только на Revit main thread (I-01). VM оборачивает в `_awaitableEvent.RaiseAsync`.
