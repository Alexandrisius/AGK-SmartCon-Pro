---
module: family-manager-interfaces
---
# Интерфейсы FamilyManager

> Загружать: при работе с модулем FamilyManager (каталог, импорт, атрибуты, плагин).
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

## IFamilyImportService

Оркестрация импорта семейств: запись в managed storage, запись в БД каталога. v2.0.0: SHA-256 dedup и `.txt` sidecar copy убраны. Метод `ImportBatchAsync` принимает уже подготовленные `FamilyBatchImportItem` (с реальным `FilePath` в managed storage или с placeholder + `Source` payload, который `ProcessProjectImportAsync` резолвит ДО передачи).

**Файл:** `IFamilyImportService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.cs`

```csharp
public interface IFamilyImportService
{
    Task<FamilyImportResult> ImportFileAsync(FamilyImportRequest request, CancellationToken ct = default);
    Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default);
    Task<FamilyImportResult> UpdateFamilyAsync(FamilyUpdateRequest request, CancellationToken ct = default);
    Task<FamilyBatchImportResult> ImportBatchAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        string? categoryId,
        IProgress<FamilyImportProgress>? progress,
        CancellationToken ct = default);
}
```

> **Архитектурное примечание (v2.0.0):** ранний план предлагал ввести отдельные методы `ImportActiveFamilyAsync(Document, ...)` и `ImportManagedFileAsync(string managedPath, ...)`. Реализация пошла по более простому пути: VM-слой сам делает `SaveAs(managedRfaPath)` в активном документе (UC-2) или `StageLoadableFamilyFromProject` / `CreateCleanProjectWithTypesAndInstances` (UC-3/UC-4), затем `item.FilePath` перезаписывается на managed-путь и orchestrator (`SystemFamilyImportOrchestrator` / `LoadableFamilyImportOrchestrator`) вызывает существующий `ImportBatchAsync`. Это сохраняет `IFamilyImportService` компактным (1 import-path для всех 4 use-case'ов) и убирает необходимость в `FamilyActiveImportRequest` record.

---

## IFamilyTypeCatalogBaker

Запекание Type Catalog (.txt) в .rfa при импорте (ADR-033). Реализация выполняет все Revit API вызовы на UI thread.

**Unit conversion (BAKE-006..009):** baker вызывает `RevitUnitsCompat.CatalogCellToInternalUnits(raw, annotation, param)` для каждой `StorageType.Double` колонки с `##TYPE##UNITS` annotation. Конвертация пропускается для: не-Double storage, dimensionless parameters, отсутствующей annotation (legacy behavior — raw value). На failure path (annotation не распознана) пишется `Warn` + skip parameter, агрегируется в `BakeStats.Failed` для Info summary. На success path значение конвертируется через `UnitUtils.ConvertToInternalUnits` после валидации `UnitUtils.IsValidUnit(targetSpec, sourceUnit)`. Pure normalization вынесен в `TypeCatalogUnitAlias.Normalize` (SmartCon.Core, fully unit-tested).

**Файл:** `IFamilyTypeCatalogBaker.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyTypeCatalogBaker.cs`

```csharp
public interface IFamilyTypeCatalogBaker
{
    Task<FamilyTypeCatalogBakingResult> BakeAsync(
        string sourceRfaPath,
        TypeCatalogParseResult catalog,
        string outputRfaPath,
        CancellationToken ct = default);

    Task<FamilyTypeCatalogBakingResult> BakeInExistingDocumentAsync(
        object familyDoc,
        TypeCatalogParseResult catalog,
        CancellationToken ct = default);
}
```

**Два метода:**
- `BakeAsync` — открывает .rfa по пути, bake, SaveAs, Close (Commit-фаза, ADR-033)
- `BakeInExistingDocumentAsync` (ADR-039) — bake в уже открытом family document
  (Prepare-фаза). `object familyDoc` — opaque Document (I-09). Без
  OpenDocumentFile/SaveAs/Close. Используется Phase 27B для bake в held-open
  документе до extraction snapshot.

**Зависимости:** `IFamilyManagerAwaitableEvent`, `ITransactionService`, `ITypeCatalogValueApplier`, `IFormulaSolver`. Все Revit API операции маршалятся через `IFamilyManagerAwaitableEvent` callback (I-01).

---

## IActiveDocumentClassifier

Определяет тип активного документа: `Family` / `Project` / `None`.
Используется командой «Импорт активного файла» для выбора code path.

**Файл:** `IActiveDocumentClassifier.cs`
**Реализация:** `SmartCon.FamilyManager/Services/ActiveDocumentClassifier.cs`

```csharp
public enum ActiveDocumentKind { None, Family, Project }

public interface IActiveDocumentClassifier
{
    Task<ActiveDocumentKind> ClassifyAsync(CancellationToken ct = default);
}
```

---

## IFamilyFileResolver

Разрешение путей к файлам семейств из managed storage. Выбирает лучший файл для целевой версии Revit.

**Файл:** `IFamilyFileResolver.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyFileResolver.cs`

```csharp
public interface IFamilyFileResolver
{
    Task<FamilyResolvedFile> ResolveForLoadAsync(string catalogItemId, int targetRevitVersion, CancellationToken ct = default);
    string? GetDatabaseRoot();
}
```

---

## IFamilyStorageRenameService

Переименование физических `.rfa` файлов в managed storage при изменении отображаемого имени семейства. Переименовывает только файлы **текущей версии** (`current_version_label`) во **всех подпапках Revit-версий** (`r24/`, `r25/`...). Исторические версии (`v1`, `v2`...) остаются нетронутыми. Обновляет `family_files.file_name` и `family_files.relative_path` в БД.

**Файл:** `IFamilyStorageRenameService.cs`  
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyStorageRenameService.cs`

```csharp
public interface IFamilyStorageRenameService
{
    Task RenameFamilyFilesAsync(string catalogItemId, string newName, CancellationToken ct = default);
}
```

---

## IFamilyAssetService

Управление вспомогательными ассетами (изображения, документы, lookup tables) семейств.
С ADR-047 (issue #131) также управляет производным файлом аватара `avatar.png` (560×420):
`SetPrimaryAssetAsync` инвалидирует его при смене primary, `GetAvatarImagePathAsync` —
единая цепочка разрешения (`avatar.png` → primary image) для аватарки и tooltip.

**Файл:** `IFamilyAssetService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyAssetService.cs`

```csharp
public interface IFamilyAssetService
{
    Task<FamilyAsset> AddAssetAsync(string catalogItemId, string? versionLabel, FamilyAssetType assetType, string sourceFilePath, string? description, CancellationToken ct = default);
    Task<IReadOnlyList<FamilyAsset>> GetAssetsAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default);
    Task<bool> DeleteAssetAsync(string assetId, CancellationToken ct = default);
    Task<string?> ResolveAssetPathAsync(string assetId, CancellationToken ct = default);
    Task SetPrimaryAssetAsync(string assetId, CancellationToken ct = default);
    Task<FamilyAsset?> GetPrimaryImageAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default);
    Task SetAssetVersionBindingAsync(string assetId, string? newVersionLabel, CancellationToken ct = default);
    Task SaveAvatarAsync(string catalogItemId, string sourcePngPath, CancellationToken ct = default);
    Task<string?> GetAvatarImagePathAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default);
    Task ClearAvatarAsync(string catalogItemId, CancellationToken ct = default);
}
```

---

## IAvatarCropService

Рендер единой миниатюры аватара (560×420 PNG, ADR-047) из исходного изображения любого
разрешения. Декод ограничен 4096px по ширине — защита памяти для очень больших файлов.

**Файл:** `IAvatarCropService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/WpfAvatarCropService.cs` (WPF imaging)

```csharp
public interface IAvatarCropService
{
    (int PixelWidth, int PixelHeight) GetImageDimensions(string path);
    void CropToPng(string sourcePath, ImageCropRect sourceRect, string outputPath);
}
```

---

## IFamilyLoadService

Загрузка семейства в проект Revit. **Не содержит `Document` в параметрах** — Document получается через `IRevitContext` в реализации.
**Вызывать только из ExternalEvent handler (I-01).**

С версии Issue #67 поддерживает опциональный callback `onSharedDecision` для интерактивного выбора режима загрузки общих вложенных семейств (shared nested). Если callback не передан, используется безопасный дефолт `UseProject` (back-compat).

С версии Issue #77 (ADR-034) добавлен опциональный параметр `nestedSharedNames` —
список имён shared nested, извлечённых при импорте в FM. Используется как fallback
для имени в диалоге, когда Revit API возвращает `null` (REVIT-198137 в Revit
2023 / Revit 2024 < 24.3.0.13). Если `null`/пусто, сервис пытается резолвить
через `ISharedNestedFamilyRepository.GetNamesForCurrentVersionAsync(catalogItemId)`.

**Файл:** `IFamilyLoadService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyLoadService.cs`

```csharp
public interface IFamilyLoadService
{
    Task<FamilyLoadResult> LoadFamilyAsync(
        FamilyResolvedFile file, FamilyLoadOptions options,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        CancellationToken ct = default);

