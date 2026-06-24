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
    Task<FamilyFileRecord?> GetFileAsync(string fileId, CancellationToken ct = default);
    Task<int> GetItemCountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<int>> GetAvailableRevitVersionsAsync(string catalogItemId, CancellationToken ct = default);
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
    Task<FamilyCatalogItem> UpdateItemAsync(string id, string? name, string? description, string? category, IReadOnlyList<string>? tags, ContentStatus? status, CancellationToken ct = default);
    Task<bool> DeleteItemAsync(string id, CancellationToken ct = default);
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
}
```

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

**Файл:** `IFamilyAssetService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyAssetService.cs`

```csharp
public interface IFamilyAssetService
{
    Task<FamilyAsset> AddAssetAsync(string catalogItemId, string? versionLabel, FamilyAssetType assetType, string sourceFilePath, string? description, CancellationToken ct = default);
    Task<IReadOnlyList<FamilyAsset>> GetAssetsAsync(string catalogItemId, string? versionLabel = null, CancellationToken ct = default);
    Task<bool> DeleteAssetAsync(string assetId, CancellationToken ct = default);
    Task<string?> ResolveAssetPathAsync(string assetId, CancellationToken ct = default);
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
}
```

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
    IReadOnlyList<DatabaseConnection> ListConnections();
    DatabaseConnection? GetActiveConnection();
    string? GetActiveDatabasePath();
    Task<DatabaseConnection> CreateDatabaseAsync(string name, string path, CancellationToken ct = default);
    Task<DatabaseConnection> ConnectDatabaseAsync(string path, CancellationToken ct = default);
    Task<bool> SwitchDatabaseAsync(string connectionId, CancellationToken ct = default);
    Task<bool> DisconnectDatabaseAsync(string connectionId, CancellationToken ct = default);
    Task<bool> DeleteDatabaseAsync(string connectionId, CancellationToken ct = default);
    event EventHandler<string>? ActiveDatabaseChanged;
}
```

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
    bool? ShowMetadataEdit(object viewModel);
    string? ShowInputDialog(string title, string prompt, string defaultText = "");
    bool ShowConfirmation(string title, string message);
    DialogResult ShowYesNoCancel(string title, string message);
    bool? ShowCategoryTreeEditor(object viewModel);
    string? ShowCategoryPicker(object viewModel);
    string? ShowOpenJsonDialog(string title, string? initialDirectory = null);
    string? ShowSaveJsonDialog(string title, string? defaultFileName = null);
    bool? ShowProperties(object viewModel);
    string? ShowAssetOpenFileDialog(string title, FamilyAssetType assetType, string? initialDirectory = null);
    bool? ShowPresetEditor(object viewModel);
    bool? ShowAttributeLibrary(object viewModel);
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