    Task<FamilyLoadResult> LoadFamilySymbolAsync(
        string filePath, string typeName,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        CancellationToken ct = default);

    Task<FamilyLoadResult> ReloadFamilyPreservingLoadedTypesAsync(
        FamilyResolvedFile file, bool overwriteParameterValues,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        CancellationToken ct = default);
}
```

`ReloadFamilyPreservingLoadedTypesAsync` (Issue #101): перезагружает семейство,
сохраняя набор уже загруженных в проект типов (per-type `LoadFamilySymbol` внутри
одной `TransactionGroup`-сессии через `ITransactionService.BeginGroupSession` — I-03).
Параметр `nestedSharedNames` (добавлен вместе с аудит-харденингом): вызывающие,
которые блокируют метод синхронно внутри ExternalEvent-callback
(`StaleFamilyUpdater`, `.GetAwaiter().GetResult()`), **обязаны** предварительно
резолвить и передать список — это убирает единственный асинхронный (SQLite) await
из метода и гарантирует синхронное завершение на Revit main thread
(латентный deadlock: async-методы Microsoft.Data.Sqlite завершаются синхронно,
но контракт на это не полагается).

`onSharedDecision` вызывается один раз для каждого конфликтующего shared nested
(когда Revit сообщает `OnSharedFamilyFound`). Должен блокировать вызывающий поток
(Revit main thread) до ответа пользователя через WPF `ShowDialog`.

---
## ISharedNestedFamilyRepository

CRUD-репозиторий для имён общих вложенных семейств, персистленных в локальной
SQLite-БД каталога FM. Заполняется при импорте **внутри**
`IFamilyDataExtractionService.ExtractFromManagedFile` open-close цикла
(см. ADR-034 §2), читается при загрузке в проект. **V3 simplification**:
отдельный `ISharedNestedFamilyExtractor` (V2) удалён, чтобы не открывать
`.rfa` дважды.

**Файл:** `ISharedNestedFamilyRepository.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalSharedNestedFamilyRepository.cs`

```csharp
public interface ISharedNestedFamilyRepository
{
    Task ReplaceForVersionAsync(
        string catalogItemId, string versionId,
        IReadOnlyList<string> nestedSharedNames,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetNamesForCurrentVersionAsync(
        string catalogItemId,
        CancellationToken ct = default);
}
```

- `ReplaceForVersionAsync` — атомарно заменяет список имён для пары
  `(catalogItemId, versionId)`. Дедупликация case-insensitive.
- `GetNamesForCurrentVersionAsync` — возвращает имена для **текущей** версии
  (по `catalog_items.current_version_label`), отсортированные по `ordinal`.
  Пустой список если данных нет (legacy-каталог).

**Где вызывается из ViewModel:** `FamilyManagerMainViewModel.SaveSharedNestedNamesAsync`
(новый helper в `FamilyManagerMainViewModel.Extract.cs`) — после каждого
успешного `ExtractFromManagedFileAsync`. Helper-метод no-op при
`versionId == null` (legacy items) или `sharedNames.Count == 0`.

---

## IFamilyPlacementService

Размещение семейств и типоразмеров в проекте Revit. Все операции выполняются в контексте ExternalEvent (I-01).

**Файл:** `IFamilyPlacementService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyPlacementService.cs`

```csharp
public interface IFamilyPlacementService
{
    void ActivateAndPlaceType(string familyName, string typeName);
    void LoadAndPlaceFamily(string filePath, string familyName, string? preferredTypeName = null);
}
```

---

## IFamilyPlacementDragService

Инициирует нативную Revit drag-and-drop операцию для размещения типоразмера семейства. Реализация вызывает `UIApplication.DoDragDrop` с кастомным `IDropHandler`.

**Файл:** `IFamilyPlacementDragService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyPlacementDragService.cs`

```csharp
public interface IFamilyPlacementDragService
{
    void StartPlacementDrag(FamilyPlacementDragData data);
    event Action? PlacementCompleted;
    event Action<string>? PlacementFailed;
    event Action<string>? PlacementSucceeded;
    event Action<string>? PlacementStatusMessage;
    event Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? SharedFamilyDecisionRequested;
}
```

`SharedFamilyDecisionRequested` (issue #67) срабатывает когда drop-handler загружает
семейство с конфликтующим shared nested. Подписчик (FamilyManagerMainViewModel)
обязан вызвать WPF-диалог на Revit main thread и вернуть выбор пользователя.

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

## IFamilyDataImportRunRepository

CRUD для запусков импорта данных семейств (FamilyDataImportRun).

**Файл:** `IFamilyDataImportRunRepository.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyDataImportRunRepository.cs`

```csharp
public interface IFamilyDataImportRunRepository
{
    Task<FamilyDataImportRun?> GetLatestRunAsync(string catalogItemId, CancellationToken ct = default);
    Task<IReadOnlyList<FamilyDataImportRun>> GetRunsForItemAsync(string catalogItemId, CancellationToken ct = default);
    Task<FamilyDataImportRun> CreateRunAsync(FamilyDataImportRun run, CancellationToken ct = default);
    Task<FamilyDataImportRun> UpdateRunAsync(string runId, FamilyDataImportStatus status, int typesCount, DateTimeOffset completedAtUtc, string? errorMessage, CancellationToken ct = default);
}
```

---

## IFamilyDataImportService

Оркестрация импорта данных семейств: подготовка, извлечение, сохранение значений атрибутов.

**Файл:** `IFamilyDataImportService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/FamilyDataImportService.cs`

```csharp
public sealed record FamilyDataImportResult(
    bool Success,
    string? RunId,
    int TypesCount,
    int AttributesFoundCount,
    int AttributesMissingCount,
    string? ErrorMessage);

public sealed record FamilyExtractionPrepareResult(
    bool Success,
    FamilyCatalogItem? Item,
    string? ResolvedFilePath,
    IReadOnlyList<string> ParameterNames,
    string? ErrorMessage);

public interface IFamilyDataImportService
{
    Task<FamilyDataImportResult> ImportDataAsync(string catalogItemId, CancellationToken ct = default);
    Task<FamilyExtractionPrepareResult> PrepareExtractionAsync(string catalogItemId, int targetRevitVersion, CancellationToken ct = default);
    Task<FamilyDataImportResult> SaveExtractionResultAsync(string catalogItemId, FamilyExtractionResult extractionResult, string? versionId, string? fileId, CancellationToken ct = default);
}
```

---

## IDatabaseManager

Управление подключениями к базам данных каталога. Registry хранится в `%APPDATA%\SmartCon\FamilyManager\registry.json`.

**Файл:** `IDatabaseManager.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/DatabaseManager.cs`

```csharp
public interface IDatabaseManager
{
    Task InitializeAsync(CancellationToken ct = default);
    IReadOnlyList<DatabaseConnection> ListConnections();
    DatabaseConnection? GetActiveConnection();
    string? GetActiveDatabasePath();
    Task<DatabaseConnection> CreateDatabaseAsync(string name, string path, CancellationToken ct = default);
    Task<DatabaseConnection> CreateProjectDatabaseAsync(string name, string path, ProjectBaseBinding binding, CancellationToken ct = default);
    Task<DatabaseConnection> ConfigureProjectBaseAsync(string connectionId, ProjectBaseBinding binding, CancellationToken ct = default);
    Task<DatabaseConnection> ConnectDatabaseAsync(string path, CancellationToken ct = default);
    Task<bool> SwitchDatabaseAsync(string connectionId, CancellationToken ct = default);
    Task<bool> DisconnectDatabaseAsync(string connectionId, CancellationToken ct = default);
    Task<bool> DeleteDatabaseAsync(string connectionId, CancellationToken ct = default);
    event EventHandler<string>? ActiveDatabaseChanged;
}
```

---

## IRegistryMigrator

Мигратор `registry.json` FamilyManager между версиями схемы. Каждая версия —
небольшое аддитивное обновление (заполнение новых полей значениями по умолчанию
+ атомарная перезапись файла). Запускается один раз при старте плагина сразу
после инициализации `IDatabaseManager`, до того как UI начнёт читать реестр
(см. #119, decision A12).

**Файл:** `IRegistryMigrator.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/RegistryMigrator.cs`

```csharp
public interface IRegistryMigrator
{
    Task MigrateAsync(CancellationToken ct = default);
    int LatestSchemaVersion { get; }
}
```

---

## IProjectBaseBindingEvaluator

Чистый C#-evaluator привязки проектной базы к имени файла Revit. Делегирует парсинг
в `IFileNameParser` из Core.

**Файл:** `IProjectBaseBindingEvaluator.cs`  
**Реализация:** `SmartCon.Core/Services/Implementation/ProjectBaseBindingEvaluator.cs`

```csharp
public interface IProjectBaseBindingEvaluator
{
    ProjectBaseMatch Evaluate(ProjectBaseBinding? binding, string filePath);
}
```

Возвращает `NotApplicable` если `binding` равен `null`; `Match` при успешном парсинге
и прохождении валидации полей; `Mismatch` с пояснением при ошибке. См. ADR-045.

---

## IProjectBaseActivator

Сервис автоматической активации базы при смене активного документа Revit. Pure C#,
не обращается к Revit API напрямую — переключение выполняет `IDatabaseManager.SwitchDatabaseAsync`.

**Файл:** `IProjectBaseActivator.cs`  
**Реализация:** `SmartCon.Core/Services/Implementation/ProjectBaseActivator.cs`

```csharp
public interface IProjectBaseActivator
{
    Task<string?> ActivateForDocumentAsync(string currentFilePath, CancellationToken ct = default);
}
```

Алгоритм: перебрать проектные базы, активировать первую подходящую; иначе fallback
на первую общую базу. Если общих баз нет — возвращает `null`.

---

## IActiveDocumentChangeNotifier

Абстрация над Revit-событиями `ViewActivated`, `DocumentSaved` и `DocumentSavedAs`.
Core-контракт, реализация в `SmartCon.Revit/Events/ActiveDocumentChangeNotifier.cs`.

**Файл:** `IActiveDocumentChangeNotifier.cs`

```csharp
public interface IActiveDocumentChangeNotifier : IDisposable
{
    event EventHandler<ActiveDocumentChangedEventArgs>? ActiveDocumentChanged;
    event EventHandler<ActiveDocumentPathChangedEventArgs>? ActiveDocumentPathChanged;
}

public enum ActiveDocumentPathChangeReason
{
    Activated,
    Saved,
    SavedAs
}

public sealed record ActiveDocumentChangedEventArgs(string FilePath);
public sealed record ActiveDocumentPathChangedEventArgs(string FilePath, ActiveDocumentPathChangeReason Reason);
```

- `ActiveDocumentChanged` — сработал `ViewActivated` для несемейного, сохранённого документа (путь гарантированно не пустой).
- `ActiveDocumentPathChanged` — у активного документа изменился путь, пока он остаётся активным: первое сохранение нового проекта (`SavedAs`) или переименование через `SaveAs` (`SavedAs`), а также редкий случай сохранения без смены пути (`Saved`).

Подписчики (`FamilyManagerMainViewModel`) получают путь активного документа и
запускают `IProjectBaseActivator.ActivateForDocumentAsync`. Фильтрация unsaved/detached/family
документов, неактивных документов и отменённых/неудавшихся сохранений выполняется в реализации,
чтобы Core оставался чистым от Revit API. См. Issue #128.

---

## IFamilyManagerDialogService

UI-диалоги модуля FamilyManager.

**Файл:** `IFamilyManagerDialogService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/FamilyManagerDialogService.cs`

```csharp
public enum DialogResult
{
    None,
    OK,
    Cancel,
    Yes,
    No
}

public interface IFamilyManagerDialogService
{
    string? ShowOpenFileDialog(string title, string? initialDirectory = null);
    string? ShowImportDialog(string title, string? initialDirectory = null);
    string[]? ShowImportFilesDialog(string title, string? initialDirectory = null);
    string? ShowFolderBrowserDialog(string title, string? initialDirectory = null);
    void ShowWarning(string title, string message);
    void ShowError(string title, string message);
    string? ShowInputDialog(string title, string prompt, string defaultText = "");
    bool ShowConfirmation(string title, string message);
    DialogResult ShowYesNoCancel(string title, string message);
    bool? ShowCategoryTreeEditor(object viewModel);
    bool? ShowProjectBaseRulesEditor(object viewModel);
    string? ShowCategoryPicker(object viewModel);
    string? ShowOpenJsonDialog(string title, string? initialDirectory = null);
    string? ShowSaveJsonDialog(string title, string? defaultFileName = null);
    bool? ShowProperties(object viewModel);
    string? ShowAssetOpenFileDialog(string title, FamilyAssetType assetType, string? initialDirectory = null);
    bool? ShowPresetEditor(object viewModel);
    bool? ShowAttributeLibrary(object viewModel);
    bool? ShowProfile(object viewModel);
    bool? ShowBatchImportDialog(object viewModel);
    SharedFamiliesLoadChoice ShowSharedFamiliesLoadModeDialog(SharedFamilyDecisionRequest request);
}
```

`ShowSharedFamiliesLoadModeDialog` показывает диалог с 3 radio-button (issue #67)
и возвращает выбор пользователя. **Должен вызываться на Revit main thread.**
При отмене пользователем возвращает `SharedFamiliesLoadChoice.UseProject` как
безопасный дефолт.

---

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

---

## IFamilyManagerAwaitableEvent

Awaitable-обёртка над `Revit API ExternalEvent`. Позволяет коду из WPF/UI thread вызвать операцию в Revit API контексте и **дождаться её завершения через `await`**, а не городить `TaskCompletionSource` boilerplate в каждом VM.

**Файл:** `SmartCon.Core/Services/Interfaces/IFamilyManagerAwaitableEvent.cs`
**Реализация (pure C#, testable):** `SmartCon.FamilyManager/Events/FamilyManagerAwaitableEvent.cs`
**Адаптер к `IExternalEventHandler`:** `SmartCon.App/Events/RevitFamilyManagerAwaitableEvent.cs`

```csharp
public interface IFamilyManagerAwaitableEvent
{
    /// Поставить Action<object> в очередь, дождаться её выполнения в Revit-потоке.
    Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default);

    /// То же, но функция возвращает значение (generic-вариант).
    Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default);

    /// Вызывается IExternalEventHandler-адаптером в UI-потоке Revit: достаёт
    /// первый элемент из очереди, исполняет его и завершает ожидающий Task.
    void ProcessQueue(object revitUIApplication);
}
```

**Дизайн-контракт:**
- FIFO: элементы обрабатываются в порядке постановки в очередь.
- `RunContinuationsAsynchronously` — продолжение после `await` не блокирует UI-поток Revit.
- Исключения из callback пробрасываются в `Task` (наблюдаются через `await`).
- При `ct` после `RaiseAsync` — `Task` завершается как `Canceled`.
- **Не thread-safe для re-entrant вызовов** — один `Raise` должен полностью завершиться до следующего.
- `object` (а не `UIApplication`) сохраняет `SmartCon.Core` независимым от `RevitAPIUI` (I-09).
- Дополнительный overload `RaiseAsyncTask(Func<object, Task>, CancellationToken)` (Phase 4c) — для async delegate-ов. Использует `BridgeAsyncResult` (TaskCompletionSource + try/catch/OperationCanceledException). Continuation через `RunContinuationsAsynchronously`. **Отдельный name, не overload**, чтобы избежать implicit conversion C# statement lambda → `Func<object, Task>` ambiguity.

---

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

On-demand проверка актуальности семейств в активном проекте (ADR-030, Issue #69). Все проверки читают ES-маркер через `IFamilyVersionStore.ReadManyFromDocument` (batch, in-memory). `CheckCategoryAsync` обновляет сессионный снимок, `CheckFamilyAsync` — нет. `GetCachedSnapshot` / `InvalidateCache` — сессионный кеш (D-10, инвалидируется при Load/Update/Edit/смене БД).

**Файл:** `IStaleDetector.cs`

```csharp
public interface IStaleDetector
{
    Task<StaleCheckResult> CheckFamilyAsync(
        string catalogItemId,
        string familyName,
        Document doc,
        ElementId familyId,
        CancellationToken ct);

    Task<IReadOnlyList<StaleCheckResult>> CheckCategoryAsync(
        string? categoryId,
        bool recursive,
        Document doc,
        CancellationToken ct);

    FamilyStaleSnapshot? GetCachedSnapshot();
    void InvalidateCache();
}
```

---

## IStaleFamilyUpdater

Обновление семейств в активном проекте — перезагрузка текущей версии из каталога (ADR-030, Issue #69 AC). После успешного обновления пишет новый `FamilyVersion`-маркер через `IFamilyVersionStore`. Хост **обязан** вызвать `IStaleDetector.InvalidateCache()` после. `UpdateBatchAsync` обрабатывает семейства последовательно и отчитывается о прогрессе через `IProgress<>`.

**Файл:** `IStaleFamilyUpdater.cs`

```csharp
public interface IStaleFamilyUpdater
{
    Task<bool> UpdateFamilyAsync(
        string catalogItemId,
        bool overwriteParameterValues,
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

---

## IFamilyVersionWriter

Единая точка записи `FamilyVersion` маркера в ExtensibleStorage (ADR-030 Phase 24). Раньше дублировалось в `LoadPlace` (2 места) и `StaleFamilyUpdater` (1 место). Теперь — один helper.

**Файл:** `IFamilyVersionWriter.cs`

```csharp
public interface IFamilyVersionWriter
{
    Task WriteVersionMarkerAsync(
        string catalogItemId,
        string familyName,
        string? versionLabel,
        int targetRevit,
        CancellationToken ct);
}
```

**Реализация:** `FamilyVersionWriter` (в `SmartCon.FamilyManager/Services/Stale/`). Внутри — `RaiseAsyncTask` для `FindByName` + `WriteToLoadedFamily` на Revit main thread. No-op если семейство не загружено в проект (например, при `LoadFamilySymbol` без `LoadFamily`).

---

## IFamilyImportPrecomputer

v2.0.0: single-purpose сервис, аллоцирующий каноническую `PrecomputedImportTriple` для заданного `displayName` без файлового I/O. Выделен из `IFamilyImportService` для соблюдения SRP: импорт-pipeline (запись файлов + обновление БД) и trivial DB-lookup + path-математика, которую каждая строка batch dialog делает заранее, — это разные ответственности.

**Файл:** `IFamilyImportPrecomputer.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportPrecomputer.cs`

```csharp
public interface IFamilyImportPrecomputer
{
    Task<PrecomputedImportTriple?> BuildPrecomputedTripleAsync(
        string displayName,
        string extension,
        string? forcedCatalogItemId = null,
        CancellationToken ct = default);
}
```

Семантика:
- `forcedCatalogItemId` (Issue #126): когда дедуп-сервис сматчил строку к существующему айтему ПО ХЭШУ (возможно под другим именем), caller передаёт id этого айтема — triple целится в него (его next version label + managed path в ЕГО папке), а не в name-lookup. Иначе IncrementVersion писал бы файл в сиротскую GUID-папку и падал на UNIQUE constraint со стейл `v1`.
- Без `forcedCatalogItemId`: нормализует `displayName` через `FamilyNameNormalizer` и ищет existing item через `IFamilyCatalogProvider.FindByNormalizedNameAsync`.
- Если existing найден — возвращает `(existing.Id, ComputeNextVersionLabel(existing.Id), ComputeManagedFilePath(...))`: id сохраняется, версия инкрементируется (`vN → vN+1`).
- Если existing не найден — возвращает `(Guid.NewGuid() в формате "N", "v1", ComputeManagedFilePath(...))`.
- `extension` — расширение с ведущей точкой (`".rfa"` для loadable, `".rvt"` для system); `".rfa"` default для null/empty.
- Возвращает `null` когда активная БД не имеет `GetDatabaseRoot()` (caller обязан surfacing this как user error, не fallback на temp-папку — v2.0.0 не использует temp-папки, см. ADR-035).

Используется в **обоих** сценариях, где нужен precomputed triple:
- Initial dialog build (`BuildSystemFamilyBatchRowVirtualAsync` / `BuildLoadableFamilyBatchRowVirtualAsync` в `FamilyManagerMainViewModel.Import.cs`) — при первом построении списка строк.
- Dialog rename handler (`FamilyBatchImportViewModel.OnRowNameChanged`) — при переименовании пользователем строки в batch dialog.

Без единого источника истины эти два пути могут разойтись: `BuildSystem` использует `existingByName` из прямого lookup в `IFamilyCatalogProvider`, а `OnRowNameChanged` — `existing` из `IFamilyCatalogProvider` (тот же provider, но в другом контексте). Преcomputers заменяет оба на один вызов, гарантируя что `CatalogItemId`/`VersionLabel`/`ManagedPath` всегда согласованы и перевычисляются атомарно.

`OnRowNameChanged` дополнительно оборачивает вызов в `Task.Delay(250 ms)` debouncer, чтобы DB-lookup не срабатывал на каждое нажатие клавиши. Cancellation token отменяет предыдущий pending-вызов при следующем нажатии.

---

## IFamilySnapshotExtractor

Extracts structured snapshots from open Revit documents for content-hash computation. All methods must be called on the Revit UI thread (I-01) — the caller is responsible for marshalling via `IFamilyManagerAwaitableEvent.RaiseAsync`.

**Файл:** `Services/Interfaces/IFamilySnapshotExtractor.cs`

```csharp
public interface IFamilySnapshotExtractor
{
    FamilySnapshot ExtractFromFamilyDocument(Document familyDoc);
    SystemFamilySnapshot ExtractFromProject(
        Document projectDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory builtInCategory);
}
```

- `ExtractFromFamilyDocument` — extracts a `FamilySnapshot` (parameters, types, values, geometry, shared nested names) from an open family document. The document must be a family document (`IsFamilyDocument == true`).
- `ExtractFromProject` — extracts a `SystemFamilySnapshot` (category + types + parameter values) from an open project document.

**Caller contract:** the active document may be the source project or a managed-storage mini-rvt (after `EditFamily` + `SaveAs`). The extracted hash is stable across both because it is based on in-memory content, not file bytes.

---

## IFamilyContentHasher

Computes a stable `FamilyContentHash` from a snapshot. Pure C# — no Revit API calls. The hash is a SHA-256 of a canonical string built from the snapshot data. Stable across SaveAs, rename, and Revit upgrade because it is based on in-memory content, not file bytes.

**Файл:** `Services/Interfaces/IFamilyContentHasher.cs`

```csharp
public interface IFamilyContentHasher
{
    FamilyContentHash? ComputeForLoadable(FamilySnapshot snapshot);
    FamilyContentHash? ComputeForSystem(SystemFamilySnapshot snapshot);
}
```

- `ComputeForLoadable` — returns `null` if the snapshot is null or empty (no parameters, no types, no geometry).
- `ComputeForSystem` — returns `null` if the snapshot is null or has no types.

**v2.0.0 stability rules:**
- Blank parameter values are excluded from the canonical string (`HasValue=false`, empty string, `INVALID`, `UNSUPPORTED`, `READERROR`). Numeric zero is meaningful (e.g. IFC=0).
- The auto-generated `Код IfcGUID` parameter is excluded because Revit regenerates it on every `.rvt` save — including it would make identical content produce different hashes across source project and mini-rvt.
- Parameter values are sorted by parameter name for deterministic output.

---

## IContentHashDedupService

Content-hash dedup service. Combines the name-based lookup with the cross-version hash search to produce the final `FamilyBatchImportStatus` for a batch-import row.

**Файл:** `Services/Interfaces/IContentHashDedupService.cs`

```csharp
public interface IContentHashDedupService
{
    Task<ContentHashDedupResult> CheckAsync(
        string normalizedName,
        FamilyContentHash? contentHash,
        string familySource,
        CancellationToken ct = default);
}
```

**Business rules (Issue #126, hash-first):**
- Хэш совпал с любой версией (current или archived) ЛЮБОГО айтема каталога, независимо от имени → `Duplicate`. Найденный по хэшу айтем — канонический «existing» для MakeActive/IncrementVersion. Если его нормализованное имя отличается от имени строки — `IsCrossNameDuplicate = true` (batch-диалог рисует ⚠ с tooltip).
- Хэш не совпал (или хэша нет) и имя есть в каталоге → `Existing`.
- Хэш не совпал (или хэша нет) и имени нет в каталоге → `New`.
- Конфликт имя/контент (хэш совпал с айтемом A, имя занято другим айтемом B): контент важнее — Duplicate к A, Warn в лог, действие по умолчанию Skip.
- Cross-source separation: `"loadable"` hashes are never compared against `"system"` hashes and vice versa.

---

## ICatalogActualizationService

**Единый** сервис актуализации БД (ADR-054): движок, который union'ит детекты всех зарегистрированных `IDatabaseActualizationTask`, открывает каждую pending-семью ровно один раз (snapshot + геометрия в одной сессии через `IFamilyMigrationExtractor.ExtractLoadableWithGeometryAsync`; staged system `.rvt` — category-only через `ExtractSystemCategoryAsync`, диспатч по расширению managed-файла) и применяет только pending-задачи. Группы грузятся для `family_source IN ('loadable','system')` — system-группы инертны для задач с loadable-scope детектом. Новые extraction-time фичи = новый класс-задача — движок, диалог, гейт, resume и purge бесплатны.

**Файл:** `Services/Interfaces/ICatalogActualizationService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Actualization/CatalogActualizationService.cs`

```csharp
public interface ICatalogActualizationService
{
    Task<DatabasePendingBreakdown> CountPendingBreakdownAsync(
        int revitMajorVersion, CancellationToken ct = default);
    Task<DatabaseMigrationResult> RunAllPendingAsync(
        int revitMajorVersion,
        IProgress<DatabaseMigrationProgress>? progress,
        CancellationToken ct = default);
    Task<(int DeletedItems, int DeletedVersions, int FailedDirectories)> PurgeMissingAsync(
        IReadOnlyList<HashRecalculationMissingFile> missing,
        CancellationToken ct = default);
}
```

Алгоритм `RunAllPendingAsync`:
1. File-free passes задач (работа без файлов, напр. system re-flag хэша).
2. Детекты задач → union ключей `catalogItemId|versionLabel`; группы загружаются одним запросом, openable-вариант — наивысший `revit_major_version` ≤ запущенного Revit; newer-only группы — счётчик в сводке.
3. По группе: файл не найден → missing + `HandleGroupFailureAsync(MissingFile)` задач; не прочитался → failed + `HandleGroupFailureAsync(ExtractionFailed)`; иначе один open → `ApplyAsync` pending-задач по `Order` (сбой одной задачи не мешает остальным — её артефакт остаётся pending).
4. `PurgeMissingAsync` — по подтверждению пользователя удаляет записи о недоступных файлах: versions (FK CASCADE чистит types/attributes/nested), file records, items без версий; если удалённая версия была активной — active переключается на новейшую оставшуюся с ресинком хэша и имени.

---

## IDatabaseActualizationTask

Контракт одного «запроса на обновление» (задачи) движка актуализации (ADR-054, `docs/architecture/database-migrations.md`). Задачи обнаруживаются через DI (`IEnumerable<IDatabaseActualizationTask>`). CRITICAL задачи с pending > 0 поднимают баннер + красный badge и гейтят все write-операции; OPTIONAL — влияют только на видимость команды «Обновить базу» (Owner/BimMaster).

**Файл:** `Services/Interfaces/IDatabaseActualizationTask.cs`
**Реализации:** `HashFormatActualizationTask` (`hash-v2`, Order=10, critical), `AttributesActualizationTask` (`attributes-v1`, Order=20, optional), `GlbPreviewActualizationTask` (`glb-v1`, Order=30, optional) — `SmartCon.FamilyManager/Services/Actualization/`.

```csharp
public interface IDatabaseActualizationTask
{
    string Id { get; }
    int Order { get; }
    bool IsCritical { get; }
    Task<int> CountPendingAsync(int revitMajorVersion, CancellationToken ct = default);
    Task<NewerOnlyPendingInfo> GetNewerOnlyPendingAsync(int revitMajorVersion, CancellationToken ct = default);
    Task<IReadOnlyCollection<string>> LoadPendingGroupKeysAsync(int revitMajorVersion, CancellationToken ct = default);
    Task<int> RunFileFreePassAsync(int revitMajorVersion, CancellationToken ct = default);
    Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default);
    Task HandleGroupFailureAsync(ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct = default);
}
```

- `CountPendingAsync` — дешёвый SQL COUNT processable групп (фильтр Revit) для badge/меню; без побочных эффектов.
- `GetNewerOnlyPendingAsync` (ADR-054 §3a) — pending-группы БЕЗ единого openable-варианта + минимальный Revit, в котором они все обновятся за раз (`RequiredRevitVersion` = MAX over groups of MIN(variant revit)); openability считается по ВСЕМ вариантам группы (apply идёт на все). CRITICAL newer-only гейтит (база read-only до идеальной миграции), OPTIONAL — только янтарный индикатор.
- `LoadPendingGroupKeysAsync` — ключи `itemId|versionLabel` ВСЕХ pending-групп (любой Revit — движок классифицирует openability).
- `RunFileFreePassAsync` — мгновенная работа без файлов (0 для большинства; у hash — system re-flag).
- `ApplyAsync` — записывает своё из готового `FamilyActualizationContext` (snapshot+geometry из одного open'а); идемпотентен; короткие транзакции (I-14); записанный артефакт обязан погасить свой детект (возобновляемость).
- `HandleGroupFailureAsync` — задача сама решает семантику терминальных маркеров (hash: -2 missing / -1 unreadable; attributes/glb: no-op → ретрай при следующем запуске).
- Scope (active-only vs все версии, system vs loadable) — собственность детекта задачи.

---

## IFamilyMigrationExtractor

Revit-bound граница движка актуализации (ADR-054): открывает ОДИН managed family-файл на Revit main thread, извлекает snapshot (+ per-type геометрию), закрывает документ. Реализация в SmartCon.Revit маршалит через `IFamilyManagerAwaitableEvent`; вызывающий `ICatalogActualizationService` остаётся pure C# и юнит-тестируемым с fake-экстрактором.

**Файл:** `Services/Interfaces/IFamilyMigrationExtractor.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyMigrationExtractor.cs`

```csharp
public interface IFamilyMigrationExtractor
{
    Task<FamilyMigrationExtractResult> ExtractLoadableAsync(
        string absolutePath,
        CancellationToken ct = default);
    Task<FamilyMigrationExtractResult> ExtractLoadableWithGeometryAsync(
        string absolutePath,
        CancellationToken ct = default);
    Task<FamilyMigrationExtractResult> ExtractSystemCategoryAsync(
        string absolutePath,
        CancellationToken ct = default);
}
```

- Открывает `.rfa` через `OpenDocumentFile`, извлекает `FamilySnapshot`, закрывает без сохранения. Не бросает через границу — ошибки в результате.
- `ExtractLoadableWithGeometryAsync` (ADR-054) — та же open→extract→close сессия плюс per-type геометрия (`FamilyMigrationExtractResult.Geometry`): файл открывается ровно один раз. Ошибка геометрии НЕ роняет результат — snapshot остаётся валидным, `Geometry` = `null` (caller делает fallback на отдельный проход геометрии).
- `ExtractSystemCategoryAsync` — для staged system `.rvt` (проектный документ): извлекает ТОЛЬКО display name Revit-категории. Детект идёт через `SystemCategoryRegistry` — канонический whitelist системных категорий продукта (тот же, что у `AnalyzeActiveProject`): сначала по размещённым инстансам (доменная истина мини-проекта — типоразмерами в БД становится только выставленное на виде), fallback — по скопированным типам для Phase-2 категорий, которые копируются без размещения (`placed=0`: перекрытия/крыши/лестницы/...). Дефолтный контент чистого проекта (уровни, виды, материалы, импосты) исключён конструктивно — его нет в реестре. Ничего не найдено → Ok с пустой категорией (задача пишет терминальный `''` маркер, без вечного retry). Возвращает минимальный `FamilySnapshot` — единая форма контекста движка. Движок диспатчит по расширению managed-файла (`.rvt` → system, `.rfa` → loadable).
- После каждого Close — `IUiFreezeRecoveryService.Nudge(" ")` (workaround #96: DockablePane freeze после циклов OpenDocumentFile+Close, REVIT-236376/237190).

---

## IDatabaseUpdateStateService

Разделяемое singleton-состояние «база требует обновления» (`docs/architecture/database-migrations.md`, Issue #126). MainViewModel обновляет его после инициализации и на каждом переключении базы; любая VM модуля блокирует write-операции через `EnsureUpToDateAsync()`. Пока `IsUpdateRequired` — база read-only: импорт (файлы/активный файл/выделенные), загрузка в проект, редактирование/удаление семейств, управление версиями, ассеты и редактор категорий гейтятся диалогом с предложением обновить.

**Файл:** `Services/Interfaces/IDatabaseUpdateStateService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Migrations/DatabaseUpdateStateService.cs`

```csharp
public interface IDatabaseUpdateStateService
{
    bool IsUpdateRequired { get; }
    int PendingCount { get; }
    int OptionalPendingCount { get; }
    int NewerOnlyCriticalCount { get; }
    int NewerOnlyRequiredRevitVersion { get; }
    int NewerOnlyPendingCount { get; }
    bool IsRunning { get; }
    event EventHandler? StateChanged;
    Task RefreshAsync(int revitMajorVersion, CancellationToken ct = default);
    void Reset();
    Task<bool> EnsureUpToDateAsync();
    Task UpdateAsync();
}
```

- `RefreshAsync` — пересчёт через движок `ICatalogActualizationService` (`DatabasePendingBreakdown`); `Reset` — при отсутствии активной БД.
- `IsUpdateRequired` (ADR-054 §3a) — ЛЮБОЙ critical pending: processable (`PendingCount`) ИЛИ newer-only (`NewerOnlyCriticalCount`) — база read-only до идеальной миграции.
- `EnsureUpToDateAsync` — gate: read-only роль (Engineer) → styled info «обновление выполнит Owner/BIM-мастер» (без оффера — запись бы упала); processable critical → диалог «Обновить сейчас?»; только newer-critical → предупреждение с `NewerOnlyRequiredRevitVersion` (оффера нет — здесь не починить).
- `UpdateAsync` — ЕДИНЫЙ прогон движка через единый диалог (команда «Обновить базу», ADR-054); ошибки логируются, закоммиченные записи сохраняются.
- `OptionalPendingCount` (ADR-054) — processable pending OPTIONAL задач; влияет только на видимость команды «Обновить базу», никогда не гейтит write-операции.
- `NewerOnlyPendingCount` (ADR-054 §3a) — OPTIONAL newer-only записи; только янтарный индикатор, база остаётся рабочей.

---

## IFamilyGeometryExtractor

Extracts tessellated 3D geometry from a managed `.rfa` file (ADR-042). Implementations MUST run on the Revit UI thread (I-01) because `OpenDocumentFile` / `element.get_Geometry(Options)` / `Face.Triangulate()` are all Revit API calls — callers marshal via `IFamilyManagerAwaitableEvent.RaiseAsync<T>`.

**Файл:** `Services/Interfaces/IFamilyGeometryExtractor.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyGeometryExtractor.cs`

```csharp
public interface IFamilyGeometryExtractor
{
    Task<FamilyGeometryPreview?> ExtractAsync(
        string managedRfaPath,
        string catalogItemId,
        string versionLabel,
        CancellationToken ct = default);
}
```

**Контракт:**
- Returns `FamilyGeometryPreview` with at least one mesh, or `null` when the family has no visible geometry / an error occurred (logged by the implementation, NOT rethrown — the pipeline treats `null` as "skip GLB write").

---

## IGlbWriter

Serializes a `FamilyGeometryPreview` to a GLB (binary glTF 2.0) file. Pure C# implementation (SharpGLTF.Toolkit) — no Revit API, no WPF (I-09), unit-testable without a Revit process.

**Файл:** `Services/Interfaces/IGlbWriter.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Geometry/FamilyGeometryGlbWriter.cs`

```csharp
public interface IGlbWriter
{
    Task<bool> WriteAsync(
        FamilyGeometryPreview preview,
        string outputPath,
        CancellationToken ct = default);
}
```

**Контракт:**
- Creates the parent directory if it does not exist. Overwrites the file if it already exists.
- Returns `true` on success; `false` on failure (logged internally, not rethrown — pipeline treats `false` as "skip asset registration").

---

## IFamilyGeometryPipeline

Coordinates the end-to-end 3D geometry preview pipeline triggered from `LocalFamilyImportService` hooks H1/H2/H3 (ADR-042): extract → write GLB → delete previous auto-extracted asset → register new asset.

**Файл:** `Services/Interfaces/IFamilyGeometryPipeline.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Geometry/FamilyGeometryPipeline.cs`

```csharp
public interface IFamilyGeometryPipeline
{
    Task RunAsync(
        string managedRfaPath,
        string catalogItemId,
        string versionId,
        string versionLabel,
        string familyName,
        CancellationToken ct = default);
}
```

**Контракт:**
- Safe to invoke from any thread — internally marshals Revit API calls to the UI thread via `IFamilyManagerAwaitableEvent`.
- Implementations MUST swallow all exceptions and log a Warn with an `[Action: ...]` suggestion (skill smartcon-logging L9) — geometry preview is a nice-to-have and MUST NOT break the import transaction that already committed before the hook was reached.

---

## IFamilyBatchImportExecutor (Issue #127)

Точка входа выполнения batch-импорта после подтверждения диалога. Реализации: `FileFamilyBatchImportExecutor` (UC-1, импорт .rfa) и `ProjectFamilyBatchImportExecutor` (UC-3/UC-4, импорт из проекта). Поэлементный цикл stage → import → extract с отчётами через `IProgress<FamilyBatchImportProgress>` и cooperative-паузой через `PauseGate`. Отмена — между элементами (текущий дорабатывает); `WasStopped` в результате.

**Файл:** `IFamilyBatchImportExecutor.cs`
**Реализации:** `SmartCon.FamilyManager/Services/Import/FileFamilyBatchImportExecutor.cs`, `ProjectFamilyBatchImportExecutor.cs`

```csharp
public interface IFamilyBatchImportExecutor
{
    Task<FamilyBatchImportExecutionResult> ExecuteAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        string? categoryId,
        IProgress<FamilyBatchImportProgress>? progress,
        PauseGate? pauseGate,
        CancellationToken ct);
}
```

---

## IFileFamilyStagingService (Issue #127)

Staging UC-1: `SaveAs` held-open документа (открытого в Phase 1 Prepare) в precomputed managed path. Весь Revit API — только внутри `IFamilyManagerAwaitableEvent.RaiseAsync`. Ошибка staging не фатальна — возвращается исходный item (fallback на copy/bake внутри импорта).

**Файл:** `IFileFamilyStagingService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Import/FileFamilyStagingService.cs`

```csharp
public interface IFileFamilyStagingService
{
    Task<FamilyBatchImportItem> StageAsync(FamilyBatchImportItem item, CancellationToken ct);
    Task CloseAllPreparedDocumentsAsync(CancellationToken ct);
}
```

---

## IProjectFamilyStagingService (Issue #127)

Staging UC-3/UC-4: system — создание mini-.rvt через `CreateCleanProjectWithTypesAndInstances`; loadable — `EditFamily` + `SaveAs` в managed storage (или `SaveAs` из held-open документа Prepare-фазы). `null` из Stage* — staging не удался, элемент помечается Error, цикл продолжается.

**Файл:** `IProjectFamilyStagingService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Import/ProjectFamilyStagingService.cs`

```csharp
public interface IProjectFamilyStagingService
{
    Task<FamilyBatchImportItem?> StageSystemAsync(FamilyBatchImportItem item, CancellationToken ct);
    Task<FamilyBatchImportItem?> StageLoadableAsync(FamilyBatchImportItem item, CancellationToken ct);
    Task CloseAllPreparedDocumentsAsync(CancellationToken ct);
}
```

---

## IFamilyImportPreparationService (Issue #127)

Тестовый шов (seam) над `FamilyImportPreparationService` (sealed) — только методы, нужные staging-сервисам: доступ к held-open документам и их закрытие. `Document` — opaque parameter (I-09), вызовы методов документа — только внутри `RaiseAsync` в реализациях staging.

**Файл:** `IFamilyImportPreparationService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/FamilyImportPreparationService.cs`

```csharp
public interface IFamilyImportPreparationService
{
    Task CloseAllPreparedDocumentsAsync(CancellationToken ct = default);
    Document? GetOpenedDocument(string sourcePath);
    void CloseAndRelease(string sourcePath);
}
```
